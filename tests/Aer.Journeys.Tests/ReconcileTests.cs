using System.Text;

namespace Aer.Journeys.Tests;

/// <summary>
/// The reconcile gate's <b>structural</b> half (#313) — the seed #314 grows into a required CI
/// check. It asserts the code-side <see cref="Journeys"/> registry and the prose
/// <c>spec/journeys.md</c> never drift: the same set of journeys, each with a byte-identical
/// <c>**Status:**</c> line. Edit a journey's status in one place and not the other and this fails.
/// <para>
/// #314 adds the <em>behavioural</em> half — comparing each declared status against the journey
/// tests' actual pass/fail, so a journey that starts passing but still reads "Fails" (or the
/// reverse) also breaks the build. That half needs the tests' results, which is why it is a
/// separate gate; this half needs only the two documents and runs on its own.
/// </para>
/// </summary>
[Trait("Category", "Reconcile")]
public class ReconcileTests
{
    private sealed record SpecJourney(string Id, string Title, string Status);

    [Fact]
    public void Registry_and_spec_declare_the_same_journeys()
    {
        var spec = ParseSpec();
        var registryIds = Journeys.All.Select(j => j.Id).OrderBy(id => id, StringComparer.Ordinal);
        var specIds = spec.Select(j => j.Id).OrderBy(id => id, StringComparer.Ordinal);

        Assert.Equal(specIds, registryIds);
    }

    [Fact]
    public void Every_journeys_declared_status_matches_the_spec_verbatim()
    {
        var spec = ParseSpec().ToDictionary(j => j.Id);

        var mismatches = new List<string>();
        foreach (var journey in Journeys.All)
        {
            if (!spec.TryGetValue(journey.Id, out var specJourney))
            {
                mismatches.Add($"{journey.Id}: in registry but not in spec/journeys.md");
                continue;
            }

            if (specJourney.Status != journey.DeclaredStatus)
            {
                mismatches.Add(
                    $"{journey.Id}: registry says \"{journey.DeclaredStatus}\" but spec says \"{specJourney.Status}\"");
            }
        }

        Assert.True(mismatches.Count == 0, "Journey status drift:\n" + string.Join('\n', mismatches));
    }

    [Fact]
    public void Every_journey_titles_match_the_spec()
    {
        var spec = ParseSpec().ToDictionary(j => j.Id);

        var mismatches = new List<string>();
        foreach (var journey in Journeys.All)
        {
            if (spec.TryGetValue(journey.Id, out var specJourney) && specJourney.Title != journey.Title)
            {
                mismatches.Add($"{journey.Id}: registry title \"{journey.Title}\" != spec \"{specJourney.Title}\"");
            }
        }

        Assert.True(mismatches.Count == 0, "Journey title drift:\n" + string.Join('\n', mismatches));
    }

    private static IReadOnlyList<SpecJourney> ParseSpec()
    {
        // Journey headers are exactly "## J{n} — {title}"; the next "**Status:** {x}" line under
        // one is its status. Plain string parsing (not regex) to sidestep the em-dash / middot the
        // status lines carry.
        const string headerPrefix = "## J";
        const string separator = " — ";
        const string statusPrefix = "**Status:**";

        var journeys = new List<SpecJourney>();
        var lines = File.ReadAllLines(SpecPath(), Encoding.UTF8);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith(headerPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var sep = line.IndexOf(separator, StringComparison.Ordinal);
            if (sep < 0)
            {
                continue;
            }

            var id = line["## ".Length..sep].Trim();
            var title = line[(sep + separator.Length)..].Trim();

            var status = lines.Skip(i + 1)
                .TakeWhile(l => !l.StartsWith(headerPrefix, StringComparison.Ordinal))
                .FirstOrDefault(l => l.StartsWith(statusPrefix, StringComparison.Ordinal))
                ?[statusPrefix.Length..].Trim();

            Assert.False(
                string.IsNullOrEmpty(status),
                $"Journey {id} in spec/journeys.md has no **Status:** line.");
            journeys.Add(new SpecJourney(id, title, status!));
        }

        Assert.True(journeys.Count > 0, "No journeys parsed from spec/journeys.md — has its format changed?");
        return journeys;
    }

    private static string SpecPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "spec", "journeys.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate spec/journeys.md by walking up from " + AppContext.BaseDirectory);
    }
}
