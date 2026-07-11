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
    public void Rejecting_a_paused_execution_that_had_succeeded_projects_it_as_Rejected()
    {
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.Reject, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Rejected, StepFor(state, Critic).Status);
    }

    [Fact]
    public void Rejecting_a_paused_execution_that_had_failed_projects_it_as_Rejected_not_Failed()
    {
        // "Equivalent in effect to exhausting RetryPolicy, but externally triggered" (§17.2):
        // Rejected is a distinct terminal status from Failed so the Retry Engine never reconsiders it.
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable),
            new FlowEvent.WorkflowPaused(executionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.Reject, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Rejected, StepFor(state, Critic).Status);
    }

    [Fact]
    public void A_paused_execution_reports_its_underlying_outcome_as_PausedOutcome()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Critic),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Succeeded, StepFor(state, Critic).PausedOutcome);
    }

    [Fact]
    public void A_step_that_is_not_currently_paused_reports_a_null_PausedOutcome()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Null(StepFor(state, Critic).PausedOutcome);
    }

    [Fact]
    public void A_step_with_no_WorkflowPaused_ever_recorded_projects_PauseRecordedForLatestExecution_false()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.False(StepFor(state, Critic).PauseRecordedForLatestExecution);
    }

    [Fact]
    public void PauseRecordedForLatestExecution_stays_true_after_resume_even_though_Status_reverts()
    {
        // §17.2's "one resolving decision per pause": Resume clears the transient Paused status,
        // but the fact that this exact ExecutionId was once paused must survive so the Pause Engine
        // never re-pauses it (§17.1).
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

        var critic = StepFor(state, Critic);
        Assert.Equal(StepStatus.Succeeded, critic.Status);
        Assert.True(critic.PauseRecordedForLatestExecution);
    }

    [Fact]
    public void A_new_attempt_after_resume_starts_with_PauseRecordedForLatestExecution_false()
    {
        // A fresh ExecutionId (e.g. via §17.2's RetryWithRevision/Supersede, landing in later
        // phases) has never itself been paused, regardless of the step's history.
        var firstAttempt = new ExecutionId("exec-1");
        var secondAttempt = new ExecutionId("exec-2");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(firstAttempt, Critic)),
            new FlowEvent.ExecutionSucceeded(firstAttempt),
            new FlowEvent.WorkflowPaused(firstAttempt, Critic),
            new FlowEvent.ExternalDecisionRecorded(decisionId, firstAttempt, DecisionType.Resume, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(secondAttempt, Critic)),
            new FlowEvent.ExecutionSucceeded(secondAttempt),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.False(StepFor(state, Critic).PauseRecordedForLatestExecution);
    }

    [Fact]
    public void An_all_pending_workflow_projects_WorkflowStatus_Terminal()
    {
        var state = StateProjector.Project([], TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Terminal, state.Status);
    }

    [Fact]
    public void A_workflow_with_a_running_step_projects_WorkflowStatus_Running()
    {
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("exec-1"), Architect)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Running, state.Status);
    }

    [Fact]
    public void A_workflow_with_a_paused_step_and_nothing_running_projects_WorkflowStatus_Paused()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Architect),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Paused, state.Status);
    }

    [Fact]
    public void Running_takes_priority_over_Paused_when_both_are_present()
    {
        var architectExecutionId = new ExecutionId("exec-1");
        var criticExecutionId = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
            new FlowEvent.ExecutionSucceeded(architectExecutionId),
            new FlowEvent.WorkflowPaused(architectExecutionId, Architect),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Running, state.Status);
    }

    [Fact]
    public void A_fully_succeeded_workflow_projects_WorkflowStatus_Terminal()
    {
        var architectExecutionId = new ExecutionId("exec-1");
        var criticExecutionId = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
            new FlowEvent.ExecutionSucceeded(architectExecutionId),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
            new FlowEvent.ExecutionSucceeded(criticExecutionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Terminal, state.Status);
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
    public void A_fail_fail_succeed_sequence_resets_the_consecutive_failure_count_to_zero()
    {
        var first = new ExecutionId("exec-1");
        var second = new ExecutionId("exec-2");
        var third = new ExecutionId("exec-3");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(first, Architect)),
            new FlowEvent.ExecutionFailed(first, FailureClassification.Retryable),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(second, Architect)),
            new FlowEvent.ExecutionFailed(second, FailureClassification.Retryable),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(third, Architect)),
            new FlowEvent.ExecutionSucceeded(third),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(0, architect.ConsecutiveFailureCount);
        Assert.Null(architect.LatestFailureClassification);
    }

    [Fact]
    public void A_fail_fail_sequence_leaves_the_consecutive_failure_count_at_two()
    {
        var first = new ExecutionId("exec-1");
        var second = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(first, Architect)),
            new FlowEvent.ExecutionFailed(first, FailureClassification.Retryable),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(second, Architect)),
            new FlowEvent.ExecutionFailed(second, FailureClassification.Permanent),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(2, architect.ConsecutiveFailureCount);
        Assert.Equal(FailureClassification.Permanent, architect.LatestFailureClassification);
    }

    [Fact]
    public void A_step_with_no_events_projects_a_zero_consecutive_failure_count_and_null_classification()
    {
        var state = StateProjector.Project([], TwoStepSnapshot());

        Assert.All(state.Steps, s =>
        {
            Assert.Equal(0, s.ConsecutiveFailureCount);
            Assert.Null(s.LatestFailureClassification);
        });
    }

    [Fact]
    public void Causal_linking_for_failure_history_is_by_ExecutionId_not_line_position()
    {
        // Architect and Critic attempts interleave in the log; each step's failure count must
        // track only its own ExecutionIds, never be confused by append order across steps.
        var architectFirst = new ExecutionId("a-1");
        var criticFirst = new ExecutionId("c-1");
        var architectSecond = new ExecutionId("a-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectFirst, Architect)),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticFirst, Critic)),
            new FlowEvent.ExecutionFailed(architectFirst, FailureClassification.Retryable),
            new FlowEvent.ExecutionSucceeded(criticFirst),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectSecond, Architect)),
            new FlowEvent.ExecutionFailed(architectSecond, FailureClassification.Retryable),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(2, StepFor(state, Architect).ConsecutiveFailureCount);
        Assert.Equal(0, StepFor(state, Critic).ConsecutiveFailureCount);
        Assert.Null(StepFor(state, Critic).LatestFailureClassification);
    }

    [Fact]
    public void RetryWithRevision_resets_the_consecutive_failure_count_for_a_fresh_retry_round()
    {
        // The externally-triggered counterpart to a success resetting the budget (M8 Phase 1):
        // an exhausted step reopened via RetryWithRevision must not still read as exhausted.
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable),
            new FlowEvent.WorkflowPaused(executionId, Architect),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.RetryWithRevision, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Failed, architect.Status);
        Assert.Equal(0, architect.ConsecutiveFailureCount);
    }

    [Fact]
    public void RetryWithRevision_with_a_SupplementaryExecutionId_projects_it_as_pending_for_the_referenced_step()
    {
        var executionId = new ExecutionId("exec-1");
        var supplementaryExecutionId = new ExecutionId("supplement-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable),
            new FlowEvent.WorkflowPaused(executionId, Architect),
            new FlowEvent.ExternalDecisionRecorded(
                decisionId, executionId, DecisionType.RetryWithRevision, null, supplementaryExecutionId),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(supplementaryExecutionId, StepFor(state, Architect).PendingSupplementaryExecutionId);
    }

    [Fact]
    public void A_newer_ExecutionRequestAccepted_clears_the_pending_supplementary_fact_for_that_step()
    {
        var firstAttempt = new ExecutionId("exec-1");
        var secondAttempt = new ExecutionId("exec-2");
        var supplementaryExecutionId = new ExecutionId("supplement-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(firstAttempt, Architect)),
            new FlowEvent.ExecutionFailed(firstAttempt, FailureClassification.Retryable),
            new FlowEvent.WorkflowPaused(firstAttempt, Architect),
            new FlowEvent.ExternalDecisionRecorded(
                decisionId, firstAttempt, DecisionType.RetryWithRevision, null, supplementaryExecutionId),
            new FlowEvent.WorkflowResumed(decisionId),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(secondAttempt, Architect)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Null(StepFor(state, Architect).PendingSupplementaryExecutionId);
    }

    [Fact]
    public void Supersede_marks_its_TargetStepId_as_a_pending_Supersede_target_with_a_pending_supplement()
    {
        var criticExecutionId = new ExecutionId("c-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
            new FlowEvent.ExecutionSucceeded(new ExecutionId("a-1")),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
            new FlowEvent.ExecutionSucceeded(criticExecutionId),
            new FlowEvent.WorkflowPaused(criticExecutionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(
                decisionId, criticExecutionId, DecisionType.Supersede, Architect, criticExecutionId),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.True(architect.IsPendingSupersedeTarget);
        Assert.Equal(criticExecutionId, architect.PendingSupplementaryExecutionId);

        // Critic itself carries no pending fact — it is the decision's referent, not its target.
        Assert.False(StepFor(state, Critic).IsPendingSupersedeTarget);
    }

    [Fact]
    public void A_newer_ExecutionRequestAccepted_for_the_target_clears_IsPendingSupersedeTarget()
    {
        var architectFirstAttempt = new ExecutionId("a-1");
        var architectSecondAttempt = new ExecutionId("a-2");
        var criticExecutionId = new ExecutionId("c-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectFirstAttempt, Architect)),
            new FlowEvent.ExecutionSucceeded(architectFirstAttempt),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
            new FlowEvent.ExecutionSucceeded(criticExecutionId),
            new FlowEvent.WorkflowPaused(criticExecutionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(
                decisionId, criticExecutionId, DecisionType.Supersede, Architect, criticExecutionId),
            new FlowEvent.WorkflowResumed(decisionId),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectSecondAttempt, Architect)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.False(architect.IsPendingSupersedeTarget);
        Assert.Null(architect.PendingSupplementaryExecutionId);
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
