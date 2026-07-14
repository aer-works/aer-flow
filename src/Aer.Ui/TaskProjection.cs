using Aer.Flow.Domain;

namespace Aer.Ui;

/// <summary>
/// A task directory's projected state, reconstructed purely from durable data (UI spec §3, §11):
/// the bound <see cref="WorkflowDefinitionSnapshot"/> a task is permanently attached to, alongside
/// the <see cref="FlowState"/> that snapshot and its Flow Event Store project to. Deliberately the
/// same pairing <c>Aer.Cli.CommandResult</c> uses on the write side, for the same reason — a
/// paused step's declared <c>PausePoint.SupersedeTargets</c> is only resolvable against the
/// snapshot, never <see cref="FlowState"/> alone — but owned by <c>Aer.Ui</c> rather than shared
/// with <c>Aer.Cli</c>, since the UI is architecturally outside the trusted execution stack
/// (UI spec §2) and must not depend on it.
/// </summary>
public sealed record TaskProjection(WorkflowDefinitionSnapshot Snapshot, FlowState State);
