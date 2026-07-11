using Aer.Flow.Domain;

namespace Aer.Flow.Store;

/// <summary>
/// Appends Core-originated lifecycle events to the combined log (spec §5.1's <c>events.jsonl</c>
/// half — M7 Phase 6 shares one physical file with Flow's own events, permitted because §5 leaves
/// the storage backend implementation-defined). Only the Core Dispatcher calls this; Flow's own
/// mutation logic writes through <see cref="IEventLogWriter"/> instead — separate interfaces are
/// what enforce, in the type system, which half of the log a given caller may write to.
/// </summary>
public interface ICoreEventLogWriter
{
    /// <summary>
    /// Appends <paramref name="coreEvent"/> durably, with the same fsync-before-return guarantee
    /// <see cref="IEventLogWriter.AppendAsync"/> gives Flow's own events (spec §7).
    /// </summary>
    Task AppendAsync(CoreEvent coreEvent, CancellationToken cancellationToken = default);
}
