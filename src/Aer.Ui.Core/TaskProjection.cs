using Aer.Flow.Domain;

namespace Aer.Ui.Core;

/// <summary>
/// A task directory's projected state, reconstructed purely from durable data (UI spec §3, §11):
/// the bound <see cref="WorkflowDefinitionSnapshot"/> a task is permanently attached to, the
/// <see cref="FlowState"/> that snapshot and its Flow Event Store project to, and the fuller
/// <see cref="ExecutionHistory"/> that state alone doesn't carry (M14 Phase 2, issue #119). The
/// Snapshot/State pairing is deliberately the same one <c>Aer.Cli.CommandResult</c> uses on the
/// write side, for the same reason — a paused step's declared <c>PausePoint.SupersedeTargets</c> is
/// only resolvable against the snapshot, never <see cref="FlowState"/> alone — but owned by
/// <c>Aer.Ui</c> rather than shared with <c>Aer.Cli</c>, since the UI is architecturally outside the
/// trusted execution stack (UI spec §2) and must not depend on it.
/// </summary>
/// <param name="Lineage">
/// Every execution's artifact-directory contents and resolved input provenance (M14 Phase 4, issue
/// #121) — a fourth read-model surface alongside <see cref="Snapshot"/>/<see cref="State"/>/
/// <see cref="History"/>, following the same "derived from the same events, owned by <c>Aer.Ui</c>"
/// shape <see cref="History"/> established (Phase 2).
/// </param>
public sealed record TaskProjection(
    WorkflowDefinitionSnapshot Snapshot, FlowState State, ExecutionHistory History, ArtifactLineage Lineage);
