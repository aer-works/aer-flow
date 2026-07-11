namespace Aer.Flow.Domain;

/// <summary>
/// <c>FlowState = Project(EventStore, WorkflowDefinitionSnapshot)</c> (spec §12) — workflow state
/// reconstructed from event history, never from live process state, wall-clock time, or anything
/// not frozen inside an event (§13). Producing this from the event log is the State Projector's
/// job (M7 Phase 4); this type is only the shape it projects into.
/// </summary>
public sealed record FlowState(
    WorkflowDefinitionSnapshotId WorkflowDefinitionSnapshotId,
    IReadOnlyList<StepState> Steps);

/// <summary>
/// The status of a single step's most recent execution attempt. <see cref="StepStatus.Running"/> covers both
/// "genuinely still executing" and "Flow crashed before recording the outcome" — the two are
/// indistinguishable from the event log alone (spec §6) until a terminal event is observed.
/// </summary>
public enum StepStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Paused,
}

/// <summary>A step's projected status, as of the most recent event concerning it (spec §11.3).</summary>
/// <param name="UpstreamExecutionIds">
/// The <see cref="ExecutionRequest.UpstreamExecutionIds"/> recorded on <paramref name="LatestExecutionId"/>'s
/// request — empty when the step has no execution yet. This is what the Dependency Resolver's
/// staleness check (§11.3 condition 2) compares against a dependency's current latest successful
/// <see cref="ExecutionId"/>.
/// </param>
/// <param name="ConsecutiveFailureCount">
/// The number of trailing consecutive <see cref="FlowEvent.ExecutionFailed"/> attempts for this step,
/// resetting to zero on <see cref="FlowEvent.ExecutionSucceeded"/> — the Retry Engine's input for
/// <c>RetryPolicy.MaxAttempts</c> (spec §10).
/// </param>
/// <param name="LatestFailureClassification">
/// The <see cref="Domain.FailureClassification"/> carried on the latest attempt's
/// <see cref="FlowEvent.ExecutionFailed"/> event; <c>null</c> when the latest attempt did not fail or
/// reported no classification, which every consumer treats as <see cref="Domain.FailureClassification.Retryable"/>
/// (spec §8.1).
/// </param>
public sealed record StepState(
    StepId StepId,
    StepStatus Status,
    ExecutionId? LatestExecutionId,
    IReadOnlyDictionary<StepId, ExecutionId> UpstreamExecutionIds,
    int ConsecutiveFailureCount = 0,
    FailureClassification? LatestFailureClassification = null);
