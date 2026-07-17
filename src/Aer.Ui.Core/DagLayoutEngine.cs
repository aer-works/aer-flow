using Aer.Flow.Domain;

namespace Aer.Ui.Core;

/// <summary>
/// Computes a <see cref="DagLayout"/> from a workflow's step declarations — the same
/// <c>IReadOnlyList&lt;WorkflowStepDefinition&gt;</c> shape both <see cref="WorkflowDefinition"/>
/// (a raw template) and <see cref="WorkflowDefinitionSnapshot"/> (a bound task) expose, so one
/// layout serves both (UI spec §10's "graph view over both bound tasks and raw templates", issue
/// #120). A pure function of that list alone: given identical steps, it produces identical
/// <see cref="DagLayout.Nodes"/>/<see cref="DagLayout.Edges"/> ordering every time (UI spec §11) —
/// output order is always driven by walking the input <paramref name="steps"/> list and each step's
/// own <c>DependsOn</c>/<c>SupersedeTargets</c> list, never by <see cref="Dictionary{TKey,TValue}"/>
/// or <see cref="HashSet{T}"/> enumeration order, which .NET does not guarantee is stable.
/// </summary>
public static class DagLayoutEngine
{
    /// <summary>
    /// <paramref name="steps"/> is assumed already structurally valid (spec §11.1: acyclic, every
    /// <c>DependsOn</c> reference resolvable) — <see cref="Templates.WorkflowDefinitionValidator"/>
    /// enforces that at parse/bind time, before either a template or a snapshot ever reaches this
    /// layer, so this method does not re-detect cycles or dangling references itself.
    /// </summary>
    public static DagLayout Layout(IReadOnlyList<WorkflowStepDefinition> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var stepById = steps.ToDictionary(step => step.StepId);
        var rankByStepId = new Dictionary<StepId, int>();

        int RankOf(StepId stepId)
        {
            if (rankByStepId.TryGetValue(stepId, out var cachedRank))
            {
                return cachedRank;
            }

            var step = stepById[stepId];
            var rank = step.DependsOn.Count == 0 ? 0 : step.DependsOn.Max(RankOf) + 1;
            rankByStepId[stepId] = rank;
            return rank;
        }

        // Indexed directly by rank, so the final flattened node order is rank-ascending regardless
        // of the order RankOf happens to resolve each step in.
        var nodesByRank = new List<List<DagNode>>();
        foreach (var step in steps)
        {
            var rank = RankOf(step.StepId);
            while (nodesByRank.Count <= rank)
            {
                nodesByRank.Add([]);
            }

            var supersedeTargets = step.PausePoint?.SupersedeTargets ?? [];
            nodesByRank[rank].Add(new DagNode(
                step.StepId,
                step.Worker,
                rank,
                Column: nodesByRank[rank].Count,
                HasPausePoint: step.PausePoint is not null,
                supersedeTargets));
        }

        var nodes = nodesByRank.SelectMany(rankNodes => rankNodes).ToList();

        var edges = new List<DagEdge>();
        foreach (var step in steps)
        {
            foreach (var dependency in step.DependsOn)
            {
                edges.Add(new DagEdge(dependency, step.StepId, IsSupersede: false));
            }
        }

        // Supersede edges are appended after every dependency edge, rather than interleaved
        // per-step, so an edge list's first N entries are always the DependsOn graph alone.
        foreach (var step in steps)
        {
            if (step.PausePoint is not { SupersedeTargets.Count: > 0 } pausePoint)
            {
                continue;
            }

            foreach (var target in pausePoint.SupersedeTargets)
            {
                edges.Add(new DagEdge(step.StepId, target, IsSupersede: true));
            }
        }

        return new DagLayout(nodes, edges);
    }
}
