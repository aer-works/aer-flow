using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Flow.Store;

namespace Aer.Flow.Mutation;

/// <summary>
/// The single mutation interface for holding-room journal changes (<c>room.jsonl</c>).
/// Enforces single-writer discipline and §15 concurrency locking.
/// </summary>
public static class RoomMutationInterface
{
    public static async Task<RoomState> DispatchHeldWorkAsync(
        string roomDirectoryPath,
        HeldWorkRef @ref,
        string shape,
        TimeSpan budget,
        string deciderIdentity,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(shape);
        ArgumentException.ThrowIfNullOrEmpty(deciderIdentity);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        // Rooted only: the ref is read later by processes with different working directories
        // (the daemon's watch set, a status reader), and a relative path silently resolves
        // against whichever one is reading -- the same class of bug dispatch.py's own header
        // records for relative task dirs.
        if (!Path.IsPathRooted(@ref.LaneDirectoryPath))
        {
            throw new InvalidRoomMutationException(
                $"HeldWorkRef '{@ref}' is not an absolute path; a relative lane directory would resolve against the reading process's working directory.");
        }

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath);

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        var currentState = RoomProjector.Project(existingEvents);

        if (currentState.HeldWork.ContainsKey(@ref))
        {
            throw new InvalidRoomMutationException($"HeldWorkRef '{@ref}' has already been dispatched in this room.");
        }

        var roomEvent = new RoomEvent.HeldWorkDispatched(@ref, shape, budget, deciderIdentity);
        await writer.AppendAsync(roomEvent, cancellationToken).ConfigureAwait(false);

        return RoomProjector.Project([.. existingEvents, roomEvent]);
    }

    public static async Task<RoomState> EscalateHeldWorkAsync(
        string roomDirectoryPath,
        HeldWorkRef what,
        string toWhom,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(toWhom);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath);

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        var currentState = RoomProjector.Project(existingEvents);

        if (!currentState.HeldWork.TryGetValue(what, out var item))
        {
            throw new InvalidRoomMutationException($"HeldWorkRef '{what}' was not found in this room.");
        }

        if (item.Status == HeldWorkStatus.Resolved)
        {
            throw new InvalidRoomMutationException($"HeldWorkRef '{what}' is already resolved and cannot be escalated.");
        }

        var roomEvent = new RoomEvent.HeldWorkEscalated(what, toWhom);
        await writer.AppendAsync(roomEvent, cancellationToken).ConfigureAwait(false);

        return RoomProjector.Project([.. existingEvents, roomEvent]);
    }

    public static async Task<RoomState> ResolveHeldWorkAsync(
        string roomDirectoryPath,
        HeldWorkRef @ref,
        HeldWorkCitation citation,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(citation);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath);

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        var currentState = RoomProjector.Project(existingEvents);

        return await ResolveHeldWorkLockedAsync(@ref, citation, existingEvents, currentState, writer, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The validate-and-append half of <see cref="ResolveHeldWorkAsync"/>, split out so
    /// <see cref="MemoryProposalResolution"/> can hold the SAME <see cref="ConcurrencyGuard"/>
    /// across its own "is this already resolved" check, its <c>memory/</c> file write, and this
    /// append -- three steps that must not interleave with a concurrent resolver (#672 review: a
    /// caller that checked status, released the lock, then separately called
    /// <see cref="ResolveHeldWorkAsync"/> left a window where a second resolve could apply a
    /// memory-proposal write before this method's own already-resolved check ever ran). Internal:
    /// callers outside this project acquire the lock via <see cref="ResolveHeldWorkAsync"/>
    /// instead, which still exists for every resolver that has no extra locked work to do.
    /// </summary>
    internal static async Task<RoomState> ResolveHeldWorkLockedAsync(
        HeldWorkRef @ref,
        HeldWorkCitation citation,
        IReadOnlyList<RoomEvent> existingEvents,
        RoomState currentState,
        IRoomEventLogWriter writer,
        CancellationToken cancellationToken = default)
    {
        if (!currentState.HeldWork.TryGetValue(@ref, out var item))
        {
            throw new InvalidRoomMutationException($"HeldWorkRef '{@ref}' was not found in this room.");
        }

        if (item.Status == HeldWorkStatus.Resolved)
        {
            throw new InvalidRoomMutationException($"HeldWorkRef '{@ref}' is already resolved.");
        }

        var roomEvent = new RoomEvent.HeldWorkResolved(@ref, citation);
        await writer.AppendAsync(roomEvent, cancellationToken).ConfigureAwait(false);

        return RoomProjector.Project([.. existingEvents, roomEvent]);
    }
}
