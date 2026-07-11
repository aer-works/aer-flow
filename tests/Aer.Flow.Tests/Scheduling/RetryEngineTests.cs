using Aer.Flow.Domain;
using Aer.Flow.Scheduling;

namespace Aer.Flow.Tests.Scheduling;

public class RetryEngineTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly ExecutionId ExecutionId = new("exec-1");
    private static readonly IReadOnlyDictionary<StepId, ExecutionId> NoUpstream = new Dictionary<StepId, ExecutionId>();

    private static StepState Failed(int consecutiveFailureCount, FailureClassification? classification = null) =>
        new(Architect, StepStatus.Failed, ExecutionId, NoUpstream, consecutiveFailureCount, classification);

    [Fact]
    public void A_retryable_failure_under_budget_may_retry()
    {
        var mayRetry = RetryEngine.MayRetry(Failed(consecutiveFailureCount: 1), new RetryPolicy(MaxAttempts: 3));

        Assert.True(mayRetry);
    }

    [Fact]
    public void An_absent_classification_defaults_to_retryable()
    {
        var mayRetry = RetryEngine.MayRetry(
            Failed(consecutiveFailureCount: 0, classification: null),
            new RetryPolicy(MaxAttempts: 1));

        Assert.True(mayRetry);
    }

    [Fact]
    public void A_Permanent_classification_short_circuits_retry_regardless_of_remaining_budget()
    {
        var mayRetry = RetryEngine.MayRetry(
            Failed(consecutiveFailureCount: 0, FailureClassification.Permanent),
            new RetryPolicy(MaxAttempts: 10));

        Assert.False(mayRetry);
    }

    [Fact]
    public void An_exhausted_budget_may_not_retry()
    {
        var mayRetry = RetryEngine.MayRetry(Failed(consecutiveFailureCount: 3), new RetryPolicy(MaxAttempts: 3));

        Assert.False(mayRetry);
    }

    [Fact]
    public void A_budget_of_one_more_than_the_current_failure_count_may_still_retry()
    {
        var mayRetry = RetryEngine.MayRetry(Failed(consecutiveFailureCount: 2), new RetryPolicy(MaxAttempts: 3));

        Assert.True(mayRetry);
    }

    [Theory]
    [InlineData(StepStatus.Succeeded)]
    [InlineData(StepStatus.Pending)]
    [InlineData(StepStatus.Running)]
    [InlineData(StepStatus.Paused)]
    [InlineData(StepStatus.Cancelled)]
    public void A_step_whose_latest_attempt_is_not_Failed_may_not_retry(StepStatus status)
    {
        var stepState = new StepState(Architect, status, ExecutionId, NoUpstream);

        var mayRetry = RetryEngine.MayRetry(stepState, new RetryPolicy(MaxAttempts: 5));

        Assert.False(mayRetry);
    }
}
