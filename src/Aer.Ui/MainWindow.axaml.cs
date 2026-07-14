using Aer.Flow;
using Aer.Flow.Domain;
using Avalonia.Controls;
using Avalonia.Threading;
using System.Linq;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Aer.Ui.Tests")]

namespace Aer.Ui;

public partial class MainWindow : Window
{
    private readonly LocalUiConfigurationStore _configurationStore;
    private readonly DispatcherTimer _liveRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _currentTaskDirectoryPath;
    private bool _lastLoadSucceeded;
    private WorkflowStatus? _lastWorkflowStatus;

    /// <summary>Test-only observation of the live-refresh polling state (see <see cref="UpdateLiveRefreshTimer"/>) — never consulted by production code.</summary>
    internal bool IsLiveRefreshTimerEnabled => _liveRefreshTimer.IsEnabled;

    public MainWindow()
        : this(LocalUiConfigurationStore.CreateDefault())
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
    {
        InitializeComponent();
        _configurationStore = configurationStore;

        _liveRefreshTimer.Tick += (_, _) => _ = RefreshAsync();
        OpenButton.Click += (_, _) => _ = OpenAsync(TaskDirectoryPathBox.Text ?? string.Empty);
        RefreshButton.Click += (_, _) => _ = RefreshAsync();
        Closed += (_, _) => _liveRefreshTimer.Stop();
    }

    /// <summary>Populates <see cref="RecentsPanel"/> from Local UI Configuration (UI spec §3.1) — call once at startup.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RefreshRecentsPanelAsync(cancellationToken);

    /// <summary>
    /// The full "open a task directory" action (UI spec §3.1): loads and renders it via
    /// <see cref="LoadAsync"/>, then — only on success — records it as the most recently opened
    /// directory and starts/stops live re-projection (M14 Phase 2, issue #119) depending on whether
    /// the projected workflow has reached a terminal state. This is what <see cref="OpenButton"/>
    /// and a click on a <see cref="RecentsPanel"/> entry both call; <see cref="App"/>'s CLI-argument
    /// launch path calls it too, so a directory opened that way is remembered exactly like one
    /// opened by hand.
    /// </summary>
    public async Task OpenAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        TaskDirectoryPathBox.Text = taskDirectoryPath;
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

            RenderExecutionHistory(projection);
            RenderDecisions(projection);
            RenderSupplementaryExecutions(projection);

            _lastLoadSucceeded = true;
            _lastWorkflowStatus = projection.State.Status;
        }
        catch (AerFlowException ex)
        {
            // A real GUI has no stderr/exit-code convention to fail into (Aer.Cli's Program.cs
            // boundary) — an invalid task directory or a malformed snapshot/event log renders as an
            // in-window message instead.
            StatusText.Text = ex.Message;
            StepsPanel.Children.Clear();
            HistoryPanel.Children.Clear();
            DecisionsPanel.Children.Clear();
            SupplementaryPanel.Children.Clear();

            _lastLoadSucceeded = false;
            _lastWorkflowStatus = null;
        }
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
