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

    /// <summary>
    /// Returns every complete Core-originated event currently in the log (spec §5.1's
    /// <c>events.jsonl</c> half, physically interleaved in the same file since M7 Phase 6's
    /// single-log decision) — the half <see cref="ReadAllAsync"/> deliberately excludes. M10 Phase 3
    /// reads this back to join Core's lifecycle facts (<c>ExecutionStarted</c>/<c>ExecutionExited</c>)
    /// to Flow's own intents by <see cref="Domain.ExecutionId"/> (§6) for crash reconciliation. Same
    /// completeness rule as <see cref="ReadAllAsync"/>: a torn trailing line is excluded, not
    /// surfaced as a parse failure.
    /// </summary>
    Task<IReadOnlyList<CoreEvent>> ReadAllCoreEventsAsync(CancellationToken cancellationToken = default);
}
