using System.Text.Json;
using Aer.Adapters;
using Xunit;

namespace Aer.Adapters.Tests;

/// <summary>
/// #888: the shared worker-role catalog. Proves a role resolves its vendor/model/effort from its
/// tier (so a role never hardcodes a model), that a tier edit reaches every role on it with no
/// rebuild (the env override stands in for the runtime <c>worker-tiers.json</c> the operator drops),
/// and that a malformed catalog fails loudly rather than dispatching something nobody chose.
/// </summary>
public class WorkerRoleCatalogTests
{
    private sealed class EnvScope : IDisposable
    {
        private readonly List<(string Key, string? Prior)> _prior = [];

        public EnvScope Set(string key, string? value)
        {
            _prior.Add((key, Environment.GetEnvironmentVariable(key)));
            Environment.SetEnvironmentVariable(key, value);
            return this;
        }

        public void Dispose()
        {
            foreach (var (key, prior) in _prior)
            {
                Environment.SetEnvironmentVariable(key, prior);
            }
        }
    }

    private sealed class TempCatalog : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"wrc-{Guid.NewGuid():N}");

        public TempCatalog() => Directory.CreateDirectory(Dir);

        public string Write(string name, string content)
        {
            var path = Path.Combine(Dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Dir))
            {
                Directory.Delete(Dir, recursive: true);
            }
        }
    }

    private static string Role(string id, string tier, bool write = false, bool shell = false, bool net = false,
        int timeout = 10, bool verdict = false) =>
        $$"""
          {"id":"{{id}}","tier":"{{tier}}","read_files":true,"write_files":{{(write ? "true" : "false")}},
           "run_shell_commands":{{(shell ? "true" : "false")}},"network_access":{{(net ? "true" : "false")}},
           "timeout_minutes":{{timeout}},"verdict_schema":{{(verdict ? "true" : "false")}},"purpose":"p"}
          """;

    private static EnvScope PointAt(TempCatalog cat, string tiersJson, string rolesJson) =>
        new EnvScope()
            .Set(WorkerRoleCatalog.TiersPathEnvironmentVariable, cat.Write("tiers.json", tiersJson))
            .Set(WorkerRoleCatalog.RolesPathEnvironmentVariable, cat.Write("roles.json", rolesJson));

    // A test that reads the SHIPPED default must neutralize the runtime overrides first: with no env
    // set, ResolvePath falls through {AER_HOME|~/.aer}/worker-*.json, so on a machine where an
    // operator has used that documented override the test would silently read their file instead of
    // the shipped one. Point AER_HOME at an empty dir (no override present) and clear the env paths.
    private static EnvScope ShippedDefault(TempCatalog cat) =>
        new EnvScope()
            .Set("AER_HOME", cat.Dir)
            .Set(WorkerRoleCatalog.TiersPathEnvironmentVariable, null)
            .Set(WorkerRoleCatalog.RolesPathEnvironmentVariable, null);

    [Fact]
    public void The_shipped_catalog_resolves_each_role_against_its_tier()
    {
        using var cat = new TempCatalog();
        using var env = ShippedDefault(cat);

        var review = WorkerRoleCatalog.For("review");
        Assert.Equal("claude", review.Adapter);
        Assert.Equal("sonnet", review.Model);
        Assert.Equal("high", review.Effort);
        Assert.False(review.Grant.WriteFiles);
        Assert.True(review.ProducesVerdict);

        var implement = WorkerRoleCatalog.For("implement");
        Assert.Equal("gemini", implement.Adapter);
        Assert.True(implement.Grant.RunShellCommands);
        Assert.True(implement.Grant.NetworkAccess);
        Assert.False(implement.ProducesVerdict);
        Assert.Equal(TimeSpan.FromMinutes(40), implement.Timeout);
    }

    [Fact]
    public void One_tier_edit_reaches_every_role_on_that_tier_with_no_rebuild()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"shared":{"adapter":"gemini","model":"a-future-model","effort":null}}""",
            $"[{Role("a", "shared")},{Role("b", "shared", write: true)}]");

        Assert.Equal("a-future-model", WorkerRoleCatalog.For("a").Model);
        Assert.Equal("a-future-model", WorkerRoleCatalog.For("b").Model);
        Assert.False(WorkerRoleCatalog.For("a").Grant.WriteFiles);
        Assert.True(WorkerRoleCatalog.For("b").Grant.WriteFiles);
    }

    [Fact]
    public void A_role_naming_an_undefined_tier_fails_loudly()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"known":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("x", "missing")}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkerRoleCatalog.All);
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void A_duplicate_role_id_fails_loudly()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("dup", "t")},{Role("dup", "t")}]");

        Assert.Throws<InvalidOperationException>(() => _ = WorkerRoleCatalog.All);
    }

    [Fact]
    public void An_unknown_role_id_throws_naming_the_known_ones()
    {
        using var cat = new TempCatalog();
        using var env = ShippedDefault(cat);

        var ex = Assert.Throws<KeyNotFoundException>(() => WorkerRoleCatalog.For("does-not-exist"));
        Assert.Contains("review", ex.Message);
    }

    [Fact]
    public void A_role_missing_a_required_field_fails_loudly()
    {
        using var cat = new TempCatalog();
        // `purpose` omitted. Without [JsonRequired] this would deserialize to a null Purpose and ship a
        // role nobody authored; the catalog's contract is to fail at load, not at dispatch.
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            """[{"id":"x","tier":"t","read_files":true,"write_files":false,"run_shell_commands":false,"network_access":false,"timeout_minutes":10,"verdict_schema":false}]""");

        Assert.Throws<JsonException>(() => _ = WorkerRoleCatalog.All);
    }

    [Fact]
    public void A_catalog_file_with_comments_fails_loudly_so_both_readers_agree()
    {
        using var cat = new TempCatalog();
        // dispatch.py reads the same files through stdlib json.loads, which rejects comments. The C#
        // reader must reject them too, or an operator's inline // WHY loads in the engine and breaks
        // every dispatch.
        using var env = PointAt(
            cat,
            "{\n  // #742 operator directive\n  \"t\":{\"adapter\":\"gemini\",\"model\":\"m\",\"effort\":null}\n}",
            $"[{Role("x", "t")}]");

        Assert.Throws<JsonException>(() => _ = WorkerRoleCatalog.All);
    }
}
