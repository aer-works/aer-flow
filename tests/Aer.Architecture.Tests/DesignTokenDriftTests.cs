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
    /// #458's gate: every status names a mark, and both toolkits must actually draw it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The marks are the one part of the design system that cannot be generated — vector geometry is
    /// hand-drawn, in a <c>StreamGeometry</c> on Avalonia and a <c>CustomPainter</c> on Flutter — so
    /// they are also the one part that can silently go missing. Adding a status to the token file, or
    /// renaming a mark, compiles and runs on both platforms and shows up only as a blank space where
    /// a status marker belongs, on whichever platform whoever made the change was not looking at.
    /// </para>
    /// <para>
    /// This is a deliberately shallow check — it asserts a mark is *defined*, not that the two
    /// drawings agree. Shape equivalence across two toolkits' path syntaxes is not something a test
    /// can assert honestly, and pretending otherwise would be worse than admitting the limit: that
    /// half stays a review question, kept tractable by both files being authored on the same 16x16
    /// grid with matching coordinates.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryStatusMarkIsDrawnByBothToolkits()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tokensJson = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.TokensPath));

        var avaloniaIcons = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.AvaloniaIconsPath));
        var flutterMarks = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.FlutterStatusMarkPath));

        var marks = TokenGenerator.StatusMarks(tokensJson).ToList();
        Assert.NotEmpty(marks);

        foreach (var (status, mark, geometryKey) in marks)
        {
            Assert.True(
                avaloniaIcons.Contains($"""x:Key="{geometryKey}" """.TrimEnd(), StringComparison.Ordinal),
                $"""
                Status '{status}' names the mark '{mark}', but {TokenGenerator.AvaloniaIconsPath} defines
                no geometry with the key '{geometryKey}'. Desktop would render that status as a blank space.
                """);

            Assert.True(
                flutterMarks.Contains($"case '{mark}':", StringComparison.Ordinal),
                $"""
                Status '{status}' names the mark '{mark}', but {TokenGenerator.FlutterStatusMarkPath} has
                no case for it. Mobile would throw when asked to draw that status.
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
