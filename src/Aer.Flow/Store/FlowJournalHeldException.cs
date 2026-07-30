namespace Aer.Flow.Store;

/// <summary>
/// Raised when opening <c>flow.jsonl</c> for append fails because another process already holds
/// it open (#816) — a live <c>aer run</c> engine driving this same task, most likely, since that
/// command keeps its <see cref="FlowEventLogWriter"/> open for the pump's whole duration rather
/// than per call. Distinct from <see cref="Aer.Flow.Concurrency.WorkflowLockedException"/>: that guards
/// <c>flow.lock</c>, which every mutation call only holds transiently, so it does not catch a
/// long-lived writer holding the journal itself.
/// </summary>
public sealed class FlowJournalHeldException : AerFlowException
{
    public FlowJournalHeldException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
