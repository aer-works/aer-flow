using Aer.Flow.Domain;

namespace Aer.Cli.Tests;

/// <summary>
/// M12 Phase 3's pause-aware reporting requirement (issue #97): without a paused step's
/// <see cref="ExecutionId"/> and declared <c>SupersedeTargets</c> printed somewhere, a terminal user
/// has no way to know what to pass to <c>aer decide --execution</c>/<c>--target-step</c>.
/// </summary>
public class FlowStateReporterTests
{
    [Fact]
    public void A_paused_step_reports_its_execution_id_paused_outcome_and_supersede_targets()
    {
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snap-1"),
            new WorkflowTemplateId("wf"),
            1,
            [
                new WorkflowStepDefinition(new StepId("source"), "source", [], ["plan"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(
                    new StepId("reviewer"), "reviewer", ["plan"], ["verdict"], [new StepId("source")],
                    new RetryPolicy(1), new PausePoint([new StepId("source")])),
            ]);

        var reviewerExecutionId = new ExecutionId("exec-reviewer");
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [
                new StepState(new StepId("source"), StepStatus.Succeeded, new ExecutionId("exec-source"), new Dictionary<StepId, ExecutionId>()),
                new StepState(
                    new StepId("reviewer"), StepStatus.Paused, reviewerExecutionId, new Dictionary<StepId, ExecutionId>(),
                    PausedOutcome: StepStatus.Succeeded),
            ],
            WorkflowStatus.Paused);

        using var stringWriter = new StringWriter();
        FlowStateReporter.Report(stringWriter, new CommandResult(state, snapshot));

        var output = stringWriter.ToString();
        Assert.Contains("Workflow status: Paused", output);
        Assert.Contains($"execution={reviewerExecutionId}", output);
        Assert.Contains("outcome=Succeeded", output);
        Assert.Contains("supersede-targets: source", output);
    }

    [Fact]
    public void A_paused_step_with_no_declared_supersede_targets_reports_none()
    {
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snap-1"),
            new WorkflowTemplateId("wf"),
            1,
            [new WorkflowStepDefinition(new StepId("a"), "a", [], ["out_a"], [], new RetryPolicy(1), new PausePoint([]))]);

        var executionId = new ExecutionId("exec-a");
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [
                new StepState(
                    new StepId("a"), StepStatus.Paused, executionId, new Dictionary<StepId, ExecutionId>(),
                    PausedOutcome: StepStatus.Succeeded),
            ],
            WorkflowStatus.Paused);

        using var stringWriter = new StringWriter();
        FlowStateReporter.Report(stringWriter, new CommandResult(state, snapshot));

        Assert.Contains("supersede-targets: none", stringWriter.ToString());
    }
}
