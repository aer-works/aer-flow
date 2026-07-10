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
public sealed record StepState(StepId StepId, StepStatus Status, ExecutionId? LatestExecutionId);
