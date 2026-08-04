using System.Text.Json;
using Aer.Flow.Domain;

namespace Aer.Daemon;

public sealed record OccupantEscalation(
    EscalationTrigger Trigger,
    EscalationSubject Subject);

public sealed record OccupantTurnActions(
    int ContractVersion,
    string Report,
    IReadOnlyList<OccupantEscalation> Escalations)
{
    public static (OccupantTurnActions? Actions, string? Error) Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, "JSON string is null or empty.");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "Root JSON must be an object.");
            }

            var root = doc.RootElement;

            if (!root.TryGetProperty("contractVersion", out var versionProp) || versionProp.ValueKind != JsonValueKind.Number)
            {
                return (null, "Missing or invalid 'contractVersion'.");
            }

            int version = versionProp.GetInt32();
            if (version != 1)
            {
                return (null, $"Unknown contractVersion: {version}. Expected 1.");
            }

            if (!root.TryGetProperty("report", out var reportProp) || reportProp.ValueKind != JsonValueKind.String)
            {
                return (null, "Missing or invalid 'report' string.");
            }

            string report = reportProp.GetString()!;

            var escalations = new List<OccupantEscalation>();
            if (root.TryGetProperty("escalations", out var escalationsProp))
            {
                if (escalationsProp.ValueKind != JsonValueKind.Array)
                {
                    return (null, "'escalations' must be an array.");
                }

                foreach (var item in escalationsProp.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        return (null, "Escalation item must be an object.");
                    }

                    if (!item.TryGetProperty("trigger", out var triggerProp) || triggerProp.ValueKind != JsonValueKind.String)
                    {
                        return (null, "Escalation item missing 'trigger' string.");
                    }

                    var triggerName = triggerProp.GetString();
                    if (triggerName is null || !Enum.TryParse<EscalationTrigger>(triggerName, ignoreCase: false, out var trigger))
                    {
                        return (null, $"Unknown trigger name '{triggerName}'.");
                    }

                    if (!item.TryGetProperty("subject", out var subjectProp) || subjectProp.ValueKind != JsonValueKind.Object)
                    {
                        return (null, "Escalation item missing 'subject' object.");
                    }

                    if (!subjectProp.TryGetProperty("kind", out var kindProp) || kindProp.ValueKind != JsonValueKind.String)
                    {
                        return (null, "Escalation subject missing 'kind' string.");
                    }

                    var kind = kindProp.GetString();
                    EscalationSubject subject;
                    if (string.Equals(kind, "decision", StringComparison.Ordinal))
                    {
                        if (!subjectProp.TryGetProperty("decisionId", out var decisionIdProp) || decisionIdProp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(decisionIdProp.GetString()))
                        {
                            return (null, "Decision escalation subject missing 'decisionId'.");
                        }
                        subject = new EscalationSubject.Decision(new DecisionId(decisionIdProp.GetString()!));
                    }
                    else if (string.Equals(kind, "origination", StringComparison.Ordinal))
                    {
                        if (!subjectProp.TryGetProperty("templateId", out var templateIdProp) || templateIdProp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(templateIdProp.GetString()))
                        {
                            return (null, "Origination escalation subject missing 'templateId'.");
                        }
                        string? briefRef = null;
                        if (subjectProp.TryGetProperty("briefRef", out var briefRefProp) && briefRefProp.ValueKind == JsonValueKind.String)
                        {
                            briefRef = briefRefProp.GetString();
                        }
                        subject = new EscalationSubject.ProposedOrigination(new WorkflowTemplateId(templateIdProp.GetString()!), briefRef);
                    }
                    else
                    {
                        return (null, $"Unknown subject kind '{kind}'.");
                    }

                    escalations.Add(new OccupantEscalation(trigger, subject));
                }
            }

            return (new OccupantTurnActions(version, report, escalations.AsReadOnly()), null);
        }
        catch (JsonException ex)
        {
            return (null, $"JSON parse error: {ex.Message}");
        }
    }
}
