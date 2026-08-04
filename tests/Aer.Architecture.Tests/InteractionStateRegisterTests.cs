using System.Text.Json;
using Aer.DesignTokens;

namespace Aer.Architecture.Tests;

/// <summary>
/// #616 check 1: every interaction state in the authoritative register
/// (<c>design/interaction-states.json</c>) either has a rendering path today — named artifacts
/// that must exist — or carries an explicit pointer to the work that will build it. A state with
/// neither is the silent absence 0020 forbids: something a surface must handle that nothing
/// renders and nothing tracks. (The register↔code agreement is not re-asserted here — the
/// <c>InteractionState</c> enum is generated from this file and string-compared by
/// <see cref="DesignTokenDriftTests"/>; a second copy of that check would itself be drift.)
/// </summary>
public class InteractionStateRegisterTests
{
    [Fact]
    public void Every_state_is_rendered_or_explicitly_pending()
    {
        var repositoryRoot = RepositoryRoot();
        var states = LoadStates(repositoryRoot);

        // Self-guard (the PlanConsistencyTests pattern): a parse that finds nothing must fail
        // rather than pass vacuously — the control arm for this whole file.
        Assert.True(states.Count > 0, $"No states parsed from {TokenGenerator.InteractionStatesPath} — has its format changed?");

        foreach (var (key, state) in states)
        {
            var hasRenderedBy = state.TryGetProperty("coverage", out var coverage)
                && coverage.TryGetProperty("renderedBy", out var renderedBy);
            var hasPending = state.TryGetProperty("coverage", out coverage)
                && coverage.TryGetProperty("pending", out var pending)
                && !string.IsNullOrWhiteSpace(pending.GetString());

            Assert.True(
                hasRenderedBy ^ hasPending,
                $"State '{key}' must declare exactly one of coverage.renderedBy (it is drawn today) " +
                "or coverage.pending (a pointer to the work that will draw it). Neither is a silent " +
                "absence; both is an ambiguous claim.");

            if (hasRenderedBy)
            {
                state.TryGetProperty("coverage", out coverage);
                coverage.TryGetProperty("renderedBy", out renderedBy);
                var artifacts = renderedBy.EnumerateArray().Select(a => a.GetString()!).ToList();
                Assert.True(artifacts.Count > 0, $"State '{key}' declares renderedBy but names no artifacts.");
                foreach (var artifact in artifacts)
                {
                    Assert.True(
                        File.Exists(Path.Combine(repositoryRoot, artifact)),
                        $"State '{key}' claims to be rendered by '{artifact}', which does not exist. " +
                        "A rendering claim over a missing file is exactly the stale record this register replaces.");
                }
            }
        }
    }

    [Fact]
    public void Every_state_carries_a_name_a_behaviour_and_a_provenance()
    {
        var states = LoadStates(RepositoryRoot());
        Assert.True(states.Count > 0, $"No states parsed from {TokenGenerator.InteractionStatesPath} — has its format changed?");

        foreach (var (key, state) in states)
        {
            foreach (var field in new[] { "name", "behaviour", "provenance" })
            {
                Assert.True(
                    state.TryGetProperty(field, out var value) && !string.IsNullOrWhiteSpace(value.GetString()),
                    $"State '{key}' is missing '{field}' — the register is the one home for it, so an empty field here is empty everywhere.");
            }
        }
    }

    private static Dictionary<string, JsonElement> LoadStates(string repositoryRoot)
    {
        var json = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.InteractionStatesPath));
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return document.RootElement.GetProperty("states").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private static string RepositoryRoot()
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

        throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory);
    }
}
