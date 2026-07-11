namespace Aer.Flow.Store;

/// <summary>
/// Raised when a complete, newline-terminated line in <c>flow.jsonl</c> does not deserialize to a
/// known <see cref="Domain.FlowEvent"/>. A malformed complete line is a corruption of the source of
/// truth (spec §5) — never silently skipped — as distinct from a torn trailing line, which is
/// simply not yet a complete event (§5.3) and is excluded without error.
/// </summary>
public sealed class FlowEventLogReadException : AerFlowException
{
    public FlowEventLogReadException(string message)
        : base(message)
    {
    }

    public FlowEventLogReadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
