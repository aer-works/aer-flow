using Aer.Adapters;
using Aer.Cli;
using Aer.Flow;
using Aer.Flow.Artifacts;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using System.Linq;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Aer.Ui.Tests")]

namespace Aer.Ui;

public partial class MainWindow : Window
{
    private const string ArtifactsDirectoryName = "artifacts";
    private const int MaxArtifactPreviewLength = 8000;

    private readonly LocalUiConfigurationStore _configurationStore;
    private readonly IReadOnlyDictionary<string, IWorkerAdapter> _adapters;
    private readonly DispatcherTimer _liveRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _currentTaskDirectoryPath;
    private bool _lastLoadSucceeded;
    private WorkflowStatus? _lastWorkflowStatus;
    private WorkflowDefinitionSnapshot? _lastSnapshot;

    /// <summary>
    /// The caller-retained delivery point (M15 Phase 4, issue #140) for whichever Run or Decide pump
    /// this window itself currently has in flight — <see langword="null"/> whenever this process is
    /// not hosting one. A targeted Cancel on an execution registered here is delivered in-process via
    /// <see cref="InFlightExecutionRegistry.RequestCancellationAsync"/>, never a second mutation-surface
    /// call, since §15's guard is already held for this call's entire duration (M10's decision of
    /// record). Set immediately before <see cref="RunAsync"/>/<see cref="DecideAsync"/> starts its
    /// pump and cleared in that same call's <c>finally</c>.
    /// </summary>
    private InFlightExecutionRegistry? _currentInFlightExecutions;

    /// <summary>
    /// The host-stop token source for whichever pump this window currently has in flight (issue #140)
    /// — cancelling it is the Ctrl+C equivalent <c>Aer.Cli</c>'s <c>Program.cs</c> wires to
    /// <c>Console.CancelKeyPress</c>, reused here for <see cref="StopAsync"/> and
    /// <see cref="OnClosing"/>. Linked to whatever <see cref="CancellationToken"/> the caller passed
    /// into <see cref="RunAsync"/>/<see cref="DecideAsync"/> (tests use this for their own cleanup),
    /// so either source firing reaches the pump.
    /// </summary>
    private CancellationTokenSource? _currentHostStopSource;

    /// <summary>
    /// The background <see cref="Task.Run(Func{Task})"/> task driving whichever pump is currently in
    /// flight — retained so <see cref="OnClosing"/> can wait for it to reach a durable fixed point
    /// before actually closing the window, rather than abandoning it mid-write (issue #140).
    /// </summary>
    private Task? _currentPumpTask;

    /// <summary>Set once <see cref="OnClosing"/> has already stopped an in-flight pump and is closing the window for real — prevents re-entering the stop sequence from the follow-up programmatic <see cref="Window.Close()"/>.</summary>
    private bool _closeConfirmed;

    /// <summary>Test-only observation of the live-refresh polling state (see <see cref="UpdateLiveRefreshTimer"/>) — never consulted by production code.</summary>
    internal bool IsLiveRefreshTimerEnabled => _liveRefreshTimer.IsEnabled;

    /// <summary>
    /// This window's ViewModel (M15 Phase 2, issue #138) — set as <see cref="Window.DataContext"/> so
    /// <see cref="MainWindow.axaml"/> can bind the paused-step decision surface and the shared
    /// mutation-in-flight flag directly. See <see cref="MainWindowViewModel"/>'s own remarks for why
    /// this is scoped to that surface only, not the rest of the window's rendering.
    /// </summary>
    internal MainWindowViewModel ViewModel { get; } = new();

    public MainWindow()
        : this(LocalUiConfigurationStore.CreateDefault(), WorkerAdapterRegistry.Default)
    {
    }

    /// <summary>
    /// Takes the recents store as a constructor argument, never constructing
    /// <see cref="LocalUiConfigurationStore.CreateDefault"/> unconditionally, so a test can point it
    /// at a temp file instead of the real per-user config directory — the same "production wiring
    /// is the caller's decision" seam <c>Aer.Cli</c>'s <c>RunCommand</c> established for the adapter
    /// registry (M11 Phase 3).
    /// </summary>
    public MainWindow(LocalUiConfigurationStore configurationStore)
        : this(configurationStore, WorkerAdapterRegistry.Default)
    {
    }

    /// <summary>
    /// Takes the worker-adapter registry as a constructor argument too (M15 Phase 1, issue #137) —
    /// the same production-wiring-is-the-caller's-decision seam as <paramref name="configurationStore"/>
    /// and, before this window existed, <c>RunCommand.ExecuteAsync</c>'s own adapter-registry
    /// parameter (M11 Phase 3). Defaults to <see cref="WorkerAdapterRegistry.Default"/> via the
    /// other two constructors so production callers never have to name it explicitly, while
    /// <c>Aer.Ui.Tests</c> can substitute a deterministic shell-stub registry instead of resolving a
    /// live vendor CLI.
    /// </summary>
    public MainWindow(LocalUiConfigurationStore configurationStore, IReadOnlyDictionary<string, IWorkerAdapter> adapters)
    {
        InitializeComponent();
        DataContext = ViewModel;
        _configurationStore = configurationStore;
        _adapters = adapters;

        _liveRefreshTimer.Tick += (_, _) => _ = RefreshAsync();
        OpenButton.Click += (_, _) => _ = OpenAsync(TaskDirectoryPathBox.Text ?? string.Empty);
        RefreshButton.Click += (_, _) => _ = RefreshAsync();
        CompareButton.Click += (_, _) => _ = CompareToTemplateAsync(TemplateComparePathBox.Text ?? string.Empty);
        RunButton.Click += (_, _) => _ = RunAsync(
            TaskDirectoryPathBox.Text ?? string.Empty, WorkflowTemplatePathBox.Text, BindingsFilePathBox.Text ?? string.Empty);
        StopButton.Click += (_, _) => _ = StopAsync();
        Closed += (_, _) => _liveRefreshTimer.Stop();
        Closing += OnClosing;
    }

