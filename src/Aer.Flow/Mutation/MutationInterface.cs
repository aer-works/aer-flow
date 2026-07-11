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
/// append to <c>flow.jsonl</c>. M7 provides only <see cref="StartWorkflowAsync"/>: linear
/// happy-path execution with no retries, pauses, or concurrent dispatch (IMPLEMENTATION_PLAN.md's
/// M7 goal). This method is itself the "pump" §21 decided on: it blocks, dispatching each ready
/// step as the previous one completes, until the workflow reaches a fixed point. It also holds the
/// §15 concurrency guard for the whole call — the single mutation surface is the natural place to
/// enforce "at most one Flow instance may mutate a given task's workflow state at a time", rather
/// than trusting every caller to remember to acquire it.
/// </summary>
public static class MutationInterface
{
    /// <summary>
    /// Acquires the task's §15 concurrency guard, then repeatedly projects <see cref="FlowState"/>,
    /// resolves ready steps (§11.3), dispatches the first ready step to Core, classifies its
    /// outcome (§8), and appends the result — until no step is ready, either because every step
    /// reached a terminal outcome or because a failure has permanently blocked the remaining graph
    /// (M7 has no Retry Engine, §10 is M8). Returns the final projected <see cref="FlowState"/>.
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
        CoreDispatcher dispatcher,
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

        var stepDefinitionsById = snapshot.Steps.ToDictionary(s => s.StepId);

        while (true)
        {
            var events = await eventLogReader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var state = StateProjector.Project(events, snapshot);
            var readyStepIds = DependencyResolver.GetReadySteps(state, snapshot);

            // Pick by the snapshot's own declaration order rather than the ready set's (unordered)
            // iteration order, so the choice is deterministic even though, for M7's linear
            // happy-path workflows, at most one step is ever actually ready at once.
            WorkflowStepDefinition? nextStep = null;
            foreach (var candidate in snapshot.Steps)
            {
                if (readyStepIds.Contains(candidate.StepId))
                {
                    nextStep = candidate;
                    break;
                }
            }

            if (nextStep is null)
            {
                return state;
            }

            if (!workerBindings.TryGetValue(nextStep.Worker, out var binding))
            {
                throw new UnresolvedWorkerException(
                    $"No WorkerBinding registered for Worker '{nextStep.Worker}' (step '{nextStep.StepId}').");
            }

            await ExecuteStepAsync(
                    workflowId,
                    nextStep,
                    snapshot,
                    state,
                    binding,
                    artifactsRootPath,
                    eventLogWriter,
                    dispatcher,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task ExecuteStepAsync(
        WorkflowId workflowId,
        WorkflowStepDefinition step,
        WorkflowDefinitionSnapshot snapshot,
        FlowState state,
        WorkerBinding binding,
        string artifactsRootPath,
        IEventLogWriter eventLogWriter,
        CoreDispatcher dispatcher,
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

        var dispatchResult = await dispatcher.DispatchAsync(request, binding.Target, cancellationToken)
            .ConfigureAwait(false);
        var classification = OutcomeClassifier.Classify(dispatchResult, binding.Contract, outputDirectory);

        FlowEvent outcomeEvent = classification.Verdict switch
        {
            OutcomeVerdict.Succeeded => new FlowEvent.ExecutionSucceeded(executionId),
            OutcomeVerdict.Failed => new FlowEvent.ExecutionFailed(executionId, classification.FailureClassification),
            OutcomeVerdict.Cancelled => new FlowEvent.ExecutionCancelled(executionId),
            _ => throw new ArgumentOutOfRangeException(nameof(classification), classification.Verdict, "Unknown OutcomeVerdict."),
        };

        await eventLogWriter.AppendAsync(outcomeEvent, cancellationToken).ConfigureAwait(false);
    }
}
