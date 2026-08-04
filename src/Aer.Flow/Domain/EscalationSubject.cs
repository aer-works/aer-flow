using System.Text.Json.Serialization;

namespace Aer.Flow.Domain;

/// <summary>
/// Subject of an escalation (§D): a decisionId OR a proposedOrigination.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Decision), "decision")]
[JsonDerivedType(typeof(ProposedOrigination), "proposedOrigination")]
public abstract record EscalationSubject
{
    private EscalationSubject()
    {
    }

    public sealed record Decision(DecisionId DecisionId) : EscalationSubject;

    public sealed record ProposedOrigination(
        WorkflowTemplateId TemplateId,
        string? BriefRef = null) : EscalationSubject;
}
