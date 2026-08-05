using Aer.Flow.Domain;
using Aer.Flow.Projection;

namespace Aer.Daemon;

/// <summary>
/// #1001: an occupant escalation's subject must resolve to something real before it is appended —
/// the first live turn wrote a fabricated DecisionId into a room journal because nothing checked.
/// Shape validation is <see cref="OccupantTurnActions.Parse"/>'s job; this is the reference check
/// against the projected room record (and the template catalog for originations). It lives on the
/// occupant path, not in <c>RoomMutationInterface</c>: the mutation interface serves trusted engine
/// callers (the host's own hostCondition escalations self-describe), while this guards the one
/// place untrusted model output becomes room events. A failed validation fails the whole turn —
/// same posture as a parse failure, and it counts toward the dormancy breaker upstream.
/// </summary>
public static class OccupantEscalationValidation
{
    /// <summary>Returns null when every subject resolves, else the reason the turn must fail.</summary>
    public static string? Validate(
        IReadOnlyList<OccupantEscalation> escalations,
        RoomState roomState,
        IReadOnlySet<string> knownTemplateIds)
    {
        ArgumentNullException.ThrowIfNull(escalations);
        ArgumentNullException.ThrowIfNull(roomState);
        ArgumentNullException.ThrowIfNull(knownTemplateIds);

        foreach (var escalation in escalations)
        {
            switch (escalation.Subject)
            {
                case EscalationSubject.HeldWork heldWork:
                    if (!roomState.HeldWork.ContainsKey(heldWork.Ref))
                    {
                        return $"HeldWork escalation cites ref '{heldWork.Ref.Value}', which the room record does not contain.";
                    }
                    break;

                case EscalationSubject.Decision decision:
                    // The room record carries no decisions today (HeldWorkCitation deliberately
                    // cites by free string, and decision events live in lane journals), so no
                    // occupant-cited DecisionId can currently resolve. The check is written
                    // against the record all the same: when decisions become room-record
                    // citable, valid citations start passing here without a code change.
                    return $"Decision escalation cites decisionId '{decision.DecisionId.Value}', which the room record does not contain.";

                case EscalationSubject.ProposedOrigination origination:
                    if (!knownTemplateIds.Contains(origination.TemplateId.Value))
                    {
                        return $"Origination escalation cites template '{origination.TemplateId.Value}', which the catalog does not contain.";
                    }
                    break;

                case EscalationSubject.HostCondition:
                    // Self-describing by design — but it is the HOST's kind, and an occupant
                    // asserting a host-observed condition is itself a fabrication.
                    return "HostCondition subjects are raised by the turn host, never by an occupant turn.";
            }
        }

        return null;
    }
}
