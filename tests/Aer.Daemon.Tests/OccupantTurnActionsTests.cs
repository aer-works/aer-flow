using Aer.Daemon;
using Aer.Flow.Domain;
using Xunit;

namespace Aer.Daemon.Tests;

public class OccupantTurnActionsTests
{
    [Fact]
    public void Parse_ValidTwoEscalationFixture_ParsesTyped()
    {
        // Red arm note: If parser fails to deserialize valid JSON or misinterprets trigger/subject kind, Actions will be null or fields will be mismatched.
        var json = """
        {
          "contractVersion": 1,
          "report": "I analyzed the room state and found two issues.",
          "escalations": [
            { "trigger": "Ambiguity", "subject": { "kind": "decision", "decisionId": "d-1" } },
            { "trigger": "Direction", "subject": { "kind": "origination", "templateId": "review-run", "briefRef": "artifacts/brief.md" } }
          ]
        }
        """;

        var (actions, error) = OccupantTurnActions.Parse(json);

        Assert.Null(error);
        Assert.NotNull(actions);
        Assert.Equal(1, actions.ContractVersion);
        Assert.Equal("I analyzed the room state and found two issues.", actions.Report);
        Assert.Equal(2, actions.Escalations.Count);

        Assert.Equal(EscalationTrigger.Ambiguity, actions.Escalations[0].Trigger);
        var decisionSub = Assert.IsType<EscalationSubject.Decision>(actions.Escalations[0].Subject);
        Assert.Equal(new DecisionId("d-1"), decisionSub.DecisionId);

        Assert.Equal(EscalationTrigger.Direction, actions.Escalations[1].Trigger);
        var origSub = Assert.IsType<EscalationSubject.ProposedOrigination>(actions.Escalations[1].Subject);
        Assert.Equal(new WorkflowTemplateId("review-run"), origSub.TemplateId);
        Assert.Equal("artifacts/brief.md", origSub.BriefRef);
    }

    [Fact]
    public void Parse_HeldWorkSubject_ParsesTyped()
    {
        // Red arm note: with no heldWork parser arm (#1001), this kind falls into the
        // unknown-kind rejection and actions is null.
        var json = """
        {
          "contractVersion": 1,
          "report": "citing held work",
          "escalations": [
            { "trigger": "Ambiguity", "subject": { "kind": "heldWork", "ref": "C:/room/lanes/demo" } }
          ]
        }
        """;

        var (actions, error) = OccupantTurnActions.Parse(json);

        Assert.Null(error);
        Assert.NotNull(actions);
        var heldSub = Assert.IsType<EscalationSubject.HeldWork>(Assert.Single(actions.Escalations).Subject);
        Assert.Equal(new HeldWorkRef("C:/room/lanes/demo"), heldSub.Ref);
    }

    [Fact]
    public void Parse_HeldWorkSubjectMissingRef_FailsClosed()
    {
        // Red arm note: polarity of the arm above — a heldWork subject without a ref must be a
        // parse error, not a subject with an empty ref.
        var json = """
        {
          "contractVersion": 1,
          "report": "r",
          "escalations": [ { "trigger": "Ambiguity", "subject": { "kind": "heldWork" } } ]
        }
        """;

        var (actions, error) = OccupantTurnActions.Parse(json);

        Assert.Null(actions);
        Assert.NotNull(error);
        Assert.Contains("ref", error);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("99")]
    [InlineData(" 3")]
    [InlineData("+3")]
    public void Parse_NumericTriggerString_ReturnsError(string numericTrigger)
    {
        // Red arm: Enum.TryParse alone accepts numeric strings — "3" maps to a defined trigger
        // and "99" parses to an UNDEFINED value — so before the guard, both parsed as triggers.
        // " 3" and "+3" are the second red arm: TryParse trims whitespace and accepts a sign,
        // so a leading-digit-only guard let both through (second-reader finding).
        var json = $$"""
        {
          "contractVersion": 1,
          "report": "r",
          "escalations": [ { "trigger": "{{numericTrigger}}", "subject": { "kind": "decision", "decisionId": "d-1" } } ]
        }
        """;

        var (actions, error) = OccupantTurnActions.Parse(json);

        Assert.Null(actions);
        Assert.Contains("Unknown trigger", error);
    }

    [Fact]
    public void Parse_UnknownTrigger_ReturnsError()
    {
        // Red arm note: If parser ignores unknown triggers, error will be null and actions will parse successfully.
        var json = """
        {
          "contractVersion": 1,
          "report": "test",
          "escalations": [
            { "trigger": "InvalidTrigger", "subject": { "kind": "decision", "decisionId": "d-1" } }
          ]
        }
        """;

        var (actions, error) = OccupantTurnActions.Parse(json);

        Assert.Null(actions);
        Assert.NotNull(error);
        Assert.Contains("Unknown trigger name", error);
    }

    [Fact]
    public void Parse_UnknownSubjectKind_ReturnsError()
    {
        // Red arm note: If parser ignores unknown subject kinds, error will be null and actions will parse successfully.
        var json = """
        {
          "contractVersion": 1,
          "report": "test",
          "escalations": [
            { "trigger": "Ambiguity", "subject": { "kind": "unknown_kind" } }
          ]
        }
        """;

        var (actions, error) = OccupantTurnActions.Parse(json);

        Assert.Null(actions);
        Assert.NotNull(error);
        Assert.Contains("Unknown subject kind", error);
    }

    [Fact]
    public void Parse_WrongContractVersion_ReturnsError()
    {
        // Red arm note: If parser accepts version 2, error will be null and actions will parse successfully.
        var json = """
        {
          "contractVersion": 2,
          "report": "test",
          "escalations": []
        }
        """;

        var (actions, error) = OccupantTurnActions.Parse(json);

        Assert.Null(actions);
        Assert.NotNull(error);
        Assert.Contains("Unknown contractVersion", error);
    }

    [Fact]
    public void Parse_EmptyEscalationsWithReport_IsValid()
    {
        // Red arm note: If parser requires non-empty escalations array, error will be non-null.
        var json = """
        {
          "contractVersion": 1,
          "report": "Nothing to escalate.",
          "escalations": []
        }
        """;

        var (actions, error) = OccupantTurnActions.Parse(json);

        Assert.Null(error);
        Assert.NotNull(actions);
        Assert.Equal(1, actions.ContractVersion);
        Assert.Equal("Nothing to escalate.", actions.Report);
        Assert.Empty(actions.Escalations);
    }
}
