using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Flow.Store;

namespace Aer.Flow.Mutation;

/// <summary>
/// Turns a captured <c>Aer.Mcp.Host.MemoryProposalTool</c> call into room-journal held work (#801),
/// so a memory-edit proposal reaches the operator through the same escalation surface every other
/// held item already uses (<see cref="RoomMutationInterface"/>) rather than a new one -- #672 item 3's
/// "proposals escalate; nothing writes memory but an approved decision or the operator's own editor".
/// <para>
/// Deliberately narrow: this class only turns a capture file into a dispatched <see cref="HeldWorkRef"/>.
/// It never reads <c>memory/</c>, never applies a proposal, and never escalates or resolves one past
/// <see cref="HeldWorkStatus.Dispatched"/> -- deciding and applying a proposal is #672's other half,
/// explicitly out of #801's scope.
/// </para>
/// </summary>
public static class MemoryProposalEscalation
{
    /// <summary>
    /// The room's own placeholder budget for a memory-proposal item (#801): unlike a dispatched
    /// workflow lane, a proposal has no natural timeout of its own -- it waits on an operator
    /// decision, not a process. <see cref="TimeSpan.Zero"/> carries no live meaning today (nothing
    /// in <see cref="RoomProjector"/>/<see cref="HeldWorkReconciler"/> currently branches on
    /// <c>Budget</c>); recorded here rather than a made-up nonzero figure so a future consumer that
    /// does start reading it does not inherit an invented number.
    /// </summary>
    public static readonly TimeSpan NoBudget = TimeSpan.Zero;

    public const string MemoryProposalShape = "memory-proposal";

    /// <summary>
    /// Dispatches every capture file under <paramref name="captureDirectoryPath"/> that is not
    /// already held work in this room, in filename order. A capture file's own path becomes its
    /// <see cref="HeldWorkRef"/> -- there is no lane directory for a memory proposal, so this reuses
    /// the ref's role as "the thing to point an operator at" rather than "a lane with a flow.jsonl".
    /// Idempotent: re-running against the same directory re-dispatches nothing already recorded.
    /// </summary>
    public static async Task<RoomState> EscalateNewProposalsAsync(
        string captureDirectoryPath,
        string roomDirectoryPath,
        string deciderIdentity,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(captureDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(deciderIdentity);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        var state = RoomProjector.Project(await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false));

        if (!Directory.Exists(captureDirectoryPath))
        {
            return state;
        }

        foreach (var file in Directory.GetFiles(captureDirectoryPath, "proposal-*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var @ref = new HeldWorkRef(Path.GetFullPath(file));
            if (state.HeldWork.ContainsKey(@ref))
            {
                continue;
            }

            state = await RoomMutationInterface.DispatchHeldWorkAsync(
                roomDirectoryPath, @ref, MemoryProposalShape, NoBudget, deciderIdentity, reader, writer, cancellationToken)
                .ConfigureAwait(false);
        }

        return state;
    }
}
