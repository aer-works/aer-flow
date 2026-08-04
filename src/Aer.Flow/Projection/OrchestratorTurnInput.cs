using Aer.Flow.Domain;
using Aer.Flow.Store;

namespace Aer.Flow.Projection;

/// <summary>
/// Assembles what one orchestrator turn reads (§B):
/// <list type="bullet">
///   <item><description>The projected <see cref="RoomState"/> (carrying ActiveGrants + OpenEscalations).</description></item>
///   <item><description>The event delta: room events appended since the last completed turn cursor.</description></item>
///   <item><description>The current wake set passed in from the bridge.</description></item>
///   <item><description>The <see cref="RoomMemoryDocument"/>.</description></item>
/// </list>
/// <para>
/// <b>Re-schedulable Turns (§E):</b> Advancing the cursor is a separate explicit call (<see cref="CommitTurn"/> / <see cref="CommitTurnAsync"/>).
/// A crashed turn must NOT advance the cursor so that the next wake replays the same event delta.
/// </para>
/// </summary>
public sealed record OrchestratorTurnInput(
    RoomState RoomState,
    IReadOnlyList<RoomEvent> EventDelta,
    IReadOnlyList<RoomWake> Wakes,
    RoomMemoryDocument MemoryDocument,
    OrchestratorSessionCursor? InitialCursor,
    bool IsColdStart,
    int TotalEventCount)
{
    private const string RoomLogFileName = "room.jsonl";

    /// <summary>
    /// Assembles an <see cref="OrchestratorTurnInput"/> from <paramref name="roomDirectoryPath"/> and the passed-in <paramref name="wakes"/>.
    /// Reads the room event journal ONCE for both state projection and delta extraction.
    /// </summary>
    public static async Task<OrchestratorTurnInput> AssembleAsync(
        string roomDirectoryPath,
        IReadOnlyList<RoomWake> wakes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(wakes);

        var roomLogPath = Path.Combine(roomDirectoryPath, RoomLogFileName);
        var reader = new RoomEventLogReader(roomLogPath);
        var allEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);

        var roomState = RoomProjector.Project(allEvents);
        var memoryDoc = await RoomMemoryDocument.LoadAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(false);

        var cursor = OrchestratorSessionStore.Load(roomDirectoryPath);

        bool isColdStart;
        IReadOnlyList<RoomEvent> eventDelta;

        if (cursor is null)
        {
            isColdStart = true;
            eventDelta = allEvents;
        }
        else if (cursor.ProcessedEventCount > allEvents.Count)
        {
            Console.Error.WriteLine(
                $"[OrchestratorTurnInput] Fallback to cold start LOUDLY: Cursor processed count ({cursor.ProcessedEventCount}) exceeds journal length ({allEvents.Count}).");
            isColdStart = true;
            eventDelta = allEvents;
        }
        else
        {
            isColdStart = false;
            eventDelta = allEvents.Skip(cursor.ProcessedEventCount).ToList().AsReadOnly();
        }

        return new OrchestratorTurnInput(
            RoomState: roomState,
            EventDelta: eventDelta,
            Wakes: wakes,
            MemoryDocument: memoryDoc,
            InitialCursor: cursor,
            IsColdStart: isColdStart,
            TotalEventCount: allEvents.Count);
    }

    /// <summary>
    /// Explicitly advances the session cursor to <paramref name="totalEventCount"/> after a completed turn (§B).
    /// </summary>
    public static Task CommitTurnAsync(
        string roomDirectoryPath,
        int totalEventCount,
        DateTimeOffset? turnTimestamp = null,
        CancellationToken cancellationToken = default)
    {
        CommitTurn(roomDirectoryPath, totalEventCount, turnTimestamp);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Explicitly advances the session cursor to <paramref name="totalEventCount"/> after a completed turn (§B).
    /// </summary>
    public static void CommitTurn(
        string roomDirectoryPath,
        int totalEventCount,
        DateTimeOffset? turnTimestamp = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        if (totalEventCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalEventCount), "Total event count cannot be negative.");
        }

        var newCursor = new OrchestratorSessionCursor(
            ProcessedEventCount: totalEventCount,
            LastCompletedTurnAt: turnTimestamp ?? DateTimeOffset.UtcNow);

        OrchestratorSessionStore.Save(roomDirectoryPath, newCursor);
    }
}
