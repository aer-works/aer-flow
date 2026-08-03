namespace Aer.Journeys.Tests;

/// <summary>
/// The reconcile gate's <b>structural</b> half (#313). Since #952 the registry <em>joins</em>
/// titles and statuses from <c>spec/journeys.md</c>
/// at load (<see cref="SpecJourneys"/>), so the old byte-drift tests are impossible by
/// construction and were deleted: a registry id the spec lacks throws inside
/// <see cref="Journeys.All"/> itself. What a join cannot catch is the other direction — a journey
/// written into the spec with no registry entry (and therefore no legs, no coverage audit) — and
/// that is what this asserts.
/// <para>
/// The <em>behavioural</em> half — comparing each declared status against the journey tests'
/// actual pass/fail, so a journey that starts passing but still reads "Fails" (or the reverse)
/// also breaks the build — belongs to the status-truthfulness umbrella (#752) and lands with the
/// UI arc, when statuses actually start flipping. (#314 turned out to own the *spec* structural
/// claims; its close reason and #752's thread carry this split.) That half needs the tests'
/// results, which is why it is a separate gate; this half needs only the two documents.
/// </para>
/// </summary>
[Trait("Category", "Reconcile")]
public class ReconcileTests
{
    [Fact]
    public void Registry_and_spec_declare_the_same_journeys()
    {
        var specIds = SpecJourneys.Parse().Select(j => j.Id).OrderBy(id => id, StringComparer.Ordinal);
        var registryIds = Journeys.All.Select(j => j.Id).OrderBy(id => id, StringComparer.Ordinal);

        Assert.Equal(specIds, registryIds);
    }

    [Fact]
    public void Every_journey_carries_its_spec_title_and_status_after_the_join()
    {
        // Not a drift test — drift is impossible since the join is the only source (#952). This is
        // the loud-failure arm: a join that silently produced empty titles/statuses would make
        // every downstream consumer of the registry read blanks, so assert the join actually
        // populated both fields for every journey.
        foreach (var journey in Journeys.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(journey.Title), $"{journey.Id}: empty Title after join");
            Assert.False(string.IsNullOrWhiteSpace(journey.DeclaredStatus), $"{journey.Id}: empty DeclaredStatus after join");
        }
    }
}
