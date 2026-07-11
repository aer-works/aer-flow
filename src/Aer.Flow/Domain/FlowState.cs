namespace Aer.Flow.Domain;

/// <summary>
/// <c>FlowState = Project(EventStore, WorkflowDefinitionSnapshot)</c> (spec §12) — workflow state
/// reconstructed from event history, never from live process state, wall-clock time, or anything
/// not frozen inside an event (§13). Producing this from the event log is the State Projector's
/// job (M7 Phase 4); this type is only the shape it projects into.
/// </summary>
/// <param name="Status">
/// A pure projection of <paramref name="Steps"/> (spec §12), never a stored event (§5.2), letting a
/// caller distinguish why <c>StartWorkflowAsync</c>'s pump returned — finished vs. waiting on an
/// external decision (§17.1). Defaults to <see cref="WorkflowStatus.Running"/> for call sites that
/// construct a <see cref="FlowState"/> directly rather than through <c>StateProjector.Project</c>.
/// </param>
public sealed record FlowState(
    WorkflowDefinitionSnapshotId WorkflowDefinitionSnapshotId,
    IReadOnlyList<StepState> Steps,
    WorkflowStatus Status = WorkflowStatus.Running);

/// <summary>
/// A workflow's derived, whole-of-DAG status (spec §12) — computed from <see cref="StepState.Status"/>
/// across every step, never stored as its own event (§5.2, §17.1).
/// </summary>
public enum WorkflowStatus
{
    /// <summary>At least one step's latest attempt is still in flight (or Flow crashed before recording its outcome, §6).</summary>
    Running,

    /// <summary>No step is running, and at least one is idle at a <see cref="FlowEvent.WorkflowPaused"/> awaiting a decision (§17.1).</summary>
    Paused,

    /// <summary>The pump reached its fixed point: nothing running, nothing paused, nothing further to dispatch.</summary>
    Terminal,
}

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

    /// <summary>
    /// An external <see cref="DecisionType.Reject"/> resolved this step's pause (spec §17.2):
    /// terminally failed with retry foreclosed regardless of remaining budget, and — unlike
    /// <see cref="Failed"/> — reachable even from an underlying <see cref="Succeeded"/> outcome
    /// (the approval-gate "no"). Never a stored event; derived from
    /// <see cref="FlowEvent.WorkflowResumed"/> plus the decision it resolves (§5.2).
    /// </summary>
    Rejected,
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
/// <param name="PauseRecordedForLatestExecution">
/// Whether a <see cref="FlowEvent.WorkflowPaused"/> was ever appended for <paramref name="LatestExecutionId"/>
/// — distinct from <see cref="StepStatus.Paused"/>, which <see cref="FlowEvent.WorkflowResumed"/> clears
/// (spec §17.1). The Pause Engine consults this, not the currently-<c>Paused</c> status, so a resumed
/// execution is never re-paused.
/// </param>
/// <param name="PausedOutcome">
/// The underlying terminal <see cref="StepStatus"/> (<see cref="StepStatus.Succeeded"/>,
/// <see cref="StepStatus.Failed"/>, or <see cref="StepStatus.Cancelled"/>) that <paramref name="LatestExecutionId"/>
/// reached before it was masked to <see cref="StepStatus.Paused"/>; <c>null</c> whenever
/// <paramref name="Status"/> is not <see cref="StepStatus.Paused"/>. This is what the External
/// Decision Handler validates <see cref="DecisionType.RetryWithRevision"/>/<see cref="DecisionType.Reject"/>
/// against, since <see cref="Status"/> itself no longer carries that information while paused (§17.2).
/// </param>
public sealed record StepState(
    StepId StepId,
    StepStatus Status,
    ExecutionId? LatestExecutionId,
    IReadOnlyDictionary<StepId, ExecutionId> UpstreamExecutionIds,
    int ConsecutiveFailureCount = 0,
    FailureClassification? LatestFailureClassification = null,
    bool PauseRecordedForLatestExecution = false,
    StepStatus? PausedOutcome = null);
