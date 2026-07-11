using Aer.Flow.Domain;

namespace Aer.Flow.Scheduling;

/// <summary>
/// Determines which steps are ready to run per spec §11.3's Dependency Resolution Rule. A pure
/// function over <see cref="FlowState"/> and <see cref="WorkflowDefinitionSnapshot"/> — no I/O, no
/// dispatch, no retries.
/// </summary>
public static class DependencyResolver
{
    /// <summary>
    /// Returns the <see cref="StepId"/>s that are ready to run: for every <see cref="StepId"/> a
    /// step <c>DependsOn</c>, that dependency's most recent attempt succeeded (condition 1), and
    /// this step does not already have a successful execution that used the dependency's current
    /// most recent successful <see cref="ExecutionId"/> (condition 2 — staleness after
    /// <see cref="DecisionType.Supersede"/>, §17.5). A step whose latest attempt failed is also
    /// ready when <see cref="RetryEngine.MayRetry"/> holds for it (§10) — "terminally failed" is
    /// the derived complement (<see cref="StepStatus.Failed"/> and not <c>MayRetry</c>), never a
    /// stored event.
    /// </summary>
    public static IReadOnlySet<StepId> GetReadySteps(FlowState state, WorkflowDefinitionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);

        var stepStateByStepId = state.Steps.ToDictionary(step => step.StepId);
        var readyStepIds = new HashSet<StepId>();

        foreach (var stepDefinition in snapshot.Steps)
        {
            var stepState = stepStateByStepId[stepDefinition.StepId];

            // Running: an attempt is already in flight. Paused: idle until an external decision
            // resolves it (§17.1). Cancelled: never retried, regardless of policy (§9, §10).
            // Rejected: an external Reject forecloses retry regardless of remaining budget,
            // equivalent in effect to exhausting RetryPolicy but externally triggered (§17.2).
            if (stepState.Status is StepStatus.Running or StepStatus.Paused or StepStatus.Cancelled or StepStatus.Rejected)
            {
                continue;
            }

            // A failed step stays terminal unless its RetryPolicy still permits another attempt
            // (§10); one that does proceeds into the same readiness check as any other step.
            if (stepState.Status == StepStatus.Failed && !RetryEngine.MayRetry(stepState, stepDefinition.RetryPolicy))
            {
                continue;
            }

            if (IsReady(stepDefinition, stepState, stepStateByStepId))
            {
                readyStepIds.Add(stepDefinition.StepId);
            }
        }

        return readyStepIds;
    }

    private static bool IsReady(
        WorkflowStepDefinition stepDefinition,
        StepState stepState,
        Dictionary<StepId, StepState> stepStateByStepId)
    {
        // Condition 2 only ever blocks re-readiness by comparing against a dependency that could
        // have gone stale (§17.5). A step with no DependsOn has nothing to go stale against, so
        // the loop below would never run and an already-succeeded root step would be vacuously
        // "ready" on every single projection — an infinite re-run, not a one-time completion.
        if (stepState.Status == StepStatus.Succeeded && stepDefinition.DependsOn.Count == 0)
        {
            return false;
        }

        foreach (var dependencyStepId in stepDefinition.DependsOn)
        {
            var dependencyState = stepStateByStepId[dependencyStepId];

            // Condition 1 (§11.3): the dependency's most recent attempt must have succeeded.
            if (dependencyState.Status != StepStatus.Succeeded)
            {
                return false;
            }

            // Condition 2 (§11.3): only relevant once this step has already succeeded — otherwise
            // there is no prior successful execution to compare staleness against. If this step's
            // recorded upstream for this dependency still matches the dependency's current latest
            // successful ExecutionId, this step is up to date with respect to it and is not ready
            // again; a mismatch (or no recorded entry) means it is stale and must rerun (§17.5).
            if (stepState.Status == StepStatus.Succeeded &&
                stepState.UpstreamExecutionIds.TryGetValue(dependencyStepId, out var recordedUpstreamExecutionId) &&
                recordedUpstreamExecutionId == dependencyState.LatestExecutionId)
            {
                return false;
            }
        }

        return true;
    }
}
