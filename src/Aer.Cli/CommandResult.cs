using Aer.Flow.Domain;

namespace Aer.Cli;

/// <summary>
/// What every mutation command (<c>aer run</c>, <c>aer cancel</c>, <c>aer decide</c>) returns:
/// the pumped-to-fixed-point <see cref="FlowState"/> alongside the bound
/// <see cref="WorkflowDefinitionSnapshot"/> it was projected against — the snapshot is what lets a
/// caller's reporting layer resolve a paused step's declared <c>PausePoint.SupersedeTargets</c>
/// (§17.1, §17.2), which <see cref="FlowState"/> alone does not carry.
/// </summary>
/// <param name="ResumedFromSnapshot">
/// Whether this call ran the task directory's already-bound snapshot rather than binding the
/// workflow file it was given (#628). Only <c>aer run</c> can bind one at all, so every other
/// command leaves this at its default — they resume by definition, and saying so per-command would
/// be noise rather than news.
/// </param>
public sealed record CommandResult(
    FlowState State,
    WorkflowDefinitionSnapshot Snapshot,
    bool ResumedFromSnapshot = false,
    string? TaskDirectoryPath = null);