    /// <summary>
    /// Populates <see cref="RecentsPanel"/> from Local UI Configuration (UI spec §3.1), plus (M15
    /// Phase 1) pre-fills the Run action's bindings/template inputs from whatever was last
    /// remembered — call once at startup.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RefreshRecentsPanelAsync(cancellationToken);

        BindingsFilePathBox.Text = await _configurationStore.LoadLastBindingsFilePathAsync(cancellationToken);
        WorkflowTemplatePathBox.Text = await _configurationStore.LoadLastWorkflowTemplateFilePathAsync(cancellationToken);
    }

    /// <summary>
    /// The full "open a task directory" action (UI spec §3.1): loads and renders it via
    /// <see cref="LoadAsync"/>, then — only on success — records it as the most recently opened
    /// directory and starts/stops live re-projection (M14 Phase 2, issue #119) depending on whether
    /// the projected workflow has reached a terminal state. This is what <see cref="OpenButton"/>
    /// and a click on a <see cref="RecentsPanel"/> entry both call; <see cref="App"/>'s CLI-argument
    /// launch path calls it too, so a directory opened that way is remembered exactly like one
    /// opened by hand.
    /// <para>
    /// If <paramref name="taskDirectoryPath"/> names a file rather than a directory, it is opened as
    /// a raw <c>WorkflowDefinition</c> template instead (M14 Phase 3, issue #120: the DAG view
    /// renders both bound tasks and not-yet-instantiated templates). A template is not a task —
    /// there is no execution state to remember a re-projection cadence for, so it is neither
    /// recorded to <see cref="LocalUiConfigurationStore"/> (that store is task-directory recents
    /// specifically, per its Phase 2 decision of record) nor live-refreshed.
    /// </para>
    /// </summary>
    public async Task OpenAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        TaskDirectoryPathBox.Text = taskDirectoryPath;

        if (File.Exists(taskDirectoryPath) && !Directory.Exists(taskDirectoryPath))
        {
            _currentTaskDirectoryPath = null;
            await LoadTemplateAsync(taskDirectoryPath, cancellationToken);
            _liveRefreshTimer.Stop();
            return;
        }

        _currentTaskDirectoryPath = taskDirectoryPath;

        await LoadAsync(taskDirectoryPath, cancellationToken);

        if (_lastLoadSucceeded)
        {
            await _configurationStore.RecordOpenedAsync(taskDirectoryPath, cancellationToken);
            await RefreshRecentsPanelAsync(cancellationToken);
        }

        UpdateLiveRefreshTimer();
    }

    /// <summary>
    /// The mutation seam this phase exists to prove (issue #137): a Run action that either starts a
    /// fresh task from <paramref name="workflowTemplateFilePath"/> + <paramref name="bindingsFilePath"/>,
    /// or resumes an already-bound <paramref name="taskDirectoryPath"/> after a pause or stop — the
    /// same <c>RunCommand.ExecuteAsync</c> call <c>aer run</c> makes, reused in-process rather than
    /// spawning the installed binary (the seam decision this phase resolves). Bindings are never
    /// persisted in a task directory (M14 Phase 2's decision of record) and the template is only
    /// ever read on a fresh start (<see cref="RunOptions.WorkflowFilePath"/>'s own remarks), so both
    /// are asked for here rather than inferred — "ask, don't infer," the same discipline the recents
    /// list already follows for task-directory discovery (UI spec §3.1).
    /// <para>
    /// The pump itself runs on a background thread (<see cref="Task.Run(Func{Task})"/>): a live
    /// execution can take however long a real worker takes, and the UI thread must never await that
    /// directly. This method starts <see cref="MainWindow"/>'s existing 2-second poller
    /// (<see cref="_liveRefreshTimer"/>) immediately, before the pump even begins, so it is what
    /// renders progress for the run's entire duration — this method itself only touches projection
    /// controls once more, via <see cref="OpenAsync"/>, after the pump has already reached its
    /// fixed point.
    /// </para>
    /// </summary>
    public async Task RunAsync(
        string taskDirectoryPath, string? workflowTemplateFilePath, string bindingsFilePath, CancellationToken cancellationToken = default)
    {
        TaskDirectoryPathBox.Text = taskDirectoryPath;
        // Kept in sync here, not just read from at Run-button-click time, so a later decision
        // (M15 Phase 2, issue #138) — which reads this same box rather than a remembered field, the
        // same "ask, don't infer" discipline this box's own value already follows — has a bindings
        // path to use even when RunAsync was invoked directly (a test, or a future non-button caller)
        // rather than through the click handler that already reads this box.
        BindingsFilePathBox.Text = bindingsFilePath;
        _currentTaskDirectoryPath = taskDirectoryPath;

        var options = new RunOptions(
            string.IsNullOrWhiteSpace(workflowTemplateFilePath) ? null : workflowTemplateFilePath,
            bindingsFilePath,
            taskDirectoryPath);

        // M15 Phase 2 (#138): RunButton's enabled state is now bound to ViewModel.IsMutationInFlight
        // in MainWindow.axaml — the same flag gates the paused-step decision buttons — rather than
        // toggled directly, so a Run and a decision can never both be in flight from this process at
        // once (the underlying §15 lock could not support that regardless).
        ViewModel.IsMutationInFlight = true;
        RunStatusText.Text = "Running…";

        // Started before the pump itself even begins: flow.jsonl is read with FileShare.ReadWrite
        // and written with FileShare.Read (Aer.Flow.Store), so a concurrent poll while this call's
        // own background dispatch is mid-write is safe by construction — this is what renders
        // progress for however long a real dispatch takes, the same 2-second re-projection M14
        // Phase 2 already built, not something this phase reinvents.
        _liveRefreshTimer.Start();

        // M15 Phase 4 (issue #140): a fresh registry and a host-stop source linked to the caller's
        // own token, retained on this window for this call's whole duration so a targeted Cancel or
        // the Stop button can reach this exact pump while it is in flight — either token firing
        // reaches it, since MutationInterface's host-stop machinery races whatever token it is given.
        var inFlightExecutions = new InFlightExecutionRegistry();
        var hostStopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _currentInFlightExecutions = inFlightExecutions;
        _currentHostStopSource = hostStopSource;

        try
        {
            var pumpTask = Task.Run(
                () => RunCommand.ExecuteAsync(options, _adapters, inFlightExecutions, hostStopSource.Token), hostStopSource.Token);
            _currentPumpTask = pumpTask;
            await pumpTask.ConfigureAwait(true);

            RunStatusText.Text = string.Empty;

            if (!string.IsNullOrWhiteSpace(workflowTemplateFilePath))
            {
                await _configurationStore.RecordWorkflowTemplateFilePathAsync(workflowTemplateFilePath, cancellationToken);
            }

            await _configurationStore.RecordBindingsFilePathAsync(bindingsFilePath, cancellationToken);
        }
        catch (AerFlowException ex)
        {
            // Same in-window-message precedent as LoadAsync (M14 Phase 1): a GUI has no
            // stderr/exit-code convention to fail into, so a malformed template/bindings file or a
            // WorkflowLockedException from a competing pump renders here rather than crashing.
            _liveRefreshTimer.Stop();
            RunStatusText.Text = ex.Message;
            return;
        }
        finally
        {
            ViewModel.IsMutationInFlight = false;
            _currentInFlightExecutions = null;
            _currentHostStopSource = null;
            _currentPumpTask = null;
            hostStopSource.Dispose();
        }

        await OpenAsync(taskDirectoryPath, cancellationToken);
    }

    /// <summary>
    /// The paused-step decision surface (issue #138; extended for Phase 3's artifact-carrying
    /// decisions, issue #139): wraps <see cref="DecideCommand.ExecuteAsync"/> — the same static,
    /// adapter-registry-as-argument call <c>aer decide</c> makes — exactly like <see cref="RunAsync"/>
    /// wraps <c>RunCommand</c> (Phase 1). <paramref name="decisionType"/> is one of §7's four labels
    /// mapped 1:1 onto <see cref="DecisionType"/> (<see cref="PausedStepViewModel"/>'s four commands)
    /// — never a UI-invented decision type (UI spec §6).
    /// <para>
    /// When <paramref name="revisionFilePath"/> is non-null, this first runs the <c>aer supply</c>
    /// half of M12 Phase 3's two-call round trip — minting, populating, and settling a supplementary
    /// execution from <paramref name="supplementaryWorker"/>/<paramref name="supplementaryOutputName"/>
    /// — then passes its <see cref="ExecutionId"/> to <c>aer decide</c> as <c>SupplementaryExecutionId</c>.
    /// Both calls run under the same <see cref="MainWindowViewModel.IsMutationInFlight"/> window and
    /// the same single poller start, since together they are one user-facing action (Retry or Send
    /// back), not two.
    /// </para>
    /// Bindings are read from <see cref="BindingsFilePathBox"/>, the same box <see cref="RunAsync"/>
    /// already asks for — never inferred or persisted per task (M14 Phase 2's decision of record).
    /// </summary>
    private async Task DecideAsync(
        string taskDirectoryPath,
        StepId stepId,
        ExecutionId executionId,
        DecisionType decisionType,
        StepId? targetStepId,
        string? revisionFilePath,
        string? supplementaryWorker,
        string? supplementaryOutputName,
        CancellationToken cancellationToken = default)
    {
        ViewModel.DecisionStatusText = $"Deciding {stepId.Value}…";
        ViewModel.IsMutationInFlight = true;

        // Same reasoning as RunAsync (Phase 1): started before the pump itself begins, so the
        // existing 2-second poller renders progress for however long this decision's pump (and, when
        // a supplementary artifact rides it, the aer supply call ahead of it) takes to reach its next
        // fixed point.
        _liveRefreshTimer.Start();

        // M15 Phase 4 (issue #140): retained the same way RunAsync retains one, for the same reason
        // — a paused step whose Retry/Send-back decision dispatches a fresh process-bound attempt is
        // itself something a targeted Cancel or the Stop button must be able to reach mid-pump.
        var inFlightExecutions = new InFlightExecutionRegistry();
        var hostStopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _currentInFlightExecutions = inFlightExecutions;
        _currentHostStopSource = hostStopSource;

        try
        {
            ExecutionId? supplementaryExecutionId = null;

            if (revisionFilePath is not null)
            {
                var supplyOptions = new SupplyOptions(
                    taskDirectoryPath,
                    supplementaryWorker ?? string.Empty,
                    supplementaryOutputName ?? string.Empty,
                    revisionFilePath,
                    BindingsFilePathBox.Text ?? string.Empty);

                var supplyResult = await Task.Run(() => SupplyCommand.ExecuteAsync(supplyOptions, _adapters, hostStopSource.Token), hostStopSource.Token)
                    .ConfigureAwait(true);

                supplementaryExecutionId = supplyResult.ExecutionId;
            }

            var options = new DecideOptions(
                taskDirectoryPath,
                executionId.Value,
                decisionType,
                targetStepId,
                supplementaryExecutionId?.Value,
                BindingsFilePathBox.Text ?? string.Empty);

            var pumpTask = Task.Run(
                () => DecideCommand.ExecuteAsync(options, _adapters, inFlightExecutions, hostStopSource.Token), hostStopSource.Token);
            _currentPumpTask = pumpTask;
            await pumpTask.ConfigureAwait(true);

            ViewModel.DecisionStatusText = string.Empty;
        }
        catch (Exception ex) when (ex is AerFlowException or FileNotFoundException)
        {
            // Same in-window-message precedent as RunAsync/LoadAsync (M14 Phase 1): a
            // WorkflowLockedException from a competing external pump, an invalid decision
            // (ExternalDecisionValidator's §17.2 rules), or aer supply's FileNotFoundException for a
            // mistyped revision file path, all render here rather than crashing.
            _liveRefreshTimer.Stop();
            ViewModel.DecisionStatusText = ex.Message;
            return;
        }
        finally
        {
            ViewModel.IsMutationInFlight = false;
            _currentInFlightExecutions = null;
            _currentHostStopSource = null;
            _currentPumpTask = null;
            hostStopSource.Dispose();
        }

        await OpenAsync(taskDirectoryPath, cancellationToken);
    }

    /// <summary>
    /// Re-projects the currently open task directory in place (M14 Phase 2's change-observation
    /// requirement, issue #119) — a no-op if nothing has been opened yet. Public and directly
    /// awaitable for the same reason <see cref="LoadAsync"/> is (issue #118): a test can drive
    /// exactly one re-projection deterministically, rather than pumping the dispatcher and waiting
    /// on <see cref="_liveRefreshTimer"/>'s real elapsed-time tick, which is what actually calls this
    /// in production.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTaskDirectoryPath is null)
        {
            return;
        }

        await LoadAsync(_currentTaskDirectoryPath, cancellationToken);
        UpdateLiveRefreshTimer();
    }

    /// <summary>
    /// The seam this phase exists to prove (issue #118), reaching the screen: loads
    /// <paramref name="taskDirectoryPath"/> through <see cref="TaskProjectionLoader"/> and renders
    /// its per-step statuses as plain <see cref="TextBlock"/> rows — deliberately minimal, per
    /// Phase 1's exclusion of "any styling worth defending". Public and directly awaitable (rather
    /// than fired from the constructor or a <c>Loaded</c> event) so a test can drive it
    /// deterministically without pumping the dispatcher on a timer. Extended in Phase 2 (issue #119)
    /// to also render the fuller <see cref="TaskProjection.History"/> surface, but
    /// <see cref="StatusText"/>/<see cref="StepsPanel"/>'s own rendering is untouched from Phase 1.
    /// </summary>
    public async Task LoadAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var projection = await TaskProjectionLoader.LoadAsync(taskDirectoryPath, cancellationToken);

            // Unlike Aer.Flow's own library code, this continuation deliberately does not
            // ConfigureAwait(false): it touches UI controls below, which must happen back on
            // Avalonia's UI thread.
            StatusText.Text = $"Workflow status: {projection.State.Status}";
            StepsPanel.Children.Clear();
            foreach (var step in projection.State.Steps)
            {
                StepsPanel.Children.Add(new TextBlock { Text = $"{step.StepId}: {step.Status}" });
            }

            var statusByStepId = projection.State.Steps.ToDictionary(step => step.StepId, step => step.Status);
            RenderDag(projection.Snapshot.Steps, statusByStepId);

            RenderExecutionHistory(projection);
            RenderDecisions(projection);
            RenderSupplementaryExecutions(projection);
            RenderArtifactLineage(projection, taskDirectoryPath);
            RebuildPausedSteps(projection, taskDirectoryPath);
            RebuildRunningExecutions(projection, taskDirectoryPath);

            _lastLoadSucceeded = true;
            _lastWorkflowStatus = projection.State.Status;
            _lastSnapshot = projection.Snapshot;
        }
        catch (AerFlowException ex)
        {
            // A real GUI has no stderr/exit-code convention to fail into (Aer.Cli's Program.cs
            // boundary) — an invalid task directory or a malformed snapshot/event log renders as an
            // in-window message instead.
            StatusText.Text = ex.Message;
            StepsPanel.Children.Clear();
            DagCanvas.Children.Clear();
            HistoryPanel.Children.Clear();
            DecisionsPanel.Children.Clear();
            SupplementaryPanel.Children.Clear();
            LineagePanel.Children.Clear();
            ArtifactPreviewBox.Text = string.Empty;
            DiffPanel.Children.Clear();
            ViewModel.PausedSteps.Clear();
            ViewModel.DecisionStatusText = string.Empty;
            ViewModel.RunningExecutions.Clear();
            ViewModel.CancelStatusText = string.Empty;

            _lastLoadSucceeded = false;
            _lastWorkflowStatus = null;
            _lastSnapshot = null;
        }
    }

    /// <summary>
    /// Renders a raw, not-yet-instantiated <see cref="WorkflowDefinition"/> template's DAG
    /// (M14 Phase 3, issue #120; UI spec §5, §10) — the counterpart to <see cref="LoadAsync"/> for
    /// paths that name a file rather than a task directory. There is no <see cref="FlowState"/> to
    /// overlay (a template is not bound to any task, so it has never executed) and no execution
    /// history/decisions/supplementary-execution surface either — those are all per-task facts;
    /// only the graph itself is meaningful for a template.
    /// </summary>
    private async Task LoadTemplateAsync(string templateFilePath, CancellationToken cancellationToken)
    {
        try
        {
            var definition = await TemplateProjectionLoader.LoadAsync(templateFilePath, cancellationToken);

            StatusText.Text =
                $"Template: {definition.WorkflowTemplateId} v{definition.WorkflowTemplateVersion} " +
                $"({definition.Steps.Count} step(s)) — not a task, no execution state.";
            StepsPanel.Children.Clear();
            HistoryPanel.Children.Clear();
            DecisionsPanel.Children.Clear();
            SupplementaryPanel.Children.Clear();
            LineagePanel.Children.Clear();
            ArtifactPreviewBox.Text = string.Empty;
            DiffPanel.Children.Clear();
            ViewModel.PausedSteps.Clear();
            ViewModel.DecisionStatusText = string.Empty;
            ViewModel.RunningExecutions.Clear();
            ViewModel.CancelStatusText = string.Empty;

            RenderDag(definition.Steps, statusByStepId: null);

            _lastLoadSucceeded = true;
            _lastWorkflowStatus = null;
            _lastSnapshot = null;
        }
        catch (AerFlowException ex)
        {
            StatusText.Text = ex.Message;
            StepsPanel.Children.Clear();
            DagCanvas.Children.Clear();
            HistoryPanel.Children.Clear();
            DecisionsPanel.Children.Clear();
            SupplementaryPanel.Children.Clear();
            LineagePanel.Children.Clear();
            ArtifactPreviewBox.Text = string.Empty;
            DiffPanel.Children.Clear();
            ViewModel.PausedSteps.Clear();
            ViewModel.DecisionStatusText = string.Empty;
            ViewModel.RunningExecutions.Clear();
            ViewModel.CancelStatusText = string.Empty;

            _lastLoadSucceeded = false;
            _lastWorkflowStatus = null;
            _lastSnapshot = null;
        }
    }

    private static readonly IReadOnlyDictionary<StepStatus, IBrush> BackgroundByStatus = new Dictionary<StepStatus, IBrush>
    {
        [StepStatus.Pending] = Brushes.WhiteSmoke,
        [StepStatus.Running] = Brushes.LightSkyBlue,
        [StepStatus.Succeeded] = Brushes.LightGreen,
        [StepStatus.Failed] = Brushes.IndianRed,
        [StepStatus.Cancelled] = Brushes.LightGray,
        [StepStatus.Paused] = Brushes.Khaki,
        [StepStatus.Rejected] = Brushes.IndianRed,
    };

    private const double DagCellWidth = 170;
    private const double DagCellHeight = 90;
    private const double DagNodeWidth = 150;
    private const double DagNodeHeight = 56;

    /// <summary>
    /// Renders <see cref="DagLayoutEngine.Layout"/>'s result over <paramref name="steps"/> as boxes
    /// (one per step, positioned by <see cref="DagNode.Rank"/>/<see cref="DagNode.Column"/>) joined
    /// by lines (one per <see cref="DagEdge"/>): solid for an ordinary <c>DependsOn</c> dependency,
    /// dashed for a declared <c>PausePoint.SupersedeTargets</c> entry (UI spec §10; issue #120).
    /// <paramref name="statusByStepId"/> is <c>null</c> for a raw template — nothing to overlay — or
    /// populated from the bound task's <see cref="FlowState"/> for a real task directory; either way
    /// every node still renders, just without a status-derived background in the template case.
    /// </summary>
    private void RenderDag(IReadOnlyList<WorkflowStepDefinition> steps, IReadOnlyDictionary<StepId, StepStatus>? statusByStepId)
    {
        DagCanvas.Children.Clear();

        var layout = DagLayoutEngine.Layout(steps);
        if (layout.Nodes.Count == 0)
        {
            DagCanvas.Width = 0;
            DagCanvas.Height = 0;
            return;
        }

        var nodeByStepId = layout.Nodes.ToDictionary(node => node.StepId);

        foreach (var edge in layout.Edges)
        {
            var from = nodeByStepId[edge.From];
            var to = nodeByStepId[edge.To];

            var line = new Line
            {
                StartPoint = new Point(
                    from.Column * DagCellWidth + DagNodeWidth / 2,
                    from.Rank * DagCellHeight + DagNodeHeight),
                EndPoint = new Point(
                    to.Column * DagCellWidth + DagNodeWidth / 2,
                    to.Rank * DagCellHeight),
                Stroke = edge.IsSupersede ? Brushes.DarkOrange : Brushes.Gray,
                StrokeThickness = 1.5,
            };

            if (edge.IsSupersede)
            {
                line.StrokeDashArray = [4, 2];
            }

            DagCanvas.Children.Add(line);
        }

        foreach (var node in layout.Nodes)
        {
            var status = statusByStepId?.GetValueOrDefault(node.StepId);
            var background = status is { } knownStatus
                ? BackgroundByStatus.GetValueOrDefault(knownStatus, Brushes.WhiteSmoke)
                : Brushes.WhiteSmoke;

            var label = status is { } renderedStatus
                ? $"{node.StepId}\n{node.Worker}\n{renderedStatus}"
                : $"{node.StepId}\n{node.Worker}";

            if (node.HasPausePoint)
            {
                label += node.SupersedeTargets.Count > 0
                    ? $"\n[pause -> {string.Join(", ", node.SupersedeTargets)}]"
                    : "\n[pause]";
            }

            var border = new Border
            {
                Width = DagNodeWidth,
                Height = DagNodeHeight,
                Background = background,
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = label,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                },
            };

            Canvas.SetLeft(border, node.Column * DagCellWidth);
            Canvas.SetTop(border, node.Rank * DagCellHeight);
            DagCanvas.Children.Add(border);
        }

        var maxColumn = layout.Nodes.Max(node => node.Column);
        var maxRank = layout.Nodes.Max(node => node.Rank);
        DagCanvas.Width = (maxColumn + 1) * DagCellWidth;
        DagCanvas.Height = (maxRank + 1) * DagCellHeight;
    }

    /// <summary>
    /// Renders every step's full attempt history (not just the latest attempt
    /// <see cref="StepsPanel"/> already shows), plus its retry count, pause state, and declared
    /// <c>SupersedeTargets</c> — the read-model surface <see cref="Aer.Flow.Domain.FlowState"/>
    /// alone does not carry (issue #119).
    /// </summary>
    private void RenderExecutionHistory(TaskProjection projection)
    {
        HistoryPanel.Children.Clear();
        var stepDefinitionByStepId = projection.Snapshot.Steps.ToDictionary(step => step.StepId);

        foreach (var stepState in projection.State.Steps)
        {
            var attempts = projection.History.AttemptsByStepId.GetValueOrDefault(
                stepState.StepId, (IReadOnlyList<ExecutionAttempt>)[]);

            for (var index = 0; index < attempts.Count; index++)
            {
                var attempt = attempts[index];
                var classificationSuffix = attempt.FailureClassification is { } classification
                    ? $" ({classification})"
                    : string.Empty;
                var nonProcessSuffix = attempt.IsNonProcess ? " [non-process]" : string.Empty;

                HistoryPanel.Children.Add(new TextBlock
                {
                    Text = $"{stepState.StepId} attempt {index + 1}/{attempts.Count}: " +
                           $"{attempt.ExecutionId} -> {attempt.Status}{classificationSuffix}{nonProcessSuffix}",
                });
            }

            var summary = $"{stepState.StepId}: consecutive failures={stepState.ConsecutiveFailureCount}";
            if (stepState.Status == StepStatus.Paused)
            {
                var pausePoint = stepDefinitionByStepId[stepState.StepId].PausePoint;
                var supersedeTargets = pausePoint?.SupersedeTargets is { Count: > 0 } targets
                    ? string.Join(", ", targets)
                    : "none";
                summary += $", paused (underlying outcome={stepState.PausedOutcome}), supersede targets=[{supersedeTargets}]";
            }

            if (stepState.PendingSupplementaryExecutionId is { } pendingSupplement)
            {
                summary += $", pending supplementary execution={pendingSupplement}";
            }

            if (stepState.IsPendingSupersedeTarget)
            {
                summary += ", pending supersede dispatch";
            }

            HistoryPanel.Children.Add(new TextBlock { Text = summary });
        }
    }

    /// <summary>
    /// Rebuilds <see cref="ViewModel"/>'s <see cref="MainWindowViewModel.PausedSteps"/> (M15 Phase 2,
    /// issue #138) — one entry per step whose latest attempt is <see cref="StepStatus.Paused"/>,
    /// carrying the <see cref="ExecutionId"/> a decision resolves (<see cref="StepState.LatestExecutionId"/>,
    /// guaranteed non-null while paused). Rebuilt from scratch on every load, the same "projected
    /// fact, not retained handler state" discipline every other render method here follows — a step
    /// that resumes simply stops appearing next load, with nothing to reconcile.
    /// </summary>
    private void RebuildPausedSteps(TaskProjection projection, string taskDirectoryPath)
    {
        ViewModel.PausedSteps.Clear();

        var stepDefinitionByStepId = projection.Snapshot.Steps.ToDictionary(step => step.StepId);

        foreach (var stepState in projection.State.Steps)
        {
            if (stepState.Status != StepStatus.Paused || stepState.LatestExecutionId is not { } executionId)
            {
                continue;
            }

            // Every Paused step was paused by the Pause Engine only for a step declaring PausePoint
            // (§17.1) — the same Flow-internal invariant ExternalDecisionValidator itself relies on.
            var supersedeTargets = stepDefinitionByStepId[stepState.StepId].PausePoint!.SupersedeTargets;

            ViewModel.PausedSteps.Add(new PausedStepViewModel(
                stepState.StepId,
                executionId,
                supersedeTargets,
                (stepId, decidedExecutionId, decisionType, targetStepId, revisionFilePath, supplementaryWorker, supplementaryOutputName) =>
                    DecideAsync(
                        taskDirectoryPath, stepId, decidedExecutionId, decisionType, targetStepId,
                        revisionFilePath, supplementaryWorker, supplementaryOutputName))
            {
                IsEnabled = !ViewModel.IsMutationInFlight,
            });
        }
    }

    /// <summary>
    /// Rebuilds <see cref="ViewModel"/>'s <see cref="MainWindowViewModel.RunningExecutions"/> (M15
    /// Phase 4, issue #140) — one entry per process-bound step still <see cref="StepStatus.Running"/>
    /// and one per step-less supplementary/human execution still pending (spec §17.3), the same
    /// "projected fact, not retained handler state" discipline <see cref="RebuildPausedSteps"/>
    /// already follows. <see cref="RunningExecutionViewModel.IsLocallyHosted"/> is derived once here,
    /// from whether this window's own retained pump (<see cref="_currentInFlightExecutions"/>) is
    /// currently the one driving <paramref name="taskDirectoryPath"/> — the only two mutations that
    /// can ever be true at once share the same <see cref="MainWindowViewModel.IsMutationInFlight"/>
    /// flag, so this is unambiguous.
    /// </summary>
    private void RebuildRunningExecutions(TaskProjection projection, string taskDirectoryPath)
    {
        ViewModel.RunningExecutions.Clear();

        var isLocallyHostedTask = _currentInFlightExecutions is not null && _currentTaskDirectoryPath == taskDirectoryPath;

        foreach (var stepState in projection.State.Steps)
        {
            if (stepState.Status != StepStatus.Running || stepState.LatestExecutionId is not { } executionId)
            {
                continue;
            }

            AddRunningExecution(stepState.StepId, executionId, isLocallyHostedTask, projection.State, taskDirectoryPath);
        }

        foreach (var stepLessExecution in projection.State.StepLessExecutions)
        {
            // Never locally hosted: a non-process dispatch never registers with
            // InFlightExecutionRegistry in the first place (Phase 1's NonProcessCancellationDetector
            // owns that tier directly, finalizing it within the same pump round it settles in).
            AddRunningExecution(stepId: null, stepLessExecution.ExecutionId, isLocallyHosted: false, projection.State, taskDirectoryPath);
        }
    }

    private void AddRunningExecution(
        StepId? stepId, ExecutionId executionId, bool isLocallyHosted, FlowState state, string taskDirectoryPath)
    {
        var cancellationRequested = state.CancellationRequestedExecutionIds.Contains(executionId);

        ViewModel.RunningExecutions.Add(new RunningExecutionViewModel(
            stepId,
            executionId,
            isLocallyHosted,
            cancellationRequested,
            targetExecutionId => CancelExecutionAsync(taskDirectoryPath, targetExecutionId))
        {
            IsEnabled = isLocallyHosted || !ViewModel.IsMutationInFlight,
        });
    }

    /// <summary>
    /// The targeted-Cancel surface (M15 Phase 4, issue #140): delivered in-process via the retained
    /// <see cref="InFlightExecutionRegistry"/> when this window's own pump currently has
    /// <paramref name="executionId"/> in flight — a fast, idempotent signal, never a second
    /// mutation-surface call, since §15's guard is already held for that pump's entire duration
    /// (M10's decision of record). Otherwise this is the only way left to reach it: a brand-new
    /// <see cref="CancelCommand"/> mutation call, wrapped exactly like <see cref="RunAsync"/> wraps
    /// <c>RunCommand</c> — including the possibility of a <see cref="Aer.Flow.Concurrency.WorkflowLockedException"/>
    /// from whatever process (or pump) currently holds the task's lock, rendered as an in-window
    /// message rather than a button that pretends to work (the phase's own open question).
    /// </summary>
    private async Task CancelExecutionAsync(string taskDirectoryPath, ExecutionId executionId, CancellationToken cancellationToken = default)
    {
        if (_currentInFlightExecutions is { } registry && _currentTaskDirectoryPath == taskDirectoryPath)
        {
            await registry.RequestCancellationAsync(executionId, cancellationToken).ConfigureAwait(true);
            return;
        }

        ViewModel.CancelStatusText = $"Cancelling {executionId.Value}…";
        ViewModel.IsMutationInFlight = true;
        _liveRefreshTimer.Start();

        try
        {
            var options = new CancelOptions(taskDirectoryPath, executionId.Value, BindingsFilePathBox.Text ?? string.Empty);
            await Task.Run(() => CancelCommand.ExecuteAsync(options, _adapters, cancellationToken: cancellationToken), cancellationToken)
                .ConfigureAwait(true);

            ViewModel.CancelStatusText = string.Empty;
        }
        catch (AerFlowException ex)
        {
            // Same in-window-message precedent as RunAsync/DecideAsync/LoadAsync (M14 Phase 1).
            _liveRefreshTimer.Stop();
            ViewModel.CancelStatusText = ex.Message;
            return;
        }
        finally
        {
            ViewModel.IsMutationInFlight = false;
        }

        await OpenAsync(taskDirectoryPath, cancellationToken);
    }

    /// <summary>
    /// The Ctrl+C equivalent (§9's host-initiated stop; M15 Phase 4, issue #140): cancels whichever
    /// pump this window's own Run/Decide action currently has in flight — a no-op when nothing is.
    /// Fire-and-forget by design, mirroring <c>Aer.Cli.Program.cs</c>'s <c>Console.CancelKeyPress</c>
    /// handler: cancelling <see cref="_currentHostStopSource"/> is only the signal.
    /// <see cref="RunAsync"/>/<see cref="DecideAsync"/>'s own awaited pump is what actually drives
    /// §9's intent-first record for every execution still in flight, then the durable
    /// <c>ExecutionCancelled</c> §7's second reflection phase needs, and clears
    /// <see cref="MainWindowViewModel.IsMutationInFlight"/> once that pump reaches its fixed point.
    /// </summary>
    public Task StopAsync()
    {
        _currentHostStopSource?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Window-close semantics with a pump in flight (issue #140): the first <c>Closing</c> is
    /// cancelled and treated as a Stop request instead of a silent abandonment — the CLI's Ctrl+C
    /// equivalent still fires even though there is no terminal to Ctrl+C in. Once the retained pump
    /// task has actually reached its fixed point (<see cref="RunAsync"/>/<see cref="DecideAsync"/>'s
    /// own <c>finally</c> already reflects that in the projection via their trailing
    /// <see cref="OpenAsync"/>), this closes the window for real — a plain, uncancelled close, since
    /// <see cref="_closeConfirmed"/> is now set.
    /// </summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || _currentPumpTask is not { IsCompleted: false } pumpTask)
        {
            return;
        }

        e.Cancel = true;
        _currentHostStopSource?.Cancel();
        _ = CloseOncePumpSettlesAsync(pumpTask);
    }

    private async Task CloseOncePumpSettlesAsync(Task pumpTask)
    {
        try
        {
            await pumpTask.ConfigureAwait(true);
        }
        catch
        {
            // RunAsync/DecideAsync's own try/catch already renders any AerFlowException as an
            // in-window message on their own await of this same task; this second await exists only
            // to learn that the pump has reached a fixed point, not to re-observe its outcome.
        }

        _closeConfirmed = true;
        Close();
    }

    private void RenderDecisions(TaskProjection projection)
    {
        DecisionsPanel.Children.Clear();
        foreach (var decision in projection.History.Decisions)
        {
            var target = decision.TargetStepId is { } targetStepId ? $", target={targetStepId}" : string.Empty;
            var supplement = decision.SupplementaryExecutionId is { } supplementaryExecutionId
                ? $", supplement={supplementaryExecutionId}"
                : string.Empty;
            var resolution = decision.Resolved ? "resolved" : "unresolved";

            DecisionsPanel.Children.Add(new TextBlock
            {
                Text = $"{decision.DecisionId}: {decision.DecisionType} on {decision.ReferencedExecutionId}" +
                       $"{target}{supplement} ({resolution})",
            });
        }
    }

    private void RenderSupplementaryExecutions(TaskProjection projection)
    {
        SupplementaryPanel.Children.Clear();
        foreach (var execution in projection.History.StepLessExecutions)
        {
            var nonProcessSuffix = execution.IsNonProcess ? " [non-process]" : string.Empty;
            SupplementaryPanel.Children.Add(new TextBlock
            {
                Text = $"{execution.ExecutionId} ({execution.Worker}): {execution.Status}{nonProcessSuffix}",
            });
        }
    }

    /// <summary>
    /// Renders <see cref="ArtifactLineage"/> (M14 Phase 4, issue #121): one block per execution,
    /// naming its declared inputs' resolved producers, then a row of buttons — one per file actually
    /// present in its output directory — each wired to <see cref="ShowArtifactPreviewAsync"/>.
    /// <paramref name="taskDirectoryPath"/> is <see cref="LoadAsync"/>'s own parameter, not
    /// <see cref="_currentTaskDirectoryPath"/>: <c>LoadAsync</c> is a supported, directly-callable
    /// entry point in its own right (issue #118) that a caller may invoke without ever going through
    /// <see cref="OpenAsync"/> (which is the only place that field is set) — the rendered buttons must
    /// resolve against the directory this exact call just loaded, not a field that might still be
    /// null or, worse, stale from a previously opened task.
    /// </summary>
    private void RenderArtifactLineage(TaskProjection projection, string taskDirectoryPath)
    {
        LineagePanel.Children.Clear();

        var artifactsRootPath = System.IO.Path.Combine(taskDirectoryPath, ArtifactsDirectoryName);

        foreach (var execution in projection.Lineage.Executions)
        {
            var header = execution.StepId is { } stepId
                ? $"{stepId} — {execution.ExecutionId} ({execution.Worker})"
                : $"(supplementary) — {execution.ExecutionId} ({execution.Worker})";
            LineagePanel.Children.Add(new TextBlock { Text = header, FontWeight = FontWeight.SemiBold });

            foreach (var link in execution.Inputs)
            {
                LineagePanel.Children.Add(new TextBlock
                {
                    Text = $"    input '{link.InputName}' <- {link.ProducerStepId} ({link.ProducerExecutionId})",
                });
            }

            if (execution.OutputFiles.Count == 0)
            {
                LineagePanel.Children.Add(new TextBlock { Text = "    (no output files)" });
                continue;
            }

            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, execution.ExecutionId);
            var filesPanel = new WrapPanel { Margin = new Thickness(16, 0, 0, 0) };
            foreach (var fileName in execution.OutputFiles)
            {
                var filePath = System.IO.Path.Combine(outputDirectory, fileName);
                var button = new Button { Content = fileName, Margin = new Thickness(0, 0, 4, 4) };
                button.Click += (_, _) => _ = ShowArtifactPreviewAsync(filePath);
                filesPanel.Children.Add(button);
            }

            LineagePanel.Children.Add(filesPanel);
        }
    }

    /// <summary>
    /// Reads one artifact file's content into <see cref="ArtifactPreviewBox"/> — "a file listing +
    /// plain-text preview," this phase's stated ceiling (issue #121), not content rendering of any
    /// kind beyond that. Public and directly awaitable, the same reason every other load-driving
    /// entry point on this window is (issue #118): a test can trigger exactly one preview
    /// deterministically instead of raising a real button-click event. Truncated defensively at
    /// <see cref="MaxArtifactPreviewLength"/> — an artifact is not guaranteed to be small or textual,
    /// and this preview is deliberately the cheapest thing that could show it, not a text-viewer.
    /// </summary>
    public async Task ShowArtifactPreviewAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var content = await File.ReadAllTextAsync(filePath, cancellationToken);
            ArtifactPreviewBox.Text = content.Length > MaxArtifactPreviewLength
                ? content[..MaxArtifactPreviewLength] + "\n… (truncated)"
                : content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ArtifactPreviewBox.Text = $"Cannot preview '{System.IO.Path.GetFileName(filePath)}': {ex.Message}";
        }
    }

    /// <summary>
    /// The snapshot-vs-template diff surface (UI spec §5; M14 Phase 4, issue #121): loads
    /// <paramref name="templateFilePath"/> via <see cref="TemplateProjectionLoader"/> and compares it
    /// against the currently open task's bound snapshot via <see cref="SnapshotTemplateDiffer"/>.
    /// Requires a task directory to already be open — <see cref="_lastSnapshot"/> is only ever set by
    /// <see cref="LoadAsync"/>'s success path, never by opening a raw template on its own, since a
    /// template with nothing bound to it has nothing to diff against.
    /// </summary>
    public async Task CompareToTemplateAsync(string templateFilePath, CancellationToken cancellationToken = default)
    {
        DiffPanel.Children.Clear();

        if (_lastSnapshot is not { } snapshot)
        {
            DiffPanel.Children.Add(new TextBlock { Text = "Open a task directory before comparing it to a template." });
            return;
        }

        try
        {
            var template = await TemplateProjectionLoader.LoadAsync(templateFilePath, cancellationToken);
            RenderDiff(SnapshotTemplateDiffer.Diff(snapshot, template));
        }
        catch (AerFlowException ex)
        {
            DiffPanel.Children.Add(new TextBlock { Text = ex.Message });
        }
    }

    private void RenderDiff(SnapshotTemplateDiff diff)
    {
        DiffPanel.Children.Clear();

        if (diff.TemplateIdMismatch)
        {
            DiffPanel.Children.Add(new TextBlock
            {
                Text = "This file is a different template than the one the task is bound to — a " +
                       "mismatch, not a divergence; no diff is shown.",
            });
            return;
        }

        DiffPanel.Children.Add(new TextBlock
        {
            Text = $"Bound snapshot is template version {diff.SnapshotTemplateVersion}; " +
                   $"current template file is version {diff.TemplateVersion}.",
        });

        if (!diff.HasDiverged)
        {
            DiffPanel.Children.Add(new TextBlock { Text = "No divergence: the bound snapshot matches the current template." });
            return;
        }

        foreach (var addedStepId in diff.AddedStepIds)
        {
            DiffPanel.Children.Add(new TextBlock { Text = $"+ {addedStepId} (added in template; not in the bound snapshot)" });
        }

        foreach (var removedStepId in diff.RemovedStepIds)
        {
            DiffPanel.Children.Add(new TextBlock { Text = $"- {removedStepId} (in the bound snapshot; removed from the template)" });
        }

        foreach (var changedStep in diff.ChangedSteps)
        {
            var changedFields = new List<string>();
            if (changedStep.WorkerChanged)
            {
                changedFields.Add("worker");
            }

            if (changedStep.InputsChanged)
            {
                changedFields.Add("inputs");
            }

            if (changedStep.OutputsChanged)
            {
                changedFields.Add("outputs");
            }

            if (changedStep.DependsOnChanged)
            {
                changedFields.Add("dependsOn");
            }

            if (changedStep.RetryPolicyChanged)
            {
                changedFields.Add("retryPolicy");
            }

            if (changedStep.PausePointChanged)
            {
                changedFields.Add("pausePoint");
            }

            DiffPanel.Children.Add(new TextBlock { Text = $"~ {changedStep.StepId} changed: {string.Join(", ", changedFields)}" });
        }
    }

    private async Task RefreshRecentsPanelAsync(CancellationToken cancellationToken)
    {
        var recents = await _configurationStore.LoadRecentTaskDirectoriesAsync(cancellationToken);

        RecentsPanel.Children.Clear();
        foreach (var path in recents)
        {
            var button = new Button { Content = path };
            button.Click += (_, _) => _ = OpenAsync(path);
            RecentsPanel.Children.Add(button);
        }
    }

    /// <summary>
    /// Polling, not a <see cref="System.IO.FileSystemWatcher"/> (issue #119's named open question):
    /// simplest thing that works identically across the win/linux/mac CI matrix without depending on
    /// a given filesystem's watch semantics inside a container. Runs only while a task is open and
    /// not yet <see cref="WorkflowStatus.Terminal"/> — once nothing further can change (spec §12),
    /// there is nothing left to observe.
    /// </summary>
    private void UpdateLiveRefreshTimer()
    {
        if (_lastLoadSucceeded && _lastWorkflowStatus != WorkflowStatus.Terminal)
        {
            _liveRefreshTimer.Start();
        }
        else
        {
            _liveRefreshTimer.Stop();
        }
    }
}
