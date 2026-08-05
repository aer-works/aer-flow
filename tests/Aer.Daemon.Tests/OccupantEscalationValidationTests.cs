using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Xunit;

namespace Aer.Daemon.Tests;

public class OccupantEscalationValidationTests
{
    private static readonly IReadOnlySet<string> Templates = new HashSet<string>(StringComparer.Ordinal) { "implement-review" };

    private static RoomState StateWithHeldWork(HeldWorkRef @ref) =>
        RoomProjector.Project([new RoomEvent.HeldWorkDispatched(@ref, "review", TimeSpan.FromMinutes(10), "operator")]);

    private static OccupantEscalation Esc(EscalationSubject subject) => new(EscalationTrigger.Ambiguity, subject);

    [Fact]
    public void HeldWorkRefInRecord_Passes()
    {
        // Red arm note: if the validator checked the wrong collection or key, a valid ref would return an error here.
        var @ref = new HeldWorkRef("C:/room/lanes/a");
        var error = OccupantEscalationValidation.Validate(
            [Esc(new EscalationSubject.HeldWork(@ref))], StateWithHeldWork(@ref), Templates);
        Assert.Null(error);
    }

    [Fact]
    public void HeldWorkRefNotInRecord_Fails()
    {
        // Red arm note: polarity of the arm above — an absent ref must be an error, not null.
        var error = OccupantEscalationValidation.Validate(
            [Esc(new EscalationSubject.HeldWork(new HeldWorkRef("C:/room/lanes/ghost")))],
            StateWithHeldWork(new HeldWorkRef("C:/room/lanes/a")), Templates);
        Assert.NotNull(error);
        Assert.Contains("ghost", error);
    }

    [Fact]
    public void DecisionSubject_FailsAgainstARecordThatCarriesNoDecisions()
    {
        // The room record carries no citable decisions today (see the validator's comment), so
        // every occupant decision subject is a fabricated reference — #1001's measured defect.
        var error = OccupantEscalationValidation.Validate(
            [Esc(new EscalationSubject.Decision(new DecisionId("d-1")))],
            StateWithHeldWork(new HeldWorkRef("C:/room/lanes/a")), Templates);
        Assert.NotNull(error);
        Assert.Contains("d-1", error);
    }

    [Fact]
    public void OriginationTemplate_KnownPasses_UnknownFails()
    {
        // Red arm note: both polarities of the catalog check in one place — a validator that
        // ignores the template set passes the second call and fails this test.
        var known = OccupantEscalationValidation.Validate(
            [Esc(new EscalationSubject.ProposedOrigination(new WorkflowTemplateId("implement-review")))],
            StateWithHeldWork(new HeldWorkRef("C:/room/lanes/a")), Templates);
        Assert.Null(known);

        var unknown = OccupantEscalationValidation.Validate(
            [Esc(new EscalationSubject.ProposedOrigination(new WorkflowTemplateId("invented-template")))],
            StateWithHeldWork(new HeldWorkRef("C:/room/lanes/a")), Templates);
        Assert.NotNull(unknown);
        Assert.Contains("invented-template", unknown);
    }

    [Fact]
    public void HostConditionFromAnOccupant_Fails()
    {
        // The parser cannot produce one, but the validator is the trust boundary and does not
        // assume its callers — an occupant asserting a host-observed condition is a fabrication.
        var error = OccupantEscalationValidation.Validate(
            [Esc(new EscalationSubject.HostCondition("turn-host-dormancy", "faked"))],
            StateWithHeldWork(new HeldWorkRef("C:/room/lanes/a")), Templates);
        Assert.NotNull(error);
        Assert.Contains("turn host", error);
    }
}
