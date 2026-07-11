namespace Aer.Flow.Concurrency;

/// <summary>
/// Raised when <see cref="ConcurrencyGuard.Acquire"/> cannot obtain a task's file lock because
/// another Flow instance already holds it (spec §15's "at most one writer per task namespace"
/// guarantee).
/// </summary>
public sealed class WorkflowLockedException : AerFlowException
{
    public WorkflowLockedException(string message)
        : base(message)
    {
    }

    public WorkflowLockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
