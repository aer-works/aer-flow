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
        LaneJournalCitation citation,
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
