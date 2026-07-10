using Aer.Flow.Domain;

namespace Aer.Flow.Projection;

/// <summary>
/// Reconstructs <see cref="FlowState"/> from event history (spec §12):
/// <c>FlowState = Project(EventStore, WorkflowDefinitionSnapshot)</c>. A pure function — no I/O, no
/// wall-clock time, no live process state (§13) — so identical inputs always produce an identical
/// result.
/// </summary>
public static class StateProjector
{
    /// <summary>
    /// Projects <paramref name="events"/> — read linearly, in append order, from Flow's half of the
    /// Event Store — against <paramref name="snapshot"/> into a <see cref="FlowState"/>. Every
    /// cross-reference below is keyed by <see cref="ExecutionId"/> or <see cref="DecisionId"/> —
    /// never by an event's position — per §6's causal-linking rule. Append order is used only to
    /// determine which accepted attempt is "most recent" for a step, which is exactly what append
    /// order of a single writer's own log means.
    /// </summary>
    public static FlowState Project(IReadOnlyList<FlowEvent> events, WorkflowDefinitionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(snapshot);

        var latestExecutionIdByStepId = new Dictionary<StepId, ExecutionId>();
        var terminalStatusByExecutionId = new Dictionary<ExecutionId, StepStatus>();
        var pausedExecutionIds = new HashSet<ExecutionId>();
        var referencedExecutionIdByDecisionId = new Dictionary<DecisionId, ExecutionId>();

        foreach (var flowEvent in events)
        {
            switch (flowEvent)
            {
                case FlowEvent.ExecutionRequestAccepted accepted:
                    latestExecutionIdByStepId[accepted.Request.StepId] = accepted.Request.ExecutionId;
                    break;

                case FlowEvent.ExecutionSucceeded succeeded:
                    terminalStatusByExecutionId[succeeded.ExecutionId] = StepStatus.Succeeded;
                    break;

                case FlowEvent.ExecutionFailed failed:
                    terminalStatusByExecutionId[failed.ExecutionId] = StepStatus.Failed;
                    break;

                case FlowEvent.ExecutionCancelled cancelled:
                    terminalStatusByExecutionId[cancelled.ExecutionId] = StepStatus.Cancelled;
                    break;

                case FlowEvent.WorkflowPaused paused:
                    pausedExecutionIds.Add(paused.ExecutionId);
                    break;

                case FlowEvent.ExternalDecisionRecorded decision:
                    referencedExecutionIdByDecisionId[decision.DecisionId] = decision.ReferencedExecutionId;
                    break;

                case FlowEvent.WorkflowResumed resumed:
                    if (referencedExecutionIdByDecisionId.TryGetValue(resumed.DecisionId, out var resumedExecutionId))
                    {
                        pausedExecutionIds.Remove(resumedExecutionId);
                    }

                    break;

                // ExecutionRequestRejected carries no StepId and never received an
                // ExecutionRequestAccepted, so it never becomes "the latest attempt" for any step.
                // CancellationRequested is mid-execution, not an outcome — the step stays Running
                // (or Paused) until the matching terminal event arrives.
                case FlowEvent.ExecutionRequestRejected:
                case FlowEvent.CancellationRequested:
                    break;
            }
        }

        var steps = new List<StepState>(snapshot.Steps.Count);
        foreach (var stepDefinition in snapshot.Steps)
        {
            if (!latestExecutionIdByStepId.TryGetValue(stepDefinition.StepId, out var latestExecutionId))
            {
                steps.Add(new StepState(stepDefinition.StepId, StepStatus.Pending, LatestExecutionId: null));
                continue;
            }

            // No terminal event yet for the latest attempt means either it is genuinely still
            // running, or Flow crashed before recording its outcome — the two are indistinguishable
            // from the event log alone (§6), and both project to Running.
            var status = pausedExecutionIds.Contains(latestExecutionId)
                ? StepStatus.Paused
                : terminalStatusByExecutionId.GetValueOrDefault(latestExecutionId, StepStatus.Running);

            steps.Add(new StepState(stepDefinition.StepId, status, latestExecutionId));
        }

        return new FlowState(snapshot.WorkflowDefinitionSnapshotId, steps);
    }
}
