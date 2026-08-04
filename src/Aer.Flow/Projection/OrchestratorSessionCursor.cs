using System.Text.Json.Serialization;

namespace Aer.Flow.Projection;

/// <summary>
/// Small engine session metadata record (§A) stored at <c>{room}/.aer/orchestrator-session.json</c>.
/// Holds the count of room events already processed by the last completed turn and the wall-clock of that turn.
/// Never recorded as a room event (0016 boundary).
/// <para>
/// Cold start (missing or corrupt cursor file) reconstructs state from the room record alone.
/// Conversational nuance since the last recorded state may be lost — that is the DESIGN (§A).
/// </para>
/// <para>
/// <b>Landmine for #903's retention path:</b> the count carries no content identity (no hash, no
/// last-event id). A cursor LARGER than the journal fails loudly to cold start — but a journal
/// compaction/rewrite that changes which events the counts refer to WITHOUT shrinking below the
/// cursor would yield a silently wrong delta. Nothing rewrites room.jsonl today; whoever builds
/// #903 must either give this cursor content identity or reset it on compaction. Recorded on
/// #903 as well.
/// </para>
/// </summary>
public sealed record OrchestratorSessionCursor(
    [property: JsonPropertyName("processedEventCount")] int ProcessedEventCount,
    [property: JsonPropertyName("lastCompletedTurnAt")] DateTimeOffset LastCompletedTurnAt);
