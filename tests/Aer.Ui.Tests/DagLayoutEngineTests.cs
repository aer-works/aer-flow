using Aer.Flow.Domain;
using Aer.Flow.Templates;

namespace Aer.Ui.Tests;

/// <summary>
/// Exercises <see cref="DagLayoutEngine.Layout"/> directly — a pure function over
/// <c>WorkflowStepDefinition</c> declarations, with no Avalonia dependency — leaving
/// <see cref="MainWindowDagTests"/> to cover the rendering that sits on top of it.
/// </summary>
public class DagLayoutEngineTests
{
    [Fact]
    public async Task Ranks_a_linear_chain_one_per_row_in_declaration_order()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);

        var layout = DagLayoutEngine.Layout(definition.Steps);

        Assert.Equal(
            [
                (new StepId("architect"), Rank: 0, Column: 0),
                (new StepId("critic"), Rank: 1, Column: 0),
                (new StepId("publisher"), Rank: 2, Column: 0),
            ],
            layout.Nodes.Select(node => (node.StepId, node.Rank, node.Column)));

        Assert.Equal(
            [
                (new StepId("architect"), new StepId("critic"), false),
                (new StepId("critic"), new StepId("publisher"), false),
            ],
            layout.Edges.Select(edge => (edge.From, edge.To, edge.IsSupersede)));
    }

    [Fact]
    public async Task Ranks_a_diamond_by_longest_path_and_carries_pause_point_and_supersede_targets()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "diamond-workflow-with-pause.json");
        var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);

        var layout = DagLayoutEngine.Layout(definition.Steps);

        Assert.Equal(
            [
                (new StepId("a"), Rank: 0, Column: 0),
                (new StepId("b"), Rank: 0, Column: 1),
                (new StepId("c"), Rank: 1, Column: 0),
                (new StepId("d"), Rank: 2, Column: 0),
            ],
            layout.Nodes.Select(node => (node.StepId, node.Rank, node.Column)));

        var nodeC = layout.Nodes.Single(node => node.StepId == new StepId("c"));
        Assert.True(nodeC.HasPausePoint);
        Assert.Equal([new StepId("a")], nodeC.SupersedeTargets);

        var nodeA = layout.Nodes.Single(node => node.StepId == new StepId("a"));
        Assert.False(nodeA.HasPausePoint);
        Assert.Empty(nodeA.SupersedeTargets);

        // Dependency edges first, in declaration order, then the supersede edge last.
        Assert.Equal(
            [
                (new StepId("a"), new StepId("c"), false),
                (new StepId("b"), new StepId("c"), false),
                (new StepId("c"), new StepId("d"), false),
                (new StepId("c"), new StepId("a"), true),
            ],
            layout.Edges.Select(edge => (edge.From, edge.To, edge.IsSupersede)));
    }

    [Fact]
    public void Layout_of_a_single_step_with_no_dependencies_is_one_node_at_the_origin_with_no_edges()
    {
        var step = new WorkflowStepDefinition(
            new StepId("solo"), "worker", Inputs: [], Outputs: [], DependsOn: [], new RetryPolicy(1));

        var layout = DagLayoutEngine.Layout([step]);

        var node = Assert.Single(layout.Nodes);
        Assert.Equal(new StepId("solo"), node.StepId);
        Assert.Equal(0, node.Rank);
        Assert.Equal(0, node.Column);
        Assert.False(node.HasPausePoint);
        Assert.Empty(layout.Edges);
    }
}
