namespace Aer.Journeys.Tests;

/// <summary>
/// Pending .NET-stack journey legs — surfaces that exist and are driveable, but whose journey test
/// is a fast-follow. Declared as skipped-with-reason so running the suite <em>enumerates</em> them
/// (with the issue that will make them driven) rather than leaving silent gaps. Cross-process and
/// live-vendor legs are not here — they are human-attested and live in <c>docs/runbooks/journeys.md</c>,
/// not as skipped tests. Phone legs live in <c>src/Aer.Mobile/test/journeys</c>.
/// </summary>
public class PendingJourneys
{
    [Trait(Journeys.TraitKey, "J3")]
    [Fact(Skip = "Fast-follow (#355): view-mounted assertion that Home's inbox summary counts a failed task as failed, not \"finished\".")]
    public void J3_desktop_failed_work_reads_as_failed_not_finished()
    {
    }

    [Trait(Journeys.TraitKey, "J2")]
    [Fact(Skip = "Pending the room model (decisions 0001/0008/0009, #333/#335): spawn/host/gate legs get driven as they land.")]
    public void J2_desktop_grow_the_room()
    {
    }

    [Trait(Journeys.TraitKey, "J9")]
    [Fact(Skip = "Pending the cross-vendor usage surface (#360/#338): no surface to drive yet.")]
    public void J9_desktop_cross_vendor_usage_view()
    {
    }
}
