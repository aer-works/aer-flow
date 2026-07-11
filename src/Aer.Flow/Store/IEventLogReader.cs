using Aer.Flow.Domain;

namespace Aer.Flow.Store;

/// <summary>
/// Reads Flow's exclusive half of the Event Store (spec §5.1) back into memory, in the order the
/// events were appended. This order is exactly the write order of a single append-only file — it
/// is not the causal-linking mechanism §6 describes (that mechanism matches events across Flow's
/// and Core's logs by shared <see cref="ExecutionId"/>, never by position), but reading one log's
/// own lines back in the order it wrote them is simply what "append-only" means.
/// </summary>
public interface IEventLogReader
{
    /// <summary>
    /// Returns every complete event currently in the log. A line with no trailing newline — a
    /// write still in flight, or a crash mid-append (§5.3) — is not yet a complete event and is
    /// excluded rather than surfaced as a parse failure.
    /// </summary>
    Task<IReadOnlyList<FlowEvent>> ReadAllAsync(CancellationToken cancellationToken = default);
}
