using Aer.Adapters;
using Aer.Cli;
using Aer.Flow;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;

namespace Aer.Ui.Core;

/// <summary>
/// One task-facing session's orchestration (M19 Phase 2, issue #187), extracted verbatim from
/// <c>MainWindow</c>'s code-behind so the remote-ready seam holds by construction: everything here
/// — projection loading, pump hosting with the retained <see cref="InFlightExecutionRegistry"/>
/// (M15 Phase 4), and the mutation-interface calls (<c>RunCommand</c>/<c>DecideCommand</c>/
/// <c>SupplyCommand</c>/<c>CancelCommand</c>, the M15 Phase 1 in-process seam) — is
/// presentation-agnostic, and this assembly cannot reference Avalonia to make it otherwise. The
/// desktop window (or any future client, candidate M20) supplies the presentation half through the
/// constructor delegates: where the bindings path comes from at mutation time ("ask, don't infer",
/// M14 Phase 2's decision of record), what happens when a mutation starts/fails (the desktop
/// starts/stops its 2-second poller), and how to re-open a task after a mutation settles.
/// </summary>
public sealed class TaskSession(
    LocalUiConfigurationStore configurationStore,
    IReadOnlyDictionary<string, IWorkerAdapter> adapters,
    MainWindowViewModel viewModel,
    Func<string?> bindingsFilePathProvider,
    Action mutationStarted,
    Action mutationFailed,
    Func<string, CancellationToken, Task> reopenTaskAsync)
{
    /// <summary>The outcome one load produces: exactly one of the two is non-null (§3's honest-error rule — an invalid directory is a rendered message, never a crash).</summary>
    public sealed record LoadOutcome(TaskProjection? Projection, string? ErrorMessage);

    /// <summary>The outcome one template load produces — <see cref="LoadOutcome"/>'s counterpart for a raw, not-yet-instantiated template file (M14 Phase 3).</summary>
    public sealed record TemplateLoadOutcome(Aer.Flow.Domain.WorkflowDefinition? Definition, string? ErrorMessage);

    /// <summary>Null on success; the in-window message otherwise (the M14 Phase 1 precedent: a GUI has no stderr/exit-code convention to fail into).</summary>
    public sealed record MutationOutcome(string? ErrorMessage);

    private readonly LocalUiConfigurationStore _configurationStore = configurationStore;
    private readonly IReadOnlyDictionary<string, IWorkerAdapter> _adapters = adapters;

    /// <summary>
    /// The caller-retained delivery point (M15 Phase 4, issue #140) for whichever Run or Decide pump
    /// this session currently has in flight — <see langword="null"/> whenever this process is not
    /// hosting one. A targeted Cancel on an execution registered here is delivered in-process via
    /// <see cref="InFlightExecutionRegistry.RequestCancellationAsync"/>, never a second
    /// mutation-surface call, since §15's guard is already held for this call's entire duration
    /// (M10's decision of record).
    /// </summary>
    private InFlightExecutionRegistry? _currentInFlightExecutions;

    /// <summary>
    /// The host-stop token source for whichever pump this session currently has in flight (issue
    /// #140) — cancelling it is the Ctrl+C equivalent <c>Aer.Cli</c>'s <c>Program.cs</c> wires to
    /// <c>Console.CancelKeyPress</c>, reused by the desktop's Stop button and window-close handler.
    /// </summary>
    private CancellationTokenSource? _currentHostStopSource;

    public MainWindowViewModel ViewModel { get; } = viewModel;

    public string? CurrentTaskDirectoryPath { get; private set; }
    public bool LastLoadSucceeded { get; private set; }
    public WorkflowStatus? LastWorkflowStatus { get; private set; }
    public WorkflowDefinitionSnapshot? LastSnapshot { get; private set; }

    /// <summary>
    /// The background task driving whichever pump is currently in flight — retained so the desktop's
    /// window-close handler can wait for it to reach a durable fixed point before actually closing,
    /// rather than abandoning it mid-write (issue #140).
    /// </summary>
    public Task? CurrentPumpTask { get; private set; }

    /// <summary>Whether the poller should keep observing: a successfully opened task that has not reached §12's terminal fixed point.</summary>
    public bool ShouldLiveRefresh => LastLoadSucceeded && LastWorkflowStatus != WorkflowStatus.Terminal;

    /// <summary>Points the session at <paramref name="taskDirectoryPath"/> without loading — <c>OpenAsync</c>'s bookkeeping half; the load itself goes through <see cref="LoadAsync"/>.</summary>
    public void SetCurrentTaskDirectory(string? taskDirectoryPath) => CurrentTaskDirectoryPath = taskDirectoryPath;

    public Task RecordOpenedAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
        => _configurationStore.RecordOpenedAsync(taskDirectoryPath, cancellationToken);

    public Task<IReadOnlyList<string>> LoadRecentTaskDirectoriesAsync(CancellationToken cancellationToken = default)
        => _configurationStore.LoadRecentTaskDirectoriesAsync(cancellationToken);

    public Task<string?> LoadLastBindingsFilePathAsync(CancellationToken cancellationToken = default)
        => _configurationStore.LoadLastBindingsFilePathAsync(cancellationToken);

    public Task<string?> LoadLastWorkflowTemplateFilePathAsync(CancellationToken cancellationToken = default)
        => _configurationStore.LoadLastWorkflowTemplateFilePathAsync(cancellationToken);

    /// <summary>
    /// Loads <paramref name="taskDirectoryPath"/> through <see cref="TaskProjectionLoader"/> and
    /// rebuilds the ViewModel's mutation surfaces (<see cref="MainWindowViewModel.PausedSteps"/>,
    /// <see cref="MainWindowViewModel.RunningExecutions"/>) from the projected facts — rebuilt from
    /// scratch on every load, never reconciled (M15's "projected fact, not retained handler state"
    /// discipline). The rendering of read-only surfaces from the returned projection is the
    /// caller's half.
    /// </summary>
    public async Task<LoadOutcome> LoadAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var projection = await TaskProjectionLoader.LoadAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);

            RebuildPausedSteps(projection, taskDirectoryPath);
            RebuildRunningExecutions(projection, taskDirectoryPath);

            LastLoadSucceeded = true;
            LastWorkflowStatus = projection.State.Status;
            LastSnapshot = projection.Snapshot;
            return new LoadOutcome(projection, null);
        }
        catch (AerFlowException ex)
        {
            ViewModel.PausedSteps.Clear();
            ViewModel.DecisionStatusText = string.Empty;
            ViewModel.RunningExecutions.Clear();
            ViewModel.CancelStatusText = string.Empty;

            LastLoadSucceeded = false;
            LastWorkflowStatus = null;
            LastSnapshot = null;
            return new LoadOutcome(null, ex.Message);
        }
    }

    /// <summary>
    /// Loads a raw template file (M14 Phase 3's counterpart to <see cref="LoadAsync"/> for paths
    /// naming a file, not a task directory) and clears the mutation surfaces — a template has no
    /// execution state, so there is nothing to pause or cancel.
    /// </summary>
    public async Task<TemplateLoadOutcome> LoadTemplateAsync(string templateFilePath, CancellationToken cancellationToken = default)
    {
        ViewModel.PausedSteps.Clear();
        ViewModel.DecisionStatusText = string.Empty;
        ViewModel.RunningExecutions.Clear();
        ViewModel.CancelStatusText = string.Empty;

        try
        {
            var definition = await TemplateProjectionLoader.LoadAsync(templateFilePath, cancellationToken).ConfigureAwait(true);

            LastLoadSucceeded = true;
            LastWorkflowStatus = null;
            LastSnapshot = null;
            return new TemplateLoadOutcome(definition, null);
        }
        catch (AerFlowException ex)
        {
            LastLoadSucceeded = false;
            LastWorkflowStatus = null;
            LastSnapshot = null;
            return new TemplateLoadOutcome(null, ex.Message);
        }
    }

    /// <summary>
    /// The Run mutation (M15 Phase 1, issue #137): the same <c>RunCommand.ExecuteAsync</c> call
    /// <c>aer run</c> makes, reused in-process, pumped on a background thread with the retained
    /// registry/host-stop plumbing of M15 Phase 4. On success, records the template/bindings paths
    /// as Local UI Configuration pre-fills and re-opens the task via the caller's delegate.
    /// </summary>
    public async Task<MutationOutcome> RunAsync(
        string taskDirectoryPath, string? workflowTemplateFilePath, string bindingsFilePath, CancellationToken cancellationToken = default)
    {
        CurrentTaskDirectoryPath = taskDirectoryPath;

        var options = new RunOptions(
            string.IsNullOrWhiteSpace(workflowTemplateFilePath) ? null : workflowTemplateFilePath,
            bindingsFilePath,
            taskDirectoryPath);

        ViewModel.IsMutationInFlight = true;
        ViewModel.RunStatusText = "Running…";
        mutationStarted();

        var inFlightExecutions = new InFlightExecutionRegistry();
        var hostStopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _currentInFlightExecutions = inFlightExecutions;
        _currentHostStopSource = hostStopSource;

        try
        {
            var pumpTask = Task.Run(
                () => RunCommand.ExecuteAsync(options, _adapters, inFlightExecutions, hostStopSource.Token), hostStopSource.Token);
            CurrentPumpTask = pumpTask;
            await pumpTask.ConfigureAwait(true);

            ViewModel.RunStatusText = string.Empty;

            if (!string.IsNullOrWhiteSpace(workflowTemplateFilePath))
            {
                await _configurationStore.RecordWorkflowTemplateFilePathAsync(workflowTemplateFilePath, cancellationToken).ConfigureAwait(true);
            }

            await _configurationStore.RecordBindingsFilePathAsync(bindingsFilePath, cancellationToken).ConfigureAwait(true);
        }
        catch (AerFlowException ex)
        {
            // The M14 Phase 1 precedent: a malformed template/bindings file or a
            // WorkflowLockedException from a competing pump becomes an in-window message.
            mutationFailed();
            ViewModel.RunStatusText = ex.Message;
            return new MutationOutcome(ex.Message);
        }
        finally
        {
            ViewModel.IsMutationInFlight = false;
            _currentInFlightExecutions = null;
            _currentHostStopSource = null;
            CurrentPumpTask = null;
            hostStopSource.Dispose();
        }

        await reopenTaskAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);
        return new MutationOutcome(null);
    }

    /// <summary>
    /// The paused-step decision mutation (M15 Phases 2–3, issues #138/#139): optionally the
    /// <c>aer supply</c> half of M12 Phase 3's two-call round trip first, then
    /// <c>DecideCommand.ExecuteAsync</c> — one user-facing action, one mutation-in-flight window,
    /// one poller start. Bindings are asked for at call time via the constructor's provider, never
    /// inferred or persisted per task (M14 Phase 2's decision of record).
    /// </summary>
    public async Task<MutationOutcome> DecideAsync(
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
        mutationStarted();

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
                    bindingsFilePathProvider() ?? string.Empty);

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
                bindingsFilePathProvider() ?? string.Empty);

            var pumpTask = Task.Run(
                () => DecideCommand.ExecuteAsync(options, _adapters, inFlightExecutions, hostStopSource.Token), hostStopSource.Token);
            CurrentPumpTask = pumpTask;
            await pumpTask.ConfigureAwait(true);

            ViewModel.DecisionStatusText = string.Empty;
        }
        catch (Exception ex) when (ex is AerFlowException or FileNotFoundException)
        {
            // WorkflowLockedException from a competing pump, an invalid decision
            // (ExternalDecisionValidator's §17.2 rules), or aer supply's FileNotFoundException for a
            // mistyped revision file path — all in-window messages, never crashes.
            mutationFailed();
            ViewModel.DecisionStatusText = ex.Message;
            return new MutationOutcome(ex.Message);
        }
        finally
        {
            ViewModel.IsMutationInFlight = false;
            _currentInFlightExecutions = null;
            _currentHostStopSource = null;
            CurrentPumpTask = null;
            hostStopSource.Dispose();
        }

        await reopenTaskAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);
        return new MutationOutcome(null);
    }

    /// <summary>
    /// The targeted-Cancel surface (M15 Phase 4, issue #140): in-process via the retained registry
    /// when this session's own pump has <paramref name="executionId"/> in flight; otherwise a
    /// brand-new <see cref="CancelCommand"/> mutation call, with a competing lock-holder's
    /// <c>WorkflowLockedException</c> rendered rather than a button that pretends to work.
    /// </summary>
    public async Task<MutationOutcome> CancelExecutionAsync(
        string taskDirectoryPath, ExecutionId executionId, CancellationToken cancellationToken = default)
    {
        if (_currentInFlightExecutions is { } registry && CurrentTaskDirectoryPath == taskDirectoryPath)
        {
            await registry.RequestCancellationAsync(executionId, cancellationToken).ConfigureAwait(true);
            return new MutationOutcome(null);
        }

        ViewModel.CancelStatusText = $"Cancelling {executionId.Value}…";
        ViewModel.IsMutationInFlight = true;
        mutationStarted();

        try
        {
            var options = new CancelOptions(taskDirectoryPath, executionId.Value, bindingsFilePathProvider() ?? string.Empty);
            await Task.Run(() => CancelCommand.ExecuteAsync(options, _adapters, cancellationToken: cancellationToken), cancellationToken)
                .ConfigureAwait(true);

            ViewModel.CancelStatusText = string.Empty;
        }
        catch (AerFlowException ex)
        {
            mutationFailed();
            ViewModel.CancelStatusText = ex.Message;
            return new MutationOutcome(ex.Message);
        }
        finally
        {
            ViewModel.IsMutationInFlight = false;
        }

        await reopenTaskAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);
        return new MutationOutcome(null);
    }

    /// <summary>
    /// §9's host-initiated stop (M15 Phase 4): cancels whichever pump this session currently has in
    /// flight — a no-op when nothing is. Only the signal; the awaited pump drives the intent-first
    /// record and the durable <c>ExecutionCancelled</c>, and its <c>finally</c> clears the flags.
    /// </summary>
    public void RequestHostStop() => _currentHostStopSource?.Cancel();

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

    private void RebuildRunningExecutions(TaskProjection projection, string taskDirectoryPath)
    {
        ViewModel.RunningExecutions.Clear();

        var isLocallyHostedTask = _currentInFlightExecutions is not null && CurrentTaskDirectoryPath == taskDirectoryPath;

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
            // InFlightExecutionRegistry in the first place (M15 Phase 1's
            // NonProcessCancellationDetector owns that tier directly).
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
}
