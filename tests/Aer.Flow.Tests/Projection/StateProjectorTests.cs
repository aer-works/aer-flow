using System.Text.Json;
using Aer.Flow.Domain;
using Aer.Flow.Projection;

namespace Aer.Flow.Tests.Projection;

public class StateProjectorTests
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

    private static ExecutionRequest MakeRequest(ExecutionId executionId, StepId stepId)
        => new(
            executionId,
            new WorkflowId("wf-1"),
            stepId,
            "worker",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    private static StepState StepFor(FlowState state, StepId stepId) => Assert.Single(state.Steps, s => s.StepId == stepId);

    [Fact]
    public void A_step_with_no_events_at_all_projects_as_Pending()
    {
        var state = StateProjector.Project([], TwoStepSnapshot());

        Assert.All(state.Steps, s =>
        {
            Assert.Equal(StepStatus.Pending, s.Status);
            Assert.Null(s.LatestExecutionId);
        });
    }

    [Fact]
    public void An_accepted_request_with_no_terminal_event_yet_projects_as_Running()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Running, architect.Status);
        Assert.Equal(executionId, architect.LatestExecutionId);
    }

    [Fact]
    public void A_succeeded_execution_projects_as_Succeeded()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Succeeded, StepFor(state, Architect).Status);
    }

    [Fact]
    public void A_failed_execution_projects_as_Failed()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Failed, StepFor(state, Architect).Status);
    }

    [Fact]
    public void A_cancelled_execution_projects_as_Cancelled()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.CancellationRequested(executionId),
            new FlowEvent.ExecutionCancelled(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Cancelled, StepFor(state, Architect).Status);
    }

    [Fact]
    public void Only_the_most_recently_accepted_attempt_determines_a_steps_status()
    {
        var firstAttempt = new ExecutionId("exec-1");
        var secondAttempt = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(firstAttempt, Architect)),
            new FlowEvent.ExecutionFailed(firstAttempt, FailureClassification.Retryable),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(secondAttempt, Architect)),
            new FlowEvent.ExecutionSucceeded(secondAttempt),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Succeeded, architect.Status);
        Assert.Equal(secondAttempt, architect.LatestExecutionId);
    }

    [Fact]
    public void A_paused_execution_projects_as_Paused_even_though_it_already_reached_a_terminal_outcome()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Critic),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Paused, StepFor(state, Critic).Status);
    }

    [Fact]
    public void Resuming_a_paused_execution_reverts_it_to_its_underlying_terminal_status()
    {
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.Resume, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Succeeded, StepFor(state, Critic).Status);
    }

    [Fact]
    public void A_rejected_request_never_having_been_accepted_leaves_the_step_Pending()
    {
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestRejected(new ExecutionId("exec-1"), "concurrency cap reached"),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Pending, architect.Status);
        Assert.Null(architect.LatestExecutionId);
    }

    [Fact]
    public void Projecting_the_same_events_twice_produces_an_identical_result()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };
        var snapshot = TwoStepSnapshot();

        var first = StateProjector.Project(events, snapshot);
        var second = StateProjector.Project(events, snapshot);

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }
}
