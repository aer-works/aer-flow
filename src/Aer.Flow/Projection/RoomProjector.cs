using Aer.Flow.Domain;

namespace Aer.Flow.Projection;

/// <summary>
/// Reconstructs <see cref="RoomState"/> from room event history (<c>room.jsonl</c>):
/// <c>RoomState = RoomProjector.Project(events)</c>. A pure function — no I/O, no filesystem
/// access — so identical event lists produce byte-identical projection output.
/// </summary>
public static class RoomProjector
{
    public static RoomState Project(IReadOnlyList<RoomEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var heldWork = new Dictionary<HeldWorkRef, HeldWorkState>();
        var unmatchedEntries = new List<string>();

        foreach (var roomEvent in events)
        {
            switch (roomEvent)
            {
                case RoomEvent.HeldWorkDispatched dispatched:
                    heldWork[dispatched.Ref] = new HeldWorkState(
                        dispatched.Ref,
                        dispatched.Shape,
                        dispatched.Budget,
                        dispatched.DeciderIdentity,
                        HeldWorkStatus.Dispatched);
                    break;

                case RoomEvent.HeldWorkEscalated escalated:
                    if (heldWork.TryGetValue(escalated.Ref, out var existingEscalated))
                    {
                        heldWork[escalated.Ref] = existingEscalated with
                        {
                            Status = HeldWorkStatus.Escalated,
                            EscalatedTo = escalated.ToWhom
                        };
                    }
                    else
                    {
                        unmatchedEntries.Add($"heldWorkEscalated for unknown ref '{escalated.Ref}'");
                    }

                    break;

                case RoomEvent.HeldWorkResolved resolved:
                    if (heldWork.TryGetValue(resolved.Ref, out var existingResolved))
                    {
                        heldWork[resolved.Ref] = existingResolved with
                        {
                            Status = HeldWorkStatus.Resolved,
                            Citation = resolved.Citation
                        };
                    }
                    else
                    {
                        unmatchedEntries.Add($"heldWorkResolved for unknown ref '{resolved.Ref}'");
                    }

                    break;
            }
        }

        return new RoomState(heldWork, unmatchedEntries);
    }
}
