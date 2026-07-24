using Aer.DesignTokens;

namespace Aer.Architecture.Tests;

/// <summary>
/// #345's gate: the checked-in theme artifacts must be exactly what <c>design/tokens.json</c>
/// generates.
/// </summary>
/// <remarks>
/// <para>
/// One token file generating both toolkits only removes drift if something notices when the
/// artifacts and the source disagree. Without this, the two failure modes are both silent: someone
/// hand-edits <c>Tokens.axaml</c> because it is right there, or changes a colour in the token file
/// and never runs the generator — and in either case desktop and mobile quietly stop matching, which
/// is the exact problem the pipeline was built to solve.
/// </para>
/// <para>
/// The comparison runs the real generator rather than a second implementation of "what the output
/// should look like". A gate with its own notion of correct output drifts from the generator and
/// then passes while the artifacts are wrong.
/// </para>
/// </remarks>
public class DesignTokenDriftTests
{
    [Fact]
    public void GeneratedThemeArtifactsMatchTheTokenFile()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tokensJson = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.TokensPath));

        foreach (var (relativePath, expected) in TokenGenerator.Generate(tokensJson))
        {
            var path = Path.Combine(repositoryRoot, relativePath);
            Assert.True(File.Exists(path), $"{relativePath} is missing. Run `{TokenGenerator.RegenerateCommand}`.");

            // Read as-is and normalise only line endings: git may check these out with CRLF on
            // Windows, which is not drift. Anything else that differs is.
            var actual = File.ReadAllText(path).ReplaceLineEndings("\n");

            Assert.True(
                string.Equals(expected, actual, StringComparison.Ordinal),
                $"""
                {relativePath} is out of date with {TokenGenerator.TokensPath}.

                Either it was hand-edited, or {TokenGenerator.TokensPath} changed without regenerating.
                Run `{TokenGenerator.RegenerateCommand}` and commit the result.

                {FirstDifference(expected, actual)}
                """);
        }
    }

    /// <summary>
    /// The first differing line, both sides. A whole-file diff in an assertion message is unreadable;
    /// the first divergence is almost always the whole story for a generated file.
    /// </summary>
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
            if (File.Exists(Path.Combine(directory.FullName, TokenGenerator.TokensPath)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root from {AppContext.BaseDirectory}.");
    }
}
