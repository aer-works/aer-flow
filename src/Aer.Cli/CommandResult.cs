using Aer.Flow.Domain;

namespace Aer.Cli;

/// <summary>
/// What every mutation command (<c>aer run</c>, <c>aer cancel</c>, <c>aer decide</c>) returns:
/// the pumped-to-fixed-point <see cref="FlowState"/> alongside the bound
/// <see cref="WorkflowDefinitionSnapshot"/> it was projected against — the snapshot is what lets a
/// caller's reporting layer resolve a paused step's declared <c>PausePoint.SupersedeTargets</c>
/// (§17.1, §17.2), which <see cref="FlowState"/> alone does not carry.
/// </summary>
public sealed record CommandResult(FlowState State, WorkflowDefinitionSnapshot Snapshot);
