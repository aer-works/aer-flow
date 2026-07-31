using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Flow.Store;

namespace Aer.Flow.Mutation;

/// <summary>
/// The operator decision surface for held work (#672 item 1), and the seam where a
/// <see cref="MemoryProposalEscalation.MemoryProposalShape"/> item's approval becomes an actual
/// <c>memory/</c> write (#672 item 2, decision 0044 point 3: nothing else applies a proposal).
/// Every other <see cref="HeldWorkState.Shape"/> resolves through the same two outcomes with no
/// side effect beyond the room-journal entry <see cref="RoomMutationInterface.ResolveHeldWorkAsync"/>
/// already records -- this class does not invent per-shape behaviour for shapes it does not know.
/// <para>
/// <b>Ordering: apply, then resolve -- never the reverse.</b> The two writes (a file under
/// <c>memory/</c>, and a <see cref="RoomEvent.HeldWorkResolved"/> journal append) cannot be one
/// atomic transaction. Resolve-then-apply would let a crash between the two leave a
/// <see cref="HeldWorkStatus.Resolved"/> item whose proposal was never actually applied -- invisible,
/// because "resolved" reads as "done". Apply-then-resolve instead leaves a crash window where the
/// item is still <see cref="HeldWorkStatus.Dispatched"/>/<see cref="HeldWorkStatus.Escalated"/> (so
/// the operator's own tooling still surfaces it as pending) even though the file write already
/// landed -- proven directly by
/// <c>MemoryProposalResolutionTests.A_failure_between_apply_and_resolve_leaves_the_file_applied_but_the_item_still_pending</c>.
/// A retry in that window re-applies <see cref="MemoryProposalApplier.ApplyAsync"/> against a
/// <c>memory/</c> tree that already reflects the first attempt: <c>edit</c> is idempotent (its
/// target already exists, so it overwrites with the identical content again, harmlessly); <c>add</c>
/// and <c>delete</c> are not -- <c>add</c>'s target now already exists (post-apply guard, below) and
/// <c>delete</c>'s target is now already gone, so both fail loudly on the retry rather than silently
/// repeating or silently no-op'ing. Either way the retry's outcome is visible to the operator, never
/// a silent second write. A wedged-looking <c>add</c>/<c>delete</c> retry is not actually stuck:
/// <b>reject is the recovery path</b> -- it skips apply entirely and resolves the item outright, and
/// <c>memory/</c> already reflects the (successful) first attempt regardless.
/// </para>
/// </summary>
public static class MemoryProposalResolution
{
    public const string ApprovedEventType = "operator-approved";
    public const string RejectedEventType = "operator-rejected";

    public static async Task<RoomState> ResolveAsync(
        string roomDirectoryPath,
        HeldWorkRef @ref,
        bool approve,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        var state = RoomProjector.Project(await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false));
        if (!state.HeldWork.TryGetValue(@ref, out var item))
        {
            throw new InvalidRoomMutationException($"HeldWorkRef '{@ref}' was not found in this room.");
        }

        if (approve && item.Shape == MemoryProposalEscalation.MemoryProposalShape)
        {
            // Deliberately BEFORE the resolve below -- see this class's own remarks on ordering.
            await MemoryProposalApplier.ApplyAsync(roomDirectoryPath, @ref.Value, cancellationToken)
                .ConfigureAwait(false);
        }

        var citation = new LaneJournalCitation(
            @ref.Value, new ExecutionId(@ref.Value), approve ? ApprovedEventType : RejectedEventType);

        return await RoomMutationInterface.ResolveHeldWorkAsync(
            roomDirectoryPath, @ref, citation, reader, writer, cancellationToken).ConfigureAwait(false);
    }
}
