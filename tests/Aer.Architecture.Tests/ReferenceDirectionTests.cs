using System.Xml.Linq;

namespace Aer.Architecture.Tests;

/// <summary>
/// #370: CLAUDE.md's reference-direction invariant is prose the compiler can't check on its own, and
/// the room-model churn (#333/#335) is exactly when it could silently erode — a stray
/// <c>ProjectReference</c> added mid-refactor, and nothing fails. These tests read the project graph
/// and fail the build the moment a forbidden dependency appears, a seam gate in decision 0005's
/// rhythm (like #317/#318 were). A guard that lands after the refactor it guards is worthless, so
/// this lands first.
///
/// <para>Scope: the <em>structurally checkable</em> invariants — who may reference whom. "Flow never
/// parses worker content to make routing decisions" (CLAUDE.md rule 1) is a property of logic, not
/// of the reference graph, so it stays a review-time invariant no static test can honestly assert.</para>
///
/// <para>Pure file reading over the repo — no project references, no network — so it runs identically
/// on every CI platform, the same shape as <c>Aer.Plan.Tests</c>.</para>
/// </summary>
public class ReferenceDirectionTests
{
    // Aer.Flow is the pure engine (CLAUDE.md rule 2: the core layer understands only the single,
    // unified canonical protocol). It may depend on the aer-core binding and the framework — never on
    // a vendor adapter, a client, or the daemon. This is the load-bearing invariant #335 rides: the
    // engine needs no changes for multi-task precisely because nothing above it reaches back in.
    [Fact]
    public void Aer_Flow_depends_on_nothing_above_the_engine()
        => AssertNoForbiddenReferences(
            project: "Aer.Flow",
            forbiddenProjects: ["Aer.Adapters", "Aer.Ui", "Aer.Ui.Core", "Aer.Daemon", "Aer.Cli", "Aer.Workers.Dialogue"],
            forbiddenPackagePrefixes: ["Avalonia", "Microsoft.AspNetCore"]);

    // Aer.Ui.Core is the Avalonia-free, remote-ready MVVM seam (M19). A reference to Aer.Ui (the
    // Avalonia app) or to Avalonia itself collapses the seam the multi-task/remote work rides on —
    // the whole point of the split is that a client's view models carry no toolkit.
    [Fact]
    public void Aer_Ui_Core_stays_toolkit_and_daemon_free()
        => AssertNoForbiddenReferences(
            project: "Aer.Ui.Core",
            forbiddenProjects: ["Aer.Ui", "Aer.Daemon"],
            forbiddenPackagePrefixes: ["Avalonia"]);

    // Adapter isolation (CLAUDE.md rule 2): vendor quirks live in Aer.Adapters, which depends only
    // downward on the engine — never up into a client or the daemon.
    [Fact]
    public void Aer_Adapters_does_not_depend_on_clients_or_the_daemon()
        => AssertNoForbiddenReferences(
            project: "Aer.Adapters",
            forbiddenProjects: ["Aer.Ui", "Aer.Ui.Core", "Aer.Daemon", "Aer.Cli"],
            forbiddenPackagePrefixes: []);

    // A guard that reads an empty reference set passes vacuously — the exact stale-and-unchecked
    // failure this milestone exists to kill (a csproj-schema change that broke parsing would silently
    // disarm every assertion above). Anchor on a reference known to exist so that failure is loud.
    [Fact]
    public void The_reference_reader_is_not_silently_returning_nothing()
    {
        var (projectRefs, packageRefs) = ReadReferences("Aer.Ui.Core");

        Assert.Contains("Aer.Flow", projectRefs);
        Assert.Contains("CommunityToolkit.Mvvm", packageRefs);
    }

    private static void AssertNoForbiddenReferences(
        string project, string[] forbiddenProjects, string[] forbiddenPackagePrefixes)
    {
        var (projectRefs, packageRefs) = ReadReferences(project);

        var projectHits = projectRefs.Intersect(forbiddenProjects, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(
            projectHits.Count == 0,
            $"{project} must not reference project(s) [{string.Join(", ", projectHits)}] — " +
            "reference-direction invariant (CLAUDE.md architecture rules, #370).");

        var packageHits = packageRefs
            .Where(pkg => forbiddenPackagePrefixes.Any(prefix => pkg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.True(
            packageHits.Count == 0,
            $"{project} must not reference package(s) [{string.Join(", ", packageHits)}] — " +
            "reference-direction invariant (CLAUDE.md architecture rules, #370).");
    }

    private static (IReadOnlyCollection<string> ProjectRefs, IReadOnlyCollection<string> PackageRefs) ReadReferences(string project)
    {
        var path = Path.Combine(RepoRoot(), "src", project, project + ".csproj");
        var doc = XDocument.Load(path);

        var projectRefs = doc.Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            // Normalize Windows separators to '/' first so GetFileNameWithoutExtension resolves the
            // project name on Unix CI too (a bare '\' is not a separator there).
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var packageRefs = doc.Descendants("PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => include!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (projectRefs, packageRefs);
    }

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
            "Could not locate the repo root (docs/plan.md) by walking up from " + AppContext.BaseDirectory);
    }
}
