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

        var inFlight = new List<Task>();
        FlowState state;

        while (true)
        {
            var events = await eventLogReader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            state = StateProjector.Project(events, snapshot);

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

                // Not awaited here: starts the dispatch and joins the in-flight set, so a slow step
                // never blocks this round from dispatching the rest of its ready work.
                inFlight.Add(DispatchAndRecordOutcomeAsync(
                    prepared, binding, eventLogWriter, dispatcher, cancellationToken));
            }

            if (inFlight.Count == 0)
            {
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
        var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
        var inputPaths = ArtifactManager.ResolveInputPaths(step, snapshot, state, artifactsRootPath);
        var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRootPath, executionId);
        var environment = ArtifactManager.BuildEnvironment(inputPaths, outputDirectory);

        var stateByStepId = state.Steps.ToDictionary(s => s.StepId);
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
            binding.Timeout,
            environment,
            upstreamExecutionIds);

        // §7's write-sequence rule: intent recorded and fsync'd before Core is ever asked to run.
        await eventLogWriter.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), cancellationToken)
            .ConfigureAwait(false);

        return new PreparedExecution(request, outputDirectory);
    }

    private static async Task DispatchAndRecordOutcomeAsync(
        PreparedExecution prepared,
        WorkerBinding binding,
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
