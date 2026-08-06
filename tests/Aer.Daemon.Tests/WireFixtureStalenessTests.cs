using System.Text.RegularExpressions;

namespace Aer.Daemon.Tests;

public class WireFixtureStalenessTests
{
    [Fact]
    public void WireFixturesMatchDaemonSerializer()
    {
        var repositoryRoot = FindRepositoryRoot();

        foreach (var (relativePath, expected) in WireFixtureGenerator.GenerateAll())
        {
            var path = Path.Combine(repositoryRoot, relativePath);
            Assert.True(File.Exists(path), $"{relativePath} is missing. Run generator or test to create.");

            var actual = File.ReadAllText(path).ReplaceLineEndings("\n");
            var expectedNormalized = expected.ReplaceLineEndings("\n");

            Assert.True(
                string.Equals(expectedNormalized, actual, StringComparison.Ordinal),
                $"""
                {relativePath} is out of date with the daemon's real serializer options.

                Either it was hand-edited, or RoomProjection / DaemonSerializerOptions changed without regenerating.
                To regenerate, run the fixture generator or update the checked-in fixture files.

                {FirstDifference(expectedNormalized, actual)}
                """);
        }
    }

    private static string FirstDifference(string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');

        for (var i = 0; i < Math.Max(expectedLines.Length, actualLines.Length); i++)
        {
            var expectedLine = i < expectedLines.Length ? expectedLines[i] : "<end of file>";
            var actualLine = i < actualLines.Length ? actualLines[i] : "<end of file>";
            if (!string.Equals(expectedLine, actualLine, StringComparison.Ordinal))
            {
                return $"""
                    First difference at line {i + 1}:
                      expected: {expectedLine}
                      on disk:  {actualLine}
                    """;
            }
        }

        return "Files differ in length only.";
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AerFlow.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root from {AppContext.BaseDirectory}.");
    }
}
