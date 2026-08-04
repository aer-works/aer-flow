using System.Text;
using Aer.Flow.Domain;
using Aer.Flow.Projection;

namespace Aer.Daemon;

public static class OrchestratorTurnPrompt
{
    public static string Render(OrchestratorTurnInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var sb = new StringBuilder();

        // 1. Cold-start banner
        if (input.IsColdStart)
        {
            sb.AppendLine("=== [COLD-START RECONSTRUCTION] ===");
            sb.AppendLine("This turn was reconstructed from the room record alone. Conversational nuance since the last recorded state may be lost.");
            sb.AppendLine("You MUST state this cold-start reconstruction in your turn report rather than pretending continuity.");
            sb.AppendLine();
        }

        // 2. Room memory document INDEX content
        sb.AppendLine("## Room Memory Index");
        if (!string.IsNullOrWhiteSpace(input.MemoryDocument.IndexContent))
        {
            sb.AppendLine(input.MemoryDocument.IndexContent);
        }
        else
        {
            sb.AppendLine("(empty memory index)");
        }
        sb.AppendLine();

        // 3. Held work
        sb.AppendLine($"## Held Work ({input.RoomState.HeldWork.Count})");
        if (input.RoomState.HeldWork.Count > 0)
        {
            foreach (var (refKey, state) in input.RoomState.HeldWork)
            {
                sb.AppendLine($"- Ref: {refKey.Value} | Shape: {state.Shape} | Status: {state.Status} | Decider: {state.DeciderIdentity}");
            }
        }
        else
        {
            sb.AppendLine("(none)");
        }
        sb.AppendLine();

        // 4. Open escalations
        sb.AppendLine($"## Open Escalations ({input.RoomState.OpenEscalations.Count})");
        if (input.RoomState.OpenEscalations.Count > 0)
        {
            foreach (var esc in input.RoomState.OpenEscalations)
            {
                sb.AppendLine($"- Trigger: {esc.Trigger} | From: {esc.FromWorkerId.Value} | Subject: {RenderSubject(esc.Subject)}");
            }
        }
        else
        {
            sb.AppendLine("(none)");
        }
        sb.AppendLine();

        // 5. Wakes
        sb.AppendLine($"## Turn Wakes ({input.Wakes.Count})");
        if (input.Wakes.Count > 0)
        {
            foreach (var wake in input.Wakes)
            {
                sb.AppendLine($"- Kind: {wake.Kind} | Ref: {wake.Ref.Value}");
            }
        }
        else
        {
            sb.AppendLine("(none)");
        }
        sb.AppendLine();

        // 6. Event delta
        sb.AppendLine($"## Event Delta ({input.EventDelta.Count})");
        if (input.EventDelta.Count > 0)
        {
            foreach (var ev in input.EventDelta)
            {
                sb.AppendLine($"- {RenderEvent(ev)}");
            }
        }
        else
        {
            sb.AppendLine("(none)");
        }
        sb.AppendLine();

        // 7. The turn contract instructions
        sb.AppendLine("## Turn Contract Instructions");
        sb.AppendLine("You are operating as a fire-and-wait room occupant turn (v1).");
        sb.AppendLine("DO NOT attempt to dispatch or execute originations or decisions directly yourself — v1 escalates everything.");
        sb.AppendLine("You MUST write a structured output file named `turn-actions.json` into your `AER_OUTPUT_DIR` directory.");
        sb.AppendLine("Writing `turn-actions.json` is REQUIRED — a turn without this file is a failed turn.");
        sb.AppendLine();
        sb.AppendLine("The `turn-actions.json` file MUST follow contract version 1 schema exactly:");
        sb.AppendLine("""
        {
          "contractVersion": 1,
          "report": "what I did / saw this turn, for humans",
          "escalations": [
            { "trigger": "Ambiguity", "subject": { "kind": "decision", "decisionId": "d-1" } },
            { "trigger": "Direction", "subject": { "kind": "origination", "templateId": "review-run", "briefRef": "artifacts/brief.md" } }
          ]
        }
        """);

        return sb.ToString();
    }

    private static string RenderSubject(EscalationSubject subject) => subject switch
    {
        EscalationSubject.Decision d => $"[Decision] decisionId={d.DecisionId.Value}",
        EscalationSubject.ProposedOrigination o => $"[ProposedOrigination] templateId={o.TemplateId.Value}, briefRef={o.BriefRef ?? "none"}",
        EscalationSubject.HostCondition h => $"[HostCondition] condition={h.Condition}, detail={h.Detail}",
        _ => subject.ToString()
    };

    private static string RenderEvent(RoomEvent ev) => ev switch
    {
        RoomEvent.HeldWorkDispatched e => $"HeldWorkDispatched: Ref={e.Ref.Value}, Shape={e.Shape}, Budget={e.Budget}, Decider={e.DeciderIdentity}",
        RoomEvent.HeldWorkEscalated e => $"HeldWorkEscalated: Ref={e.Ref.Value}, ToWhom={e.ToWhom}",
        RoomEvent.HeldWorkResolved e => $"HeldWorkResolved: Ref={e.Ref.Value}",
        RoomEvent.GrantRecorded e => $"GrantRecorded: GrantId={e.GrantId.Value}, WorkerId={e.WorkerId.Value}, Level={e.Level}",
        RoomEvent.GrantAmended e => $"GrantAmended: GrantId={e.GrantId.Value}, Amends={e.AmendsGrantId.Value}, WorkerId={e.WorkerId.Value}",
        RoomEvent.GrantRevoked e => $"GrantRevoked: GrantId={e.GrantId.Value}, Revoker={e.Revoker}, Reason={e.Reason}",
        RoomEvent.EscalationRaised e => $"EscalationRaised: From={e.FromWorkerId.Value}, Trigger={e.Trigger}, Subject={RenderSubject(e.Subject)}",
        RoomEvent.TurnHostDormancyEntered e => $"TurnHostDormancyEntered: ConsecutiveFailures={e.ConsecutiveFailures}",
        RoomEvent.TurnHostDormancyCleared e => $"TurnHostDormancyCleared: ClearedBy={e.ClearedBy}",
        _ => ev.GetType().Name
    };
}
