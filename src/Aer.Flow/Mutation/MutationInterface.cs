using Aer.Flow.Artifacts;
using Aer.Flow.Concurrency;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Outcomes;
using Aer.Flow.Projection;
using Aer.Flow.Scheduling;
using Aer.Flow.Store;

namespace Aer.Flow.Mutation;

/// <summary>
/// The single external entry point for all Flow state mutation (spec §14) — no other code path may
/// append to <c>flow.jsonl</c>. <see cref="StartWorkflowAsync"/> is the "pump" §21 decided on: it
/// blocks until the workflow reaches a fixed point. From M8 Phase 3 on, every step ready in a given
/// scheduling round dispatches concurrently rather than one at a time — a diamond's B and C run
/// simultaneously, and a slow step never delays unrelated ready work.
/// </summary>
public static class MutationInterface
{
    /// <summary>
    /// Acquires the task's §15 concurrency guard, then repeatedly projects <see cref="FlowState"/>,
    /// resolves every ready step (§11.3, retry-aware per §10), and dispatches all of them to Core
    /// concurrently. Each completion (<c>Task.WhenAny</c>) triggers a fresh round — re-projecting
    /// and dispatching any newly-ready work — while the rest stay in flight. Returns once nothing is
    /// ready and nothing remains in flight.
    /// </summary>
    /// <exception cref="WorkflowLockedException">
    /// Another Flow instance already holds <paramref name="taskDirectoryPath"/>'s lock.
    /// </exception>
    public static async Task<FlowState> StartWorkflowAsync(
        WorkflowId workflowId,
        string taskDirectoryPath,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        ICoreDispatcher dispatcher,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskDirectoryPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workerBindings);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);
        ArgumentNullException.ThrowIfNull(eventLogReader);
        ArgumentNullException.ThrowIfNull(eventLogWriter);
        ArgumentNullException.ThrowIfNull(dispatcher);

        using var guard = ConcurrencyGuard.Acquire(taskDirectoryPath);

        return await PumpToFixedPointAsync(
                workflowId, snapshot, workerBindings, artifactsRootPath, eventLogReader, eventLogWriter, dispatcher, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A second mutation-surface entry point (spec §14, §17.2): records an external decision
    /// against a currently paused execution, resumes the workflow, and drives the consequences to
    /// the next fixed point through the same pump <see cref="StartWorkflowAsync"/> uses. Validates
    /// every <see cref="DecisionType"/> against projected state (§17.2's closed-set rules) before
    /// appending anything — an invalid decision throws and leaves the log untouched.
    /// </summary>
    /// <exception cref="WorkflowLockedException">
    /// Another Flow instance already holds <paramref name="taskDirectoryPath"/>'s lock.
    /// </exception>
    /// <exception cref="InvalidExternalDecisionException">The decision violates one of §17.2's rules.</exception>
    public static async Task<FlowState> RecordDecisionAsync(
        WorkflowId workflowId,
        string taskDirectoryPath,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        ICoreDispatcher dispatcher,
        ExecutionId referencedExecutionId,
        DecisionType decisionType,
        StepId? targetStepId = null,
        ExecutionId? supplementaryExecutionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskDirectoryPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workerBindings);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);
        ArgumentNullException.ThrowIfNull(eventLogReader);
        ArgumentNullException.ThrowIfNull(eventLogWriter);
        ArgumentNullException.ThrowIfNull(dispatcher);

        using var guard = ConcurrencyGuard.Acquire(taskDirectoryPath);

        var events = await eventLogReader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var state = StateProjector.Project(events, snapshot);
        var succeededExecutionIds = events
            .OfType<FlowEvent.ExecutionSucceeded>()
            .Select(e => e.ExecutionId)
            .ToHashSet();

        ExternalDecisionValidator.Validate(
            state, snapshot, succeededExecutionIds, referencedExecutionId, decisionType, targetStepId, supplementaryExecutionId);

        var decisionId = new DecisionId(Guid.NewGuid().ToString("n"));

        // Both fsync'd — lifecycle events, same write-sequence discipline as any other append (§7).
        await eventLogWriter.AppendAsync(
                new FlowEvent.ExternalDecisionRecorded(
                    decisionId, referencedExecutionId, decisionType, targetStepId, supplementaryExecutionId),
                cancellationToken)
            .ConfigureAwait(false);
        await eventLogWriter.AppendAsync(new FlowEvent.WorkflowResumed(decisionId), cancellationToken).ConfigureAwait(false);

        return await PumpToFixedPointAsync(
                workflowId, snapshot, workerBindings, artifactsRootPath, eventLogReader, eventLogWriter, dispatcher, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A third mutation-surface entry point (spec §14, §17.3): mints a step-less supplementary
    /// execution — a human, or any other non-process party, producing a new artifact outside the
    /// DAG during a pause. Appends <see cref="FlowEvent.ExecutionRequestAccepted"/> with
    /// <c>StepId: null</c> and pre-allocates the output directory exactly like any other worker
    /// (§16), but does not run the pump: minting one changes no step's readiness by itself, and
    /// nothing here needs driving to a fixed point (§20, no daemon). The returned
    /// <see cref="ExecutionId"/> becomes usable as a <see cref="DecisionType.RetryWithRevision"/> or
    /// <see cref="DecisionType.Supersede"/> decision's <c>SupplementaryExecutionId</c> once
    /// completion — <see cref="NonProcessCompletionDetector"/>, consulted by a later
    /// <see cref="StartWorkflowAsync"/> or <see cref="RecordDecisionAsync"/> pump — has recorded it
    /// as <see cref="FlowEvent.ExecutionSucceeded"/>.
    /// </summary>
    /// <exception cref="WorkflowLockedException">
    /// Another Flow instance already holds <paramref name="taskDirectoryPath"/>'s lock.
    /// </exception>
    /// <exception cref="UnresolvedWorkerException">
    /// <paramref name="worker"/> has no corresponding <see cref="WorkerBinding.NonProcess"/> among
    /// <paramref name="workerBindings"/> — a supplementary execution is non-process by definition
    /// (§17.3), so naming a <see cref="WorkerBinding.Process"/> role (or no role at all) is invalid.
    /// </exception>
    public static async Task<(FlowState State, ExecutionId ExecutionId)> RecordSupplementaryExecutionAsync(
        WorkflowId workflowId,
        string taskDirectoryPath,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath,
        string worker,
        IReadOnlyList<string> inputs,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskDirectoryPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workerBindings);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);
        ArgumentException.ThrowIfNullOrEmpty(worker);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(eventLogReader);
        ArgumentNullException.ThrowIfNull(eventLogWriter);

        using var guard = ConcurrencyGuard.Acquire(taskDirectoryPath);

        if (!workerBindings.TryGetValue(worker, out var binding) || binding is not WorkerBinding.NonProcess nonProcess)
        {
            throw new UnresolvedWorkerException($"No non-process WorkerBinding registered for Worker '{worker}'.");
        }

        var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
        var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRootPath, executionId);
        var environment = ArtifactManager.BuildEnvironment(inputs, outputDirectory);
        var outputs = nonProcess.Contract.ProducedOutputs.Select(output => output.Name).ToList();

        var request = new ExecutionRequest(
            executionId,
            workflowId,
            StepId: null,
            worker,
            inputs,
            outputs,
            Timeout: null,
            environment,
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        // §7's write-sequence discipline still applies: appended and fsync'd before this method
        // returns, even though no Core process ever follows it (§17.3).
        await eventLogWriter.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), cancellationToken)
            .ConfigureAwait(false);

        var events = await eventLogReader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var state = StateProjector.Project(events, snapshot);

        return (state, executionId);
    }

    /// <summary>
    /// The scheduling pump shared by both mutation-surface entry points: repeatedly projects
    /// <see cref="FlowState"/>, finalizes any settled non-process execution, appends any owed
    /// <see cref="FlowEvent.WorkflowPaused"/> obligations, resolves every ready step, and dispatches
    /// all of them concurrently — to Core, or, for a <see cref="WorkerBinding.NonProcess"/> step,
    /// nowhere at all — until nothing is ready and nothing remains in flight. Assumes the caller
    /// already holds the §15 concurrency guard.
    /// </summary>
    private static async Task<FlowState> PumpToFixedPointAsync(
        WorkflowId workflowId,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        ICoreDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var inFlight = new List<Task>();
        FlowState state;

        while (true)
        {
            var events = await eventLogReader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            state = StateProjector.Project(events, snapshot);

            // A derived obligation (§17.3), re-evaluated from projected state on every round for
            // the same crash-safety reason the pause obligation below is: the filesystem is read
            // only here, at classification time, and the resulting ExecutionSucceeded is the
            // durable truth from then on (§13). Must run before pause obligations, so a step that
            // just settled this way can still owe a WorkflowPaused append in the same pass.
            var settledNonProcessExecutionIds = NonProcessCompletionDetector.GetSettledExecutions(
                state, snapshot, workerBindings, artifactsRootPath);
            if (settledNonProcessExecutionIds.Count > 0)
            {
                foreach (var executionId in settledNonProcessExecutionIds)
                {
                    await eventLogWriter.AppendAsync(new FlowEvent.ExecutionSucceeded(executionId), cancellationToken)
                        .ConfigureAwait(false);
                }

                continue;
            }

            // A derived obligation (§17.1), re-evaluated from projected state on every round rather
            // than welded into the dispatch continuation, so a crash between the outcome event and
            // this append loses nothing (§7, §13). Appending changes a paused step's projected
            // status from its terminal outcome to Paused, which must be reflected before readiness
            // is resolved — re-reading and re-projecting the freshly appended events is simpler than
            // threading that one status change through by hand.
            var pauseObligations = PauseEngine.GetPauseObligations(state, snapshot);
            if (pauseObligations.Count > 0)
            {
                foreach (var (stepId, executionId) in pauseObligations)
                {
                    await eventLogWriter.AppendAsync(new FlowEvent.WorkflowPaused(executionId, stepId), cancellationToken)
                        .ConfigureAwait(false);
                }

                continue;
            }

            var readyStepIds = DependencyResolver.GetReadySteps(state, snapshot);

            // Snapshot declaration order, not the ready set's (unordered) iteration order, so a
            // round's intents are always emitted in the same sequence for the same FlowState (§13)
            // regardless of how concurrent dispatches later complete.
            foreach (var stepDefinition in snapshot.Steps)
            {
                if (!readyStepIds.Contains(stepDefinition.StepId))
                {
                    continue;
                }

                if (!workerBindings.TryGetValue(stepDefinition.Worker, out var binding))
                {
                    throw new UnresolvedWorkerException(
                        $"No WorkerBinding registered for Worker '{stepDefinition.Worker}' (step '{stepDefinition.StepId}').");
                }

                // §7's write-sequence rule, extended to a concurrent round: each intent is appended
                // and fsync'd here — awaited sequentially, in declaration order — before that step's
                // own dispatch is even started, and before the next step's intent is written.
                var prepared = await PrepareExecutionAsync(
                        workflowId, stepDefinition, snapshot, state, binding, artifactsRootPath, eventLogWriter, cancellationToken)
                    .ConfigureAwait(false);

                // A non-process worker (§17.3) is fully handled by the append above: no Core
                // process to spawn, so nothing joins the in-flight set. The pump reaches its fixed
                // point with the step awaiting external completion (no daemon, §20); a later round's
                // NonProcessCompletionDetector call is what eventually finalizes it.
                if (binding is WorkerBinding.Process processBinding)
                {
                    // Not awaited here: starts the dispatch and joins the in-flight set, so a slow
                    // step never blocks this round from dispatching the rest of its ready work.
                    inFlight.Add(DispatchAndRecordOutcomeAsync(
                        prepared, processBinding, eventLogWriter, dispatcher, cancellationToken));
                }
            }

            if (inFlight.Count == 0)
            {
                // A round that dispatched only non-process work still changed projected state (new
                // ExecutionRequestAccepted events) even though nothing joined inFlight — loop back
                // around to re-project and return the state that actually reflects it, rather than
                // the stale snapshot read at the top of this iteration.
                if (readyStepIds.Count > 0)
                {
                    continue;
                }

                return state;
            }

            var completed = await Task.WhenAny(inFlight).ConfigureAwait(false);
            inFlight.Remove(completed);
            await completed.ConfigureAwait(false);
        }
    }

    private static async Task<PreparedExecution> PrepareExecutionAsync(
        WorkflowId workflowId,
        WorkflowStepDefinition step,
        WorkflowDefinitionSnapshot snapshot,
        FlowState state,
        WorkerBinding binding,
        string artifactsRootPath,
        IEventLogWriter eventLogWriter,
        CancellationToken cancellationToken)
    {
        var stateByStepId = state.Steps.ToDictionary(s => s.StepId);

        var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
        var inputPaths = ArtifactManager.ResolveInputPaths(step, snapshot, state, artifactsRootPath);
        var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRootPath, executionId);

        // A RetryWithRevision/Supersede consequence still owed to this step (§17.5) carries its
        // supplement into this dispatch — a projected fact, so this holds whether this round is the
        // decision's immediate consequence or a replay resuming after a crash between the two (§13).
        var supplementaryInputPath = stateByStepId[step.StepId].PendingSupplementaryExecutionId is { } supplementaryExecutionId
            ? ArtifactManager.ResolveSupplementaryInputPath(artifactsRootPath, supplementaryExecutionId)
            : null;
        var environment = ArtifactManager.BuildEnvironment(inputPaths, outputDirectory, supplementaryInputPath);

        var upstreamExecutionIds = new Dictionary<StepId, ExecutionId>();
        foreach (var dependencyStepId in step.DependsOn)
        {
            // The Dependency Resolver's condition 1 already guarantees every DependsOn entry has a
            // successful execution — LatestExecutionId is never null here.
            upstreamExecutionIds[dependencyStepId] = stateByStepId[dependencyStepId].LatestExecutionId!.Value;
        }

        var request = new ExecutionRequest(
            executionId,
            workflowId,
            step.StepId,
            step.Worker,
            inputPaths,
            step.Outputs,
            binding is WorkerBinding.Process processBinding ? processBinding.Timeout : null,
            environment,
            upstreamExecutionIds);

        // §7's write-sequence rule: intent recorded and fsync'd before Core is ever asked to run.
        await eventLogWriter.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), cancellationToken)
            .ConfigureAwait(false);

        return new PreparedExecution(request, outputDirectory);
    }

    private static async Task DispatchAndRecordOutcomeAsync(
        PreparedExecution prepared,
        WorkerBinding.Process binding,
        IEventLogWriter eventLogWriter,
        ICoreDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var dispatchResult = await dispatcher.DispatchAsync(prepared.Request, binding.Target, cancellationToken)
            .ConfigureAwait(false);
        var classification = OutcomeClassifier.Classify(dispatchResult, binding.Contract, prepared.OutputDirectory);

        FlowEvent outcomeEvent = classification.Verdict switch
        {
            OutcomeVerdict.Succeeded => new FlowEvent.ExecutionSucceeded(prepared.Request.ExecutionId),
            OutcomeVerdict.Failed => new FlowEvent.ExecutionFailed(prepared.Request.ExecutionId, classification.FailureClassification),
            OutcomeVerdict.Cancelled => new FlowEvent.ExecutionCancelled(prepared.Request.ExecutionId),
            _ => throw new ArgumentOutOfRangeException(nameof(classification), classification.Verdict, "Unknown OutcomeVerdict."),
        };

        await eventLogWriter.AppendAsync(outcomeEvent, cancellationToken).ConfigureAwait(false);
    }

    private sealed record PreparedExecution(ExecutionRequest Request, string OutputDirectory);
}
