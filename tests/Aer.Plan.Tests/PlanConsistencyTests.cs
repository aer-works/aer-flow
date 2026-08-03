using System.Text;
using System.Text.RegularExpressions;

namespace Aer.Plan.Tests;

/// <summary>
/// The freshness gate for <c>docs/plan.md</c> (#373). The plan is a thin index that <em>names</em>
/// decisions and journeys but defers their status to the sources that keep it. These tests assert it
/// cannot lie about either: every decision it names exists in <c>docs/decisions/</c> (the index
/// itself is generated from the records — #952, checked by <c>completeness.py</c> STEP 12), and
/// every journey it references exists in <c>spec/journeys.md</c>. This is the build failure — not a
/// note — that stops the plan rotting the way its GitHub-issue predecessor did (five stale claims,
/// nothing checking). It runs in default CI because it is meant to pass.
/// </summary>
public class PlanConsistencyTests
{
    [Fact]
    public void Every_decision_the_plan_names_exists_on_disk()
    {
        var inPlan = FourDigits(Read("docs/plan.md"), @"decisions/(\d{4})-");
        var onDisk = DecisionFilesOnDisk();

        // Subset, not the old three-way equality: #952 retired the plan's own copy of the decision
        // table (the index is generated from the records; completeness.py STEP 12 guards its
        // freshness), so the plan now only *mentions* the decisions its prose leans on. What can
        // still rot here is a mention of a record that never existed or was renamed.
        var unknown = inPlan.Except(onDisk).ToList();
        Assert.True(
            unknown.Count == 0,
            $"docs/plan.md names decisions that do not exist in docs/decisions/: {Show(unknown)}");
    }

    [Fact]
    public void Every_journey_the_plan_references_exists_in_the_journeys_spec()
    {
        var referenced = JourneyIds(Read("docs/plan.md"));
        var defined = DefinedJourneys(Read(Path.Combine("spec", "journeys.md")));

        var unknown = referenced.Except(defined).OrderBy(j => j, StringComparer.Ordinal).ToList();
        Assert.True(
            unknown.Count == 0,
            $"docs/plan.md references journeys not defined in spec/journeys.md: {string.Join(", ", unknown)}. "
            + $"Defined: {string.Join(", ", defined.OrderBy(j => j, StringComparer.Ordinal))}");
    }

    [Fact]
    public void The_retired_implementation_plan_has_not_come_back()
    {
        // IMPLEMENTATION_PLAN.md was decomposed into docs/milestone-history.md (milestone history)
        // and docs/plan.md (the current, gated plan), then deleted (#367). A second competing plan
        // document is exactly the drift this whole effort exists to kill — fail if one reappears.
        var path = Path.Combine(RepoRoot(), "IMPLEMENTATION_PLAN.md");
        Assert.False(
            File.Exists(path),
            "IMPLEMENTATION_PLAN.md is back. Its roadmap and milestone summaries belong in "
            + "docs/milestone-history.md, and the current, gated plan is docs/plan.md — there is no "
            + "second plan document (#367).");
    }

    [Fact]
    public void Every_relative_link_in_the_living_docs_resolves_to_a_real_file()
    {
        // Link rot is the plainest form of doc rot. The two docs this milestone made canonical must
        // never point at a file that has moved or been deleted — the failure that dangled every
        // IMPLEMENTATION_PLAN.md reference the moment it was retired.
        string[] docs = { Path.Combine("docs", "plan.md"), Path.Combine("docs", "milestone-history.md") };
        var broken = new List<string>();
        foreach (var doc in docs)
        {
            var docDir = Path.GetDirectoryName(Path.Combine(RepoRoot(), doc))!;
            foreach (Match m in Regex.Matches(Read(doc), @"\]\(([^)]+)\)"))
            {
                var target = m.Groups[1].Value.Trim();
                if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) || target.StartsWith('#'))
                {
                    continue; // external URL or same-page anchor
                }

                var relativePath = target.Split('#')[0]; // drop any #anchor on a file link
                if (relativePath.Length == 0)
                {
                    continue;
                }

                var resolved = Path.GetFullPath(Path.Combine(docDir, relativePath));
                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                {
                    broken.Add($"{doc.Replace('\\', '/')} -> {target}");
                }
            }
        }

        Assert.True(
            broken.Count == 0,
            "Relative links in the living docs that no longer resolve:\n  " + string.Join("\n  ", broken));
    }

    private static SortedSet<string> DecisionFilesOnDisk()
    {
        var dir = Path.Combine(RepoRoot(), "docs", "decisions");
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
        {
            var match = Regex.Match(Path.GetFileName(file), @"^(\d{4})-");
            if (match.Success)
            {
                set.Add(match.Groups[1].Value);
            }
        }

        return set;
    }

    private static SortedSet<string> FourDigits(string text, string pattern)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, pattern))
        {
            set.Add(m.Groups[1].Value);
        }

        return set;
    }

    private static SortedSet<string> JourneyIds(string text)
    {
        // Bare "J6" / "J9" references (including the endpoints of a "J1–J9" range).
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, @"\bJ(\d+)\b"))
        {
            set.Add("J" + m.Groups[1].Value);
        }

        return set;
    }

    private static SortedSet<string> DefinedJourneys(string spec)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(spec, @"(?m)^## (J\d+) "))
        {
            set.Add(m.Groups[1].Value);
        }

        Assert.True(set.Count > 0, "No journeys parsed from spec/journeys.md — has its format changed?");
        return set;
    }

    private static string Read(string repoRelativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), repoRelativePath), Encoding.UTF8);

    private static string Show(IEnumerable<string> ids) => string.Join(", ", ids);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "plan.md")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate docs/plan.md by walking up from " + AppContext.BaseDirectory);
    }
}
