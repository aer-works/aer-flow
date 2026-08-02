using System.Xml.Linq;

namespace Aer.Architecture.Tests;

/// <summary>
/// Every test project must use xUnit <b>v3</b>, never v2. This is the guard #159 ("migrate all test
/// projects to xunit v3") never had — and its absence is exactly why the migration regressed twice:
/// <c>Aer.Workers.Dialogue.Tests</c> predated #159's close and was missed, and <c>Aer.Mcp.Tests</c>
/// was created on v2 ten days <em>after</em> #159 closed, by copying an old template that nothing
/// stopped. A one-time migration with no enforcing check is prose; this makes it a build failure.
/// <para>
/// The uniform framework is also load-bearing for the flaky-CI fix: the shared parallelism config
/// (<c>xunit.runner.json</c>) can only be expressed once when every project is one runner. A stray v2
/// project would silently reintroduce a second concurrency regime.
/// </para>
/// </summary>
public sealed class TestFrameworkVersionTests
{
    // The v2 packages. `xunit.v3` is the allowed one. `xunit.runner.visualstudio` and
    // `xunit.analyzers` are shared by both major versions, so they are NOT markers of v2 and must not
    // be flagged — the discriminator is the v2 metapackage `xunit` and its split assemblies.
    private static readonly string[] ForbiddenV2Packages =
        ["xunit", "xunit.core", "xunit.execution", "xunit.assert"];

    [Fact]
    public void No_test_project_references_xunit_v2()
    {
        var testsRoot = Path.Combine(RepoRoot(), "tests");
        var offenders = new List<string>();

        foreach (var csproj in Directory.EnumerateFiles(testsRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var v2Hits = XDocument.Load(csproj)
                .Descendants("PackageReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Where(include => ForbiddenV2Packages.Contains(include, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (v2Hits.Count > 0)
            {
                offenders.Add($"{Path.GetFileName(csproj)} references v2 package(s) [{string.Join(", ", v2Hits!)}]");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Test projects must reference xunit.v3, not the xunit v2 packages (finishing #159 with the "
            + "guard it lacked). Migrate the offender(s) to xunit.v3 and pass TestContext.Current.CancellationToken "
            + "where xUnit1051 flags:\n  " + string.Join("\n  ", offenders));
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
