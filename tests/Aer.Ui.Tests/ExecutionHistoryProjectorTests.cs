using Aer.Flow.Domain;

namespace Aer.Ui.Tests;

/// <summary>
/// Unit-level coverage for <see cref="ExecutionHistoryProjector"/> (M14 Phase 2, issue #119),
/// mirroring <c>Aer.Flow.Tests.Projection.StateProjectorTests</c>' style of building
/// <see cref="FlowEvent"/> lists by hand — the read-model surface this projector adds is exactly
/// the per-execution history <c>StateProjector.Project</c> collapses to each step's latest attempt.
/// </summary>
public class ExecutionHistoryProjectorTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");

    private static WorkflowDefinitionSnapshot TwoStepSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("architect-critic"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(3)),
            new WorkflowStepDefinition(
                Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1),
                PausePoint: new PausePoint(SupersedeTargets: [Architect])),
        ]);

    /// <summary>An ordinary process dispatch — always a real (non-null) <c>Timeout</c>.</summary>
    private static ExecutionRequest MakeRequest(ExecutionId executionId, StepId? stepId, string worker = "worker")
        => new(
            executionId,
            new WorkflowId("wf-1"),
            stepId,
            worker,
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    /// <summary>
    /// A non-process dispatch (spec §17.3) — always a <c>null</c> <c>Timeout</c>, the recorded
    /// signal <see cref="ExecutionHistoryProjector"/> uses for <c>IsNonProcess</c>. A distinct
    /// helper from <see cref="MakeRequest"/>, not an optional parameter on it, so a test can never
    /// accidentally get the wrong one via a defaulted argument.
    /// </summary>
    private static ExecutionRequest MakeNonProcessRequest(ExecutionId executionId, StepId? stepId, string worker = "human")
        => new(
            executionId,
            new WorkflowId("wf-1"),
            stepId,
            worker,
            Inputs: [],
            Outputs: [],
            Timeout: null,
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    [Fact]
    public void A_step_with_no_attempts_projects_an_empty_list_not_a_missing_entry()
    {
        var history = ExecutionHistoryProjector.Project([], TwoStepSnapshot());

        Assert.Empty(history.AttemptsByStepId[Architect]);
        Assert.Empty(history.AttemptsByStepId[Critic]);
    }

    [Fact]
    public void Every_attempt_a_step_ever_had_is_retained_not_just_the_latest()
    {
        var first = new ExecutionId("exec-1");
        var second = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(first, Architect)),
            new FlowEvent.ExecutionFailed(first, FailureClassification.Retryable),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(second, Architect)),
            new FlowEvent.ExecutionSucceeded(second),
        };

        var history = ExecutionHistoryProjector.Project(events, TwoStepSnapshot());

        var attempts = history.AttemptsByStepId[Architect];
        Assert.Equal(2, attempts.Count);
        Assert.Equal(first, attempts[0].ExecutionId);
        Assert.Equal(StepStatus.Failed, attempts[0].Status);
        Assert.Equal(FailureClassification.Retryable, attempts[0].FailureClassification);
        Assert.Equal(second, attempts[1].ExecutionId);
        Assert.Equal(StepStatus.Succeeded, attempts[1].Status);
        Assert.Null(attempts[1].FailureClassification);
    }

    [Fact]
    public void An_execution_with_no_terminal_event_projects_as_Running()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[] { new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)) };

        var history = ExecutionHistoryProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Running, Assert.Single(history.AttemptsByStepId[Architect]).Status);
    }

    [Fact]
    public void An_execution_with_a_null_Timeout_is_flagged_non_process()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeNonProcessRequest(executionId, Critic)),
        };

        var history = ExecutionHistoryProjector.Project(events, TwoStepSnapshot());

        var attempt = Assert.Single(history.AttemptsByStepId[Critic]);
        Assert.True(attempt.IsNonProcess);
        Assert.Equal("human", attempt.Worker);
    }

    [Fact]
    public void An_execution_with_a_real_Timeout_is_not_flagged_non_process()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
        };

        var history = ExecutionHistoryProjector.Project(events, TwoStepSnapshot());

        Assert.False(Assert.Single(history.AttemptsByStepId[Architect]).IsNonProcess);
    }

    [Fact]
    public void A_paused_attempt_projects_as_Paused_masking_its_underlying_outcome()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Critic),
        };

        var history = ExecutionHistoryProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Paused, Assert.Single(history.AttemptsByStepId[Critic]).Status);
    }

    [Fact]
    public void Resuming_a_paused_attempt_reverts_it_to_its_underlying_outcome()
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

        var history = ExecutionHistoryProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Succeeded, Assert.Single(history.AttemptsByStepId[Critic]).Status);
    }

    [Fact]
    public void Rejecting_a_paused_attempt_projects_it_as_Rejected_without_touching_earlier_attempts()
    {
        var firstAttempt = new ExecutionId("exec-1");
        var secondAttempt = new ExecutionId("exec-2");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(firstAttempt, Critic)),
            new FlowEvent.ExecutionFailed(firstAttempt, FailureClassification.Retryable),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(secondAttempt, Critic)),
            new FlowEvent.ExecutionSucceeded(secondAttempt),
            new FlowEvent.WorkflowPaused(secondAttempt, Critic),
            new FlowEvent.ExternalDecisionRecorded(decisionId, secondAttempt, DecisionType.Reject, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var history = ExecutionHistoryProjector.Project(events, TwoStepSnapshot());

        var attempts = history.AttemptsByStepId[Critic];
        Assert.Equal(StepStatus.Failed, attempts[0].Status);
        Assert.Equal(StepStatus.Rejected, attempts[1].Status);
    }

    [Fact]
    public void A_settled_step_less_execution_still_appears_unlike_FlowState_StepLessExecutions()
    {
        var executionId = new ExecutionId("supplement-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeNonProcessRequest(executionId, null)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };

        var history = ExecutionHistoryProjector.Project(events, TwoStepSnapshot());

        var stepLess = Assert.Single(history.StepLessExecutions);
        Assert.Equal(executionId, stepLess.ExecutionId);
        Assert.Equal(StepStatus.Succeeded, stepLess.Status);
        Assert.True(stepLess.IsNonProcess);

        // Never perturbs any step's own attempt history — a step-less execution belongs to no StepId.
        Assert.Empty(history.AttemptsByStepId[Architect]);
        Assert.Empty(history.AttemptsByStepId[Critic]);
    }

    [Fact]
    public void An_unresolved_decision_is_recorded_with_Resolved_false()
    {
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.Resume, null, null),
        };

        var history = ExecutionHistoryProjector.Project(events, TwoStepSnapshot());

        var decision = Assert.Single(history.Decisions);
        Assert.Equal(decisionId, decision.DecisionId);
        Assert.False(decision.Resolved);
    }

    [Fact]
    public void A_resolved_decision_is_recorded_with_Resolved_true_and_its_full_detail()
    {
        var criticExecutionId = new ExecutionId("c-1");
        var supplementExecutionId = new ExecutionId("supplement-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
            new FlowEvent.ExecutionSucceeded(new ExecutionId("a-1")),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
            new FlowEvent.ExecutionSucceeded(criticExecutionId),
            new FlowEvent.WorkflowPaused(criticExecutionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(
                decisionId, criticExecutionId, DecisionType.Supersede, Architect, supplementExecutionId),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var history = ExecutionHistoryProjector.Project(events, TwoStepSnapshot());

        var decision = Assert.Single(history.Decisions);
        Assert.True(decision.Resolved);
        Assert.Equal(DecisionType.Supersede, decision.DecisionType);
        Assert.Equal(criticExecutionId, decision.ReferencedExecutionId);
        Assert.Equal(Architect, decision.TargetStepId);
        Assert.Equal(supplementExecutionId, decision.SupplementaryExecutionId);
    }

    [Fact]
    public void Multiple_decisions_across_the_log_are_all_retained_in_recorded_order()
    {
        var firstExecutionId = new ExecutionId("exec-1");
        var secondExecutionId = new ExecutionId("exec-2");
        var firstDecisionId = new DecisionId("decision-1");
        var secondDecisionId = new DecisionId("decision-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(firstExecutionId, Critic)),
            new FlowEvent.ExecutionFailed(firstExecutionId, FailureClassification.Retryable),
            new FlowEvent.WorkflowPaused(firstExecutionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(firstDecisionId, firstExecutionId, DecisionType.RetryWithRevision, null, null),
            new FlowEvent.WorkflowResumed(firstDecisionId),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(secondExecutionId, Critic)),
            new FlowEvent.ExecutionSucceeded(secondExecutionId),
            new FlowEvent.WorkflowPaused(secondExecutionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(secondDecisionId, secondExecutionId, DecisionType.Resume, null, null),
            new FlowEvent.WorkflowResumed(secondDecisionId),
        };

        var history = ExecutionHistoryProjector.Project(events, TwoStepSnapshot());

        Assert.Equal([firstDecisionId, secondDecisionId], history.Decisions.Select(d => d.DecisionId));
    }
}
