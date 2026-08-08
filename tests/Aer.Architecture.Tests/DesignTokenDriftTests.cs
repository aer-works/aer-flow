using System.Text.RegularExpressions;
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
        var interactionStatesJson = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.InteractionStatesPath));

        foreach (var (relativePath, expected) in TokenGenerator.Generate(tokensJson, interactionStatesJson))
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
    /// #952's sweep found the one token copy nothing checked: <c>AerFonts</c>' two family names,
    /// whose own doc comments say "must match <c>design/tokens.json</c>" — and nothing did. Read
    /// from source text (this project deliberately does not reference <c>Aer.Ui</c>, and the
    /// suite's own style is file assertions). The avares URI's fragment (after <c>#</c>) is the
    /// family name Avalonia resolves, so that is the half compared; the asset path before it is
    /// Avalonia packaging, not a token.
    /// </summary>
    [Fact]
    public void AerFontsFamilyNamesMatchTheTokenFile()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tokensJson = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.TokensPath));
        var families = Regex.Match(
            tokensJson, "\"fontFamily\":\\s*\\{\\s*\"sans\":\\s*\"([^\"]+)\",\\s*\"mono\":\\s*\"([^\"]+)\"");
        Assert.True(families.Success, $"{TokenGenerator.TokensPath} no longer carries type.fontFamily.sans/mono — update this test with it.");

        var aerFonts = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Aer.Ui", "AerFonts.cs"));
        var sans = Regex.Match(aerFonts, "Sans = \"[^\"]*#([^\"]+)\"");
        var mono = Regex.Match(aerFonts, "Mono = \"[^\"]*#([^\"]+)\"");
        Assert.True(sans.Success && mono.Success, "AerFonts.cs no longer declares Sans/Mono avares constants — update this test with it.");

        Assert.Equal(families.Groups[1].Value, sans.Groups[1].Value);
        Assert.Equal(families.Groups[2].Value, mono.Groups[1].Value);
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
    /// The inverse of <see cref="EveryStatusMarkIsDrawnByBothToolkits"/> (#489): no toolkit may define
    /// a status mark the token file does not name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The forward check walks tokens → toolkits, so it can only see marks someone declared. A mark
    /// that exists in <em>one</em> toolkit and in no token is invisible to it — and that is not
    /// hypothetical: <c>Icon.Dot</c> was defined in Avalonia and used for the idle/pending state, had
    /// no Flutter counterpart, and appeared in no token. The desktop drew a mark the phone could not
    /// draw, for a state <c>0020</c> lists as canonical, and the gate built to prevent exactly this
    /// class of divergence (#458, #461) could not see it.
    /// </para>
    /// <para>
    /// A toolkit-only mark is how the design system forks: whoever adds it is looking at one platform,
    /// it renders correctly there, and the other silently falls back or blanks. Requiring every drawn
    /// mark to be declared in <c>design/tokens.json</c> forces the declaration first, which is what
    /// makes the forward check meaningful.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoToolkitDefinesAStatusMarkTheTokenFileDoesNotName()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tokensJson = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.TokensPath));
        var declared = TokenGenerator.StatusMarks(tokensJson)
            .Select(m => m.GeometryKey)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(declared);

        var avaloniaIcons = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.AvaloniaIconsPath));

        // Only the status ramp's own marks are in scope. Action glyphs (Icon.Refresh, Icon.Copy, …)
        // are not statuses and are deliberately not token-driven — the rule is about the accessibility
        // contract in 0006, which binds states, not controls. The status marks are exactly the keys the
        // generator emits, so anything matching the same shape but absent from the token file is drift.
        var drawnStatusKeys = Regex
            .Matches(avaloniaIcons, "x:Key=\"(Icon\\.[A-Za-z]+)\"")
            .Select(m => m.Groups[1].Value)
            .Where(key => !NonStatusGlyphs.Contains(key))
            .ToList();

        var orphans = drawnStatusKeys.Where(key => !declared.Contains(key)).ToList();

        Assert.True(
            orphans.Count == 0,
            $"""
            {TokenGenerator.AvaloniaIconsPath} defines status geometry the token file does not name:
              {string.Join("\n  ", orphans)}
            Every status mark must be declared in {TokenGenerator.TokensPath} so the forward check can
            require both toolkits to draw it. If one of these is an action glyph rather than a status
            mark, add it to {nameof(NonStatusGlyphs)} in this test with a note saying why.
            """);
    }

    /// <summary>
    /// Keys in <c>Icons.axaml</c> that are navigation or action glyphs rather than status marks, and so
    /// are correctly absent from the status ramp.
    /// </summary>
    /// <remarks>
    /// Listed explicitly rather than pattern-matched, and that friction is the point: adding a glyph
    /// means answering "is this a state or a control?" out loud. #461 is why the question matters — a
    /// state wearing an action's icon is a trap, and the stale-list state had borrowed
    /// <c>Icon.Refresh</c>, the Retry <em>action</em>'s glyph, inviting a click that would do nothing.
    /// </remarks>
    private static readonly HashSet<string> NonStatusGlyphs = new(StringComparer.Ordinal)
    {
        "Icon.Refresh",
        "Icon.Home",
        "Icon.Task",
        "Icon.Author",
        "Icon.Folder",
        "Icon.Remote",
        "Icon.Chat",
        "Icon.Fleet",
        // #1068: the Settings nav destination's gear — a control (where you go to adjust things), not a
        // state a room can be in, so it is deliberately not in the token-driven status ramp.
        "Icon.Settings",
    };

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
