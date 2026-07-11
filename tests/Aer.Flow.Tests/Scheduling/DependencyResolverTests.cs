using Aer.Flow.Domain;
using Aer.Flow.Scheduling;

namespace Aer.Flow.Tests.Scheduling;

public class DependencyResolverTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");

    private static WorkflowDefinitionSnapshot TwoStepSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("architect-critic"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
            new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
        ]);

    private static readonly IReadOnlyDictionary<StepId, ExecutionId> NoUpstream = new Dictionary<StepId, ExecutionId>();

    private static StepState Pending(StepId stepId) => new(stepId, StepStatus.Pending, LatestExecutionId: null, NoUpstream);

    private static StepState Terminal(StepId stepId, StepStatus status, ExecutionId executionId) =>
        new(stepId, status, executionId, NoUpstream);

    private static StepState SucceededUsing(StepId stepId, ExecutionId executionId, StepId dependencyStepId, ExecutionId upstreamExecutionId) =>
        new(stepId, StepStatus.Succeeded, executionId, new Dictionary<StepId, ExecutionId> { [dependencyStepId] = upstreamExecutionId });

    [Fact]
    public void A_step_with_no_dependencies_is_immediately_ready()
    {
        var state = new FlowState(new WorkflowDefinitionSnapshotId("snapshot-1"), [Pending(Architect), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot());

        Assert.Contains(Architect, ready);
    }

    [Fact]
    public void A_step_whose_dependency_succeeded_becomes_ready()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Succeeded, new ExecutionId("A1")), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot());

        Assert.Contains(Critic, ready);
    }

    [Fact]
    public void A_step_whose_dependency_failed_is_not_ready()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Failed, new ExecutionId("A1")), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot());

        Assert.DoesNotContain(Critic, ready);
    }

    [Fact]
    public void A_step_already_succeeded_with_upstream_still_current_is_not_re_queued()
    {
        var architectExecutionId = new ExecutionId("A1");
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Terminal(Architect, StepStatus.Succeeded, architectExecutionId),
                SucceededUsing(Critic, new ExecutionId("C1"), Architect, architectExecutionId),
            ]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot());

        Assert.DoesNotContain(Critic, ready);
    }

    [Fact]
    public void A_step_already_succeeded_whose_dependency_was_since_superseded_becomes_ready_again()
    {
        // §17.5: Architect's original success was A1; Critic ran against A1. Architect was
        // superseded and now has a newer success A2 — Critic's recorded upstream (A1) no longer
        // matches Architect's current latest success, so Critic is stale and ready to rerun.
        var supersededArchitectExecutionId = new ExecutionId("A1");
        var currentArchitectExecutionId = new ExecutionId("A2");
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Terminal(Architect, StepStatus.Succeeded, currentArchitectExecutionId),
                SucceededUsing(Critic, new ExecutionId("C1"), Architect, supersededArchitectExecutionId),
            ]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot());

        Assert.Contains(Critic, ready);
    }

    [Fact]
    public void A_step_with_an_execution_already_running_is_not_ready()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Running, new ExecutionId("A1")), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot());

        Assert.DoesNotContain(Architect, ready);
    }

    [Fact]
    public void A_paused_step_is_not_ready()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Paused, new ExecutionId("A1")), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot());

        Assert.DoesNotContain(Architect, ready);
    }

    [Fact]
    public void A_step_that_already_failed_is_not_re_queued_without_a_retry_engine()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Failed, new ExecutionId("A1")), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot());

        Assert.DoesNotContain(Architect, ready);
    }
}
