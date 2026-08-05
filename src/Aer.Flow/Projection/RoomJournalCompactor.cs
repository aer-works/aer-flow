using Aer.Flow.Domain;
using Aer.Flow.Store;

namespace Aer.Flow.Projection;

/// <summary>
/// Compacts a room's journal (<c>room.jsonl</c>) by dropping events belonging to COMPLETED runs (§972).
/// Follows existing seams (<see cref="RoomEventLogReader"/> and <see cref="RoomProjector"/>).
/// <para>
/// <b>Crash-Safe:</b> Rewrites retained entries to a temp file and atomically moves via <see cref="RetryingFileMove.Move"/>.
/// <b>Idempotent:</b> Running compaction twice in a row produces no changes on the second run.
/// <b>Scope:</b> Touches completed runs only (held work with <see cref="HeldWorkStatus.Resolved"/>).
/// Live and paused runs are untouched.
/// </para>
/// </summary>
public static class RoomJournalCompactor
{
    private const string RoomLogFileName = "room.jsonl";

    /// <summary>
    /// Compacts the room journal at <paramref name="roomDirectoryPath"/> if present.
    /// Returns <c>true</c> if the journal was compacted (shrunk), or <c>false</c> if no compaction was needed.
    /// </summary>
    public static async Task<bool> CompactAsync(
        string roomDirectoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        var roomLogPath = Path.Combine(roomDirectoryPath, RoomLogFileName);
        if (!File.Exists(roomLogPath))
        {
            return false;
        }

        var reader = new RoomEventLogReader(roomLogPath);
        var events = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);

        if (events.Count == 0)
        {
            return false;
        }

        var roomState = RoomProjector.Project(events);
        var completedRefs = roomState.HeldWork
            .Where(kv => kv.Value.Status == HeldWorkStatus.Resolved)
            .Select(kv => kv.Key)
            .ToHashSet();

        if (completedRefs.Count == 0)
        {
            return false;
        }

        var rawLines = OrchestratorSessionStore.ReadRoomLogLines(roomDirectoryPath);
        if (rawLines.Length != events.Count)
        {
            // Defensive posture: if line count does not match parsed events count, do not compact
            return false;
        }

        var retainedLines = new List<string>(rawLines.Length);
        for (int i = 0; i < events.Count; i++)
        {
            var @event = events[i];
            if (IsEventOfCompletedRun(@event, completedRefs))
            {
                continue;
            }

            retainedLines.Add(rawLines[i]);
        }

        if (retainedLines.Count == rawLines.Length)
        {
            return false;
        }

        var tempFilePath = roomLogPath + ".tmp." + Guid.NewGuid().ToString("n");
        var textContent = retainedLines.Count > 0 ? string.Join('\n', retainedLines) + "\n" : string.Empty;
        await File.WriteAllTextAsync(tempFilePath, textContent, cancellationToken).ConfigureAwait(false);

        RetryingFileMove.Move(tempFilePath, roomLogPath, overwrite: true, deleteSourceOnFinalFailure: true);
        return true;
    }

    private static bool IsEventOfCompletedRun(RoomEvent @event, HashSet<HeldWorkRef> completedRefs)
    {
        return @event switch
        {
            RoomEvent.HeldWorkDispatched dispatched => completedRefs.Contains(dispatched.Ref),
            RoomEvent.HeldWorkEscalated escalated => completedRefs.Contains(escalated.Ref),
            RoomEvent.HeldWorkResolved resolved => completedRefs.Contains(resolved.Ref),
            RoomEvent.EscalationRaised escalation => escalation.Subject is EscalationSubject.HeldWork(var @ref) && completedRefs.Contains(@ref),
            _ => false,
        };
    }
}
