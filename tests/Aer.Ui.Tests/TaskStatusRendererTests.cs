using Aer.Flow.Domain;

namespace Aer.Ui.Tests;

public class TaskStatusRendererTests
{
    [Fact]
    public void Renders_workflow_status_and_each_steps_status()
    {
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snap-1"),
            new WorkflowTemplateId("wf"),
            1,
            []);
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [
                new StepState(new StepId("architect"), StepStatus.Succeeded, new ExecutionId("exec-1"), new Dictionary<StepId, ExecutionId>()),
                new StepState(new StepId("critic"), StepStatus.Running, new ExecutionId("exec-2"), new Dictionary<StepId, ExecutionId>()),
            ],
            WorkflowStatus.Running);
        var projection = new TaskProjection(snapshot, state);

        using var output = new StringWriter();
        TaskStatusRenderer.Render(output, projection);

        var expected = string.Join(
            Environment.NewLine,
            "Workflow status: Running",
            "  architect: Succeeded",
            "  critic: Running") + Environment.NewLine;
        Assert.Equal(expected, output.ToString());
    }
}
