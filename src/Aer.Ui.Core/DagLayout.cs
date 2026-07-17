using Aer.Flow.Domain;

namespace Aer.Ui.Core;

/// <summary>
/// A step's position in a layered DAG drawing (UI spec §10), computed by <see cref="DagLayoutEngine"/>
/// from a workflow's step declarations alone — never from execution state. <see cref="Rank"/> is a
/// step's longest-path distance from a root (a step with no <c>DependsOn</c>); <see cref="Column"/>
/// is its position among same-<see cref="Rank"/> steps, in declaration order. Deliberately carries no
/// projected <see cref="StepStatus"/>: overlaying status onto an already-laid-out node is a rendering
/// concern (<c>MainWindow.RenderDag</c>), kept separate so the same layout serves both a bound task
/// (status available) and a raw, not-yet-instantiated template (status view spec §5).
/// </summary>
public sealed record DagNode(
    StepId StepId,
    string Worker,
    int Rank,
    int Column,
    bool HasPausePoint,
    IReadOnlyList<StepId> SupersedeTargets);

/// <summary>
/// A directed edge in the DAG drawing: either an ordinary <c>DependsOn</c> dependency
/// (<see cref="IsSupersede"/> false), or a declared <c>PausePoint.SupersedeTargets</c> entry
/// (true) — rendered distinctly (UI spec §10) since the two mean different things: one is an
/// execution-order constraint, the other a *possible future* decision, never a constraint that has
/// happened yet.
/// </summary>
public sealed record DagEdge(StepId From, StepId To, bool IsSupersede);

/// <summary>The full layered-graph result <see cref="DagLayoutEngine.Layout"/> produces.</summary>
public sealed record DagLayout(IReadOnlyList<DagNode> Nodes, IReadOnlyList<DagEdge> Edges);
