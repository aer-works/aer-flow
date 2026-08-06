namespace Aer.Flow.Concurrency;

/// <summary>
/// Raised when <see cref="ConcurrencyGuard.Acquire"/> cannot obtain a room's file lock because
/// another Flow instance already holds it (spec §15's "at most one writer per room namespace"
/// guarantee).
/// </summary>
public sealed class WorkflowLockedException : AerFlowException
{
    public string? HolderDescription { get; }
    public DateTime? AcquiredAtUtc { get; }

    public WorkflowLockedException(string message, string? holderDescription = null, DateTime? acquiredAtUtc = null)
        : base(message)
    {
        HolderDescription = holderDescription;
        AcquiredAtUtc = acquiredAtUtc;
    }

    public WorkflowLockedException(string message, Exception innerException, string? holderDescription = null, DateTime? acquiredAtUtc = null)
        : base(message, innerException)
    {
        HolderDescription = holderDescription;
        AcquiredAtUtc = acquiredAtUtc;
    }
}
