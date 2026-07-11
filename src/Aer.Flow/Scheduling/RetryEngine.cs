using Aer.Flow.Domain;

namespace Aer.Flow.Scheduling;

/// <summary>
/// Capability 10 (spec §10): decides whether a step's latest failed attempt is eligible for a
/// brand-new <see cref="ExecutionRequest"/>. A pure predicate over <see cref="StepState"/> and
/// <see cref="RetryPolicy"/> — no I/O, no dispatch — consulted by the <see cref="DependencyResolver"/>.
/// </summary>
public static class RetryEngine
{
    /// <summary>
    /// True exactly when <paramref name="stepState"/>'s latest attempt is <see cref="StepStatus.Failed"/>,
    /// its <see cref="StepState.LatestFailureClassification"/> is not <see cref="FailureClassification.Permanent"/>
    /// (absent or unrecognized defaults to <see cref="FailureClassification.Retryable"/>, §8.1), and
    /// <see cref="StepState.ConsecutiveFailureCount"/> has not yet reached <paramref name="retryPolicy"/>'s
    /// <see cref="RetryPolicy.MaxAttempts"/> — the total number of attempts allowed per round.
    /// <see cref="StepStatus.Cancelled"/> is never retried regardless of policy (§9, §10): cancellation is a
    /// decision to stop, not a failure to route around, and this predicate only ever returns true for a
    /// step whose latest attempt is <see cref="StepStatus.Failed"/>.
    /// </summary>
    public static bool MayRetry(StepState stepState, RetryPolicy retryPolicy)
    {
        ArgumentNullException.ThrowIfNull(stepState);
        ArgumentNullException.ThrowIfNull(retryPolicy);

        return stepState.Status == StepStatus.Failed
            && stepState.LatestFailureClassification != FailureClassification.Permanent
            && stepState.ConsecutiveFailureCount < retryPolicy.MaxAttempts;
    }
}
