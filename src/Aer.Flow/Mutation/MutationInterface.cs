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
    /// <param name="inFlightExecutions">
    /// M10 Phase 2's live-cancellation delivery point (§9 steps 1-3): populated with every
    /// process-bound dispatch this call has in flight, so a caller retaining this instance can
    /// cancel one of them via <see cref="InFlightExecutionRegistry.RequestCancellationAsync"/> while
    /// this call is still running — the only way a live execution is reachable at all, since §15's
    /// guard blocks any second mutation-surface call for the same task until this one returns.
    /// Defaults to a fresh, unshared instance when the caller has no need to interact with it.
    /// </param>
    /// <param name="cancellationToken">
    /// A host-initiated stop (§9): when cancelled, every execution this call currently has in flight
    /// gets a <see cref="FlowEvent.CancellationRequested"/> recorded and fsync'd, then is signalled —
    /// never the reverse, and never signalled directly without a recorded intent first.
    /// </param>
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
        InFlightExecutionRegistry? inFlightExecutions = null,
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
                workflowId, snapshot, workerBindings, artifactsRootPath, eventLogReader, eventLogWriter, dispatcher,
                inFlightExecutions ?? new InFlightExecutionRegistry(), cancellationToken)
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
        InFlightExecutionRegistry? inFlightExecutions = null,
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
                workflowId, snapshot, workerBindings, artifactsRootPath, eventLogReader, eventLogWriter, dispatcher,
                inFlightExecutions ?? new InFlightExecutionRegistry(), cancellationToken)
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
    /// A fourth mutation-surface entry point (spec §14, §9 steps 1 and 4): records an on-demand
    /// cancellation intent — fsync'd before anything else happens, even when the target has already
    /// reached a terminal outcome (§9 step 4's too-late no-op; §7's intent-first ordering) — then
    /// drives the consequences to the next fixed point through the same pump
    /// <see cref="StartWorkflowAsync"/> uses. Phase 1 finalizes only targets with no live Core
    /// process to signal: a pending non-process execution's obligation is fulfilled directly, in the
    /// same round, by <see cref="NonProcessCancellationDetector"/>. A still-running
    /// <see cref="WorkerBinding.Process"/> target's request is durably recorded here but not yet
    /// delivered — that is Phase 2's machinery.
    /// </summary>
    /// <exception cref="WorkflowLockedException">
    /// Another Flow instance already holds <paramref name="taskDirectoryPath"/>'s lock.
    /// </exception>
    /// <exception cref="UnknownExecutionIdException">
    /// <paramref name="targetExecutionId"/> was never admitted via <see cref="FlowEvent.ExecutionRequestAccepted"/>.
    /// </exception>
    public static async Task<FlowState> RequestCancellationAsync(
        WorkflowId workflowId,
        string taskDirectoryPath,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        ICoreDispatcher dispatcher,
        ExecutionId targetExecutionId,
        InFlightExecutionRegistry? inFlightExecutions = null,
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
        var knownExecutionIds = events
            .OfType<FlowEvent.ExecutionRequestAccepted>()
            .Select(e => e.Request.ExecutionId)
            .ToHashSet();

        CancellationValidator.Validate(knownExecutionIds, targetExecutionId);

        // §7's write-sequence discipline: recorded and fsync'd before anything else, whether the
        // target turns out to be a live process, a pending non-process execution, or already
        // terminal (§9 step 4 — the record itself is the too-late outcome; nothing else changes).
        await eventLogWriter.AppendAsync(new FlowEvent.CancellationRequested(targetExecutionId), cancellationToken)
            .ConfigureAwait(false);

        return await PumpToFixedPointAsync(
                workflowId, snapshot, workerBindings, artifactsRootPath, eventLogReader, eventLogWriter, dispatcher,
                inFlightExecutions ?? new InFlightExecutionRegistry(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The scheduling pump shared by every mutation-surface entry point that needs one: repeatedly
    /// projects <see cref="FlowState"/>, finalizes any settled non-process execution, finalizes any
    /// non-process execution with an unfulfilled cancellation request, appends any owed
    /// <see cref="FlowEvent.WorkflowPaused"/> obligations, resolves every ready step, and dispatches
    /// all of them concurrently — to Core, or, for a <see cref="WorkerBinding.NonProcess"/> step,
    /// nowhere at all — until nothing is ready and nothing remains in flight. Assumes the caller
    /// already holds the §15 concurrency guard.
    /// </summary>
    /// <remarks>
    /// M10 Phase 2: every process-bound dispatch this loop starts is registered with
    /// <paramref name="inFlightExecutions"/> under its own <see cref="CancellationTokenSource"/> —
    /// never the ambient <paramref name="cancellationToken"/> directly, so a cancellation of that
    /// host token can never reach Core without <see cref="FlowEvent.CancellationRequested"/> being
    /// recorded first (§7, §9 step 1). While dispatches are in flight, this loop also races
    /// <paramref name="cancellationToken"/> itself: the instant it is cancelled, every execution
    /// still registered gets its intent recorded and is then signalled via
    /// <see cref="InFlightExecutionRegistry.RequestStopAsync"/> — the host-initiated stop.
    /// </remarks>
    private static async Task<FlowState> PumpToFixedPointAsync(
        WorkflowId workflowId,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        ICoreDispatcher dispatcher,
        InFlightExecutionRegistry inFlightExecutions,
        CancellationToken cancellationToken)
    {
        inFlightExecutions.Bind(eventLogWriter);

        var inFlight = new List<Task>();
        var hostStopRequested = false;

        // Starts as the caller's own token, but is switched to CancellationToken.None the instant a
        // host stop is detected below (M10 Phase 2): every read/write this loop performs to reach
        // its fixed point must keep completing even after the ambient token has fired, or the pump
        // could never converge to the consistent, fully-classified state a host stop promises.
        var ioCancellationToken = cancellationToken;
        FlowState state;

        while (true)
        {
            // Captured before the log read below, not after (issue #81): a sibling dispatch's
            // DispatchAndRecordOutcomeAsync always appends its outcome and fsyncs it before calling
            // Unregister, so if an ExecutionId has already dropped out of this snapshot, the append
            // that preceded its Unregister is guaranteed to already be durable — and therefore
            // visible to the log read started right after. Reading the log first and checking the
            // registry second (the previous order) offered no such guarantee: a sibling could finish
            // its append-then-Unregister sequence in the gap after the read had already started,
            // leaving a Running step that looks unregistered and unstarted-in-Core — indistinguishable
            // from the "safe pre-spawn crash" state — even though it had, in fact, just succeeded.
            var registeredExecutionIds = inFlightExecutions.RegisteredExecutionIds();

            // A single read of the combined log per round — feeding both Flow's own projection and
            // M10 Phase 3's crash reconciliation from one pass, rather than reading and parsing the
            // same file twice for no new information.
            var log = await eventLogReader.ReadSnapshotAsync(ioCancellationToken).ConfigureAwait(false);
            var events = log.FlowEvents;
            state = StateProjector.Project(events, snapshot);

            // Keyed once per round rather than re-scanned per obligation: every crash-recovery
            // branch below that acts on an already-accepted execution (classification or
            // re-submission) looks up its durably recorded ExecutionRequest here instead of
            // reconstructing one, so no new ExecutionRequestAccepted is ever needed for the same
            // attempt.
            var acceptedRequestByExecutionId = events
                .OfType<FlowEvent.ExecutionRequestAccepted>()
                .ToDictionary(e => e.Request.ExecutionId, e => e.Request);

            // M10 Phase 3 (§7 full robustness): joins Core's half of the log — read back here for
            // the first time since M7 Phase 6 wrote it — to Flow's own intents by ExecutionId (§6),
            // distinguishing a process-bound step's "genuinely still Running" from "a prior pump
            // crashed before recording its outcome" (until now indistinguishable, per StateProjector's
            // own comment). A dispatch this very call still has registered is excluded — that pump is
            // this pump, not a crashed one.
            var crashRecovery = ProcessCrashRecoveryDetector.GetObligations(
                state, snapshot, workerBindings, log.CoreEvents, registeredExecutionIds);

            // Ran while Flow was down (§6): classify now from the recorded exit and the contract on
            // disk, exactly as if the completion had just arrived — regardless of any unfulfilled
            // cancellation request, which simply derives as too late unless the recorded exit reason
            // was itself CancelRequested (§9's crash clause).
            if (crashRecovery.ToClassify.Count > 0)
            {
                foreach (var (executionId, exit) in crashRecovery.ToClassify)
                {
                    var request = acceptedRequestByExecutionId[executionId];
                    var binding = (WorkerBinding.Process)workerBindings[request.Worker];
                    var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, executionId);
                    var classification = OutcomeClassifier.Classify(
                        new CoreDispatchResult(exit.ExitCode, exit.Reason), binding.Contract, outputDirectory);

                    await eventLogWriter.AppendAsync(ToOutcomeEvent(executionId, classification), ioCancellationToken)
                        .ConfigureAwait(false);
                }

                continue;
            }

            // No ExecutionStarted was ever recorded for this target (§9's crash clause): the cancel
            // wins, finalized directly — there was never anything to forward to Core in the first
            // place, and re-dispatching now would race the intent that already decided this attempt
            // is not to run.
            if (crashRecovery.ToFinalizeAsCancelled.Count > 0)
            {
                foreach (var executionId in crashRecovery.ToFinalizeAsCancelled)
                {
                    await eventLogWriter.AppendAsync(new FlowEvent.ExecutionCancelled(executionId), ioCancellationToken)
                        .ConfigureAwait(false);
                }

                continue;
            }

            // The orphan (§7's third crash state): ExecutionStarted with no ExecutionExited, this
            // call's own registry proving it is not still genuinely in flight here. Nothing can
            // re-attach (§20 no daemon; the binding is spawn-and-await) and §15 forbids a second
            // execution for the same request, so the attempt is finalized from recorded facts alone
            // as abandoned — a real, chargeable failed attempt (§10) — regardless of whether a
            // cancellation was also pending for it. There is no live handle left to re-issue a
            // cancellation toward (this pump is not the one that dispatched it); the best-effort
            // re-issue spec §7 allows for is therefore a documented no-op given aer-core's binding
            // has no cross-process re-attach capability, not a new mechanism this phase introduces.
            if (crashRecovery.ToFinalizeAsAbandoned.Count > 0)
            {
                foreach (var executionId in crashRecovery.ToFinalizeAsAbandoned)
                {
                    await eventLogWriter.AppendAsync(
                            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable), ioCancellationToken)
                        .ConfigureAwait(false);
                }

                continue;
            }

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
                    await eventLogWriter.AppendAsync(new FlowEvent.ExecutionSucceeded(executionId), ioCancellationToken)
                        .ConfigureAwait(false);
                }

                continue;
            }

            // A derived obligation (§9 steps 2-3, vacuous with no process), re-evaluated from
            // projected state on every round for the same crash-safety reason as the settlement
            // check above. Must run before pause obligations, so a step just cancelled this way can
            // still owe a WorkflowPaused append in the same pass.
            var cancelledNonProcessExecutionIds = NonProcessCancellationDetector.GetCancelledExecutions(
                state, snapshot, workerBindings);
            if (cancelledNonProcessExecutionIds.Count > 0)
            {
                foreach (var executionId in cancelledNonProcessExecutionIds)
                {
                    await eventLogWriter.AppendAsync(new FlowEvent.ExecutionCancelled(executionId), ioCancellationToken)
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
                    await eventLogWriter.AppendAsync(new FlowEvent.WorkflowPaused(executionId, stepId), ioCancellationToken)
                        .ConfigureAwait(false);
                }

                continue;
            }

            // Once a host stop is underway, no newly-ready step should be dispatched — cancellation
            // is winding this call down, not making room for fresh work. The same applies to a
            // crash-recovery resubmission (M10 Phase 3): it is a brand-new dispatch to Core too.
            var readyStepIds = hostStopRequested
                ? (IReadOnlySet<StepId>)new HashSet<StepId>()
                : DependencyResolver.GetReadySteps(state, snapshot);
            var toResubmit = hostStopRequested ? (IReadOnlyList<ExecutionId>)[] : crashRecovery.ToResubmit;

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
                        workflowId, stepDefinition, snapshot, state, binding, artifactsRootPath, eventLogWriter, ioCancellationToken)
                    .ConfigureAwait(false);

                // A non-process worker (§17.3) is fully handled by the append above: no Core
                // process to spawn, so nothing joins the in-flight set. The pump reaches its fixed
                // point with the step awaiting external completion (no daemon, §20); a later round's
                // NonProcessCompletionDetector call is what eventually finalizes it.
                if (binding is WorkerBinding.Process processBinding)
                {
                    // Registered under its own token (§9 steps 1-3, M10 Phase 2) — never the ambient
                    // cancellationToken directly — so this specific execution, and only this one, can
                    // be signalled without touching a sibling dispatched in the same round.
                    var executionId = prepared.Request.ExecutionId;
                    var dispatchCancellationToken = inFlightExecutions.Register(executionId);

                    // Not awaited here: starts the dispatch and joins the in-flight set, so a slow
                    // step never blocks this round from dispatching the rest of its ready work.
                    inFlight.Add(DispatchAndRecordOutcomeAsync(
                        prepared, processBinding, eventLogWriter, dispatcher, inFlightExecutions, dispatchCancellationToken));
                }
            }

            // M10 Phase 3's re-submission crash state (§7): the same attempt, not a retry — the
            // intent is already durably recorded (ExecutionRequestAccepted), so this re-dispatches
            // the existing request as-is rather than calling PrepareExecutionAsync, which would
            // append a new one and charge a fresh ExecutionId against nothing.
            foreach (var executionId in toResubmit)
            {
                var request = acceptedRequestByExecutionId[executionId];
                var processBinding = (WorkerBinding.Process)workerBindings[request.Worker];
                var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRootPath, executionId);
                var prepared = new PreparedExecution(request, outputDirectory);

                var dispatchCancellationToken = inFlightExecutions.Register(executionId);
                inFlight.Add(DispatchAndRecordOutcomeAsync(
                    prepared, processBinding, eventLogWriter, dispatcher, inFlightExecutions, dispatchCancellationToken));
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

            // Races the round's in-flight dispatches against the host token itself (M10 Phase 2): a
            // Task.Delay(Timeout.Infinite, ...) never completes on its own, only transitions to
            // Canceled the instant cancellationToken fires, which Task.WhenAny treats as "done" —
            // exactly the wakeup a host-initiated stop needs without polling.
            var hostStopWatcher = !hostStopRequested && cancellationToken.CanBeCanceled
                ? Task.Delay(Timeout.Infinite, cancellationToken)
                : null;
            var waitCandidates = new List<Task>(inFlight);
            if (hostStopWatcher is not null)
            {
                waitCandidates.Add(hostStopWatcher);
            }

            var completed = await Task.WhenAny(waitCandidates).ConfigureAwait(false);
            if (completed == hostStopWatcher)
            {
                hostStopRequested = true;

                // From here on every read/write this loop performs must survive the now-cancelled
                // ambient token so the pump can still converge (see ioCancellationToken's own
                // remarks above).
                ioCancellationToken = CancellationToken.None;

                // Intent-first, for every execution still in flight, before any of them is signalled
                // (§7, §9 step 1) — RequestStopAsync itself enforces that ordering.
                await inFlightExecutions.RequestStopAsync(CancellationToken.None).ConfigureAwait(false);
                continue;
            }

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
        InFlightExecutionRegistry inFlightExecutions,
        CancellationToken dispatchCancellationToken)
    {
        try
        {
            var dispatchResult = await dispatcher.DispatchAsync(prepared.Request, binding.Target, dispatchCancellationToken)
                .ConfigureAwait(false);
            var classification = OutcomeClassifier.Classify(dispatchResult, binding.Contract, prepared.OutputDirectory);

            // Never gated on dispatchCancellationToken: that token having fired is exactly what
            // produced this outcome (Cancelled) in the first place, so recording it must not itself
            // be cancellable by the same signal (§7 — the outcome append always completes once
            // dispatch has returned).
            await eventLogWriter.AppendAsync(ToOutcomeEvent(prepared.Request.ExecutionId, classification), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            inFlightExecutions.Unregister(prepared.Request.ExecutionId);
        }
    }

    /// <summary>
    /// Maps a classified outcome to the terminal <see cref="FlowEvent"/> it owes (spec §8), shared by
    /// a fresh dispatch's own completion (<see cref="DispatchAndRecordOutcomeAsync"/>) and M10 Phase
    /// 3's from-the-log classification of a recorded exit — the same mapping either way.
    /// </summary>
    private static FlowEvent ToOutcomeEvent(ExecutionId executionId, OutcomeClassification classification) =>
        classification.Verdict switch
        {
            OutcomeVerdict.Succeeded => new FlowEvent.ExecutionSucceeded(executionId),
            OutcomeVerdict.Failed => new FlowEvent.ExecutionFailed(executionId, classification.FailureClassification),
            OutcomeVerdict.Cancelled => new FlowEvent.ExecutionCancelled(executionId),
            _ => throw new ArgumentOutOfRangeException(nameof(classification), classification.Verdict, "Unknown OutcomeVerdict."),
        };

    private sealed record PreparedExecution(ExecutionRequest Request, string OutputDirectory);
}
