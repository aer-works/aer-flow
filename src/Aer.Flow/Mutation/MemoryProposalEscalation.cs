using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Flow.Store;

namespace Aer.Flow.Mutation;

/// <summary>
/// Turns a captured <c>Aer.Mcp.Host.MemoryProposalTool</c> call into room-journal held work (#801),
/// so proposals reach the operator through the same escalation surface every other held item uses
/// (<see cref="RoomMutationInterface"/>) rather than a new one -- for the design constraint, see
/// <see cref="Aer.Mcp.Host.MemoryProposalTool"/> (#672 item 3).
/// <!-- record-once-ok: #801 src/Aer.Mcp.Host/MemoryProposalTool.cs -->
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
    /// The idempotency key is the capture file's full path, so <paramref name="captureDirectoryPath"/>
    /// must be rooted -- a relative path would resolve against the caller's current directory and
    /// mint a second ref for the same physical file under a different cwd (#801 review).
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
        if (!Path.IsPathRooted(captureDirectoryPath))
        {
            throw new ArgumentException(
                $"captureDirectoryPath must be rooted; got '{captureDirectoryPath}'. The full path is the " +
                "held-work idempotency key, and a relative path keys on the caller's current directory.",
                nameof(captureDirectoryPath));
        }

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
