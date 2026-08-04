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
[JsonDerivedType(typeof(GrantRecorded), "grantRecorded")]
[JsonDerivedType(typeof(GrantAmended), "grantAmended")]
[JsonDerivedType(typeof(GrantRevoked), "grantRevoked")]
[JsonDerivedType(typeof(EscalationRaised), "escalationRaised")]
[JsonDerivedType(typeof(TurnHostDormancyEntered), "turnHostDormancyEntered")]
[JsonDerivedType(typeof(TurnHostDormancyCleared), "turnHostDormancyCleared")]
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
        HeldWorkRef Ref,
        string ToWhom) : RoomEvent;

    /// <summary>Records that held work was resolved, citing the thing it was decided on.</summary>
    public sealed record HeldWorkResolved(
        HeldWorkRef Ref,
        HeldWorkCitation Citation) : RoomEvent;

    /// <summary>Records a grant given to a worker (§D).</summary>
    public sealed record GrantRecorded(
        GrantId GrantId,
        WorkerId WorkerId,
        GrantLevel Level,
        GrantScope Scope,
        SpendBounds SpendBounds,
        string Grantor,
        DateTimeOffset Timestamp) : RoomEvent;

    /// <summary>Records an amendment to a grant (§D).</summary>
    public sealed record GrantAmended(
        GrantId GrantId,
        GrantId AmendsGrantId,
        WorkerId WorkerId,
        GrantLevel Level,
        GrantScope Scope,
        SpendBounds SpendBounds,
        string Grantor,
        DateTimeOffset Timestamp) : RoomEvent;

    /// <summary>Records revocation of a grant (§D).</summary>
    public sealed record GrantRevoked(
        GrantId GrantId,
        string Revoker,
        DateTimeOffset Timestamp,
        string Reason) : RoomEvent;

    /// <summary>Records an escalation raised by a worker (§D).</summary>
    public sealed record EscalationRaised(
        WorkerId FromWorkerId,
        EscalationTrigger Trigger,
        EscalationSubject Subject,
        DateTimeOffset Timestamp) : RoomEvent;

    /// <summary>Records that the turn host entered dormancy due to consecutive failures.</summary>
    public sealed record TurnHostDormancyEntered(
        int ConsecutiveFailures,
        DateTimeOffset Timestamp) : RoomEvent;

    /// <summary>Records that turn host dormancy was cleared.</summary>
    public sealed record TurnHostDormancyCleared(
        string ClearedBy,
        DateTimeOffset Timestamp) : RoomEvent;
}

