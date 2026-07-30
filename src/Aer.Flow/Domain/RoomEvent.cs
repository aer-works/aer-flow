using System.Text.Json.Serialization;

namespace Aer.Flow.Domain;

/// <summary>
/// The <c>room.jsonl</c> event discriminated union (held-work reference lifecycle).
/// Owner tag is <c>"owner": "room"</c> on the <see cref="LogEntry"/> envelope.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "eventType")]
[JsonDerivedType(typeof(HeldWorkDispatched), "heldWorkDispatched")]
[JsonDerivedType(typeof(HeldWorkEscalated), "heldWorkEscalated")]
[JsonDerivedType(typeof(HeldWorkResolved), "heldWorkResolved")]
public abstract record RoomEvent
{
    private RoomEvent()
    {
    }

    /// <summary>Records that a held work reference was dispatched into a lane directory.</summary>
    public sealed record HeldWorkDispatched(
        HeldWorkRef Ref,
        string Shape,
        TimeSpan Budget,
        string DeciderIdentity) : RoomEvent;

    /// <summary>Records that held work was escalated.</summary>
    public sealed record HeldWorkEscalated(
        HeldWorkRef What,
        string ToWhom) : RoomEvent;

    /// <summary>Records that held work was resolved, citing the lane's terminal event.</summary>
    public sealed record HeldWorkResolved(
        HeldWorkRef Ref,
        LaneJournalCitation Citation) : RoomEvent;
}
