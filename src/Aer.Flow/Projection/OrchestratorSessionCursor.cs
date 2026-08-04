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
/// </summary>
public sealed record OrchestratorSessionCursor(
    [property: JsonPropertyName("processedEventCount")] int ProcessedEventCount,
    [property: JsonPropertyName("lastCompletedTurnAt")] DateTimeOffset LastCompletedTurnAt);
