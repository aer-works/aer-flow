using System.Text;
using System.Text.RegularExpressions;

namespace Aer.Plan.Tests;

/// <summary>
/// The freshness gate for <c>docs/plan.md</c> (#373). The plan is a thin index that <em>names</em>
/// decisions and journeys but defers their status to the sources that keep it. These tests assert it
/// cannot lie about either: the decisions it lists are exactly the ones in <c>docs/decisions/</c>
/// (and its index), and every journey it references exists in <c>spec/journeys.md</c>. This is the
/// build failure — not a note — that stops the plan rotting the way its GitHub-issue predecessor did
/// (five stale claims, nothing checking). It runs in default CI because it is meant to pass.
/// </summary>
public class PlanConsistencyTests
{
    [Fact]
    public void The_plan_lists_exactly_the_decisions_that_exist_and_the_index_agrees()
    {
        var inPlan = FourDigits(Read("docs/plan.md"), @"decisions/(\d{4})-");
        var onDisk = DecisionFilesOnDisk();
        var inIndex = FourDigits(Read(Path.Combine("docs", "decisions", "README.md")), @"\((\d{4})-");

        // Three-way: the plan, the actual files, and the decisions index must name the same set.
        // A decision added but not indexed, or referenced in the plan but never written, or written
        // but dropped from the plan — any of the three drifting — fails here (the exact shape of the
        // #283 rot, where the table stopped at 0007 while 0008/0009 shipped).
        Assert.True(
            inPlan.SetEquals(onDisk) && onDisk.SetEquals(inIndex),
            "Decision drift:\n"
            + $"  docs/plan.md names:      {Show(inPlan)}\n"
            + $"  docs/decisions/ has:     {Show(onDisk)}\n"
            + $"  decisions/README index:  {Show(inIndex)}");
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
        // IMPLEMENTATION_PLAN.md was decomposed into docs/decisions-of-record.md (milestone history)
        // and docs/plan.md (the current, gated plan), then deleted (#367). A second competing plan
        // document is exactly the drift this whole effort exists to kill — fail if one reappears.
        var path = Path.Combine(RepoRoot(), "IMPLEMENTATION_PLAN.md");
        Assert.False(
            File.Exists(path),
            "IMPLEMENTATION_PLAN.md is back. Its roadmap and milestone summaries belong in "
            + "docs/decisions-of-record.md, and the current, gated plan is docs/plan.md — there is no "
            + "second plan document (#367).");
    }

    [Fact]
    public void Every_relative_link_in_the_living_docs_resolves_to_a_real_file()
    {
        // Link rot is the plainest form of doc rot. The two docs this milestone made canonical must
        // never point at a file that has moved or been deleted — the failure that dangled every
        // IMPLEMENTATION_PLAN.md reference the moment it was retired.
        string[] docs = { Path.Combine("docs", "plan.md"), Path.Combine("docs", "decisions-of-record.md") };
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
