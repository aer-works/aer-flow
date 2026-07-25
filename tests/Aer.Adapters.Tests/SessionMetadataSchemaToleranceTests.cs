using Aer.Adapters;
using Xunit;

namespace Aer.Adapters.Tests;

/// <summary>
/// #521: removing <c>MinimalOverhead</c> deleted a field that had been serialized into every
/// <c>session.json</c> ever written. This pins the property that makes such a removal safe.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InteractiveSessionMaterializer.LoadMetadataAsync"/> configures no
/// <c>UnmappedMemberHandling</c>, so System.Text.Json's default (<c>Skip</c>) applies and an unknown
/// key is ignored. That is a property of the loader's options, not of the record — flipping it to
/// <c>Disallow</c>, or adding a converter that rejects unknown keys, would make every pre-existing
/// session file throw on load. Nothing else in the suite would notice, because every other fixture
/// is written by the current serializer and therefore never carries a key the current record lacks.
/// </para>
/// <para>
/// The fixture deliberately carries two unknown keys: <c>minimalOverhead</c>, the field this issue
/// removed, and <c>aerNotARealField</c>, which never existed. Asserting on the removed field alone
/// would still pass under a loader that special-cased it; the second key is the control that makes
/// this a claim about unknown-key tolerance in general.
/// </para>
/// </remarks>
public class SessionMetadataSchemaToleranceTests
{
    [Fact]
    public async Task A_session_file_carrying_removed_and_unknown_fields_still_loads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aer-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session.json");

        try
        {
            // Shaped like a session.json written before #521, plus a key that never existed.
            await File.WriteAllTextAsync(path, """
                {
                  "sessionId": "sess-legacy-001",
                  "taskDirectoryPath": "C:\\tmp\\legacy-room",
                  "currentAdapter": "claude",
                  "currentVendorSessionId": "vendor-abc",
                  "model": "claude-haiku-4-5-20251001",
                  "workingDirectory": null,
                  "turnCount": 3,
                  "safetyCeiling": 200,
                  "minimalOverhead": true,
                  "aerNotARealField": {"nested": ["anything", 1, null]},
                  "createdAt": "2026-07-01T10:00:00+00:00",
                  "updatedAt": "2026-07-01T10:05:00+00:00",
                  "turns": [],
                  "vendorSessionEstablished": true
                }
                """, TestContext.Current.CancellationToken);

            var metadata = await InteractiveSessionMaterializer.LoadMetadataAsync(
                path, TestContext.Current.CancellationToken);

            Assert.NotNull(metadata);

            // The unknown keys must be skipped rather than throwing -- and the fields either side of
            // them must survive, so a "load" that silently produced a default-everything record
            // cannot pass.
            Assert.Equal("sess-legacy-001", metadata.SessionId);
            Assert.Equal("claude", metadata.CurrentAdapter);
            Assert.Equal("vendor-abc", metadata.CurrentVendorSessionId);
            Assert.Equal(3, metadata.TurnCount);
            Assert.Equal(200, metadata.SafetyCeiling);
            Assert.True(metadata.VendorSessionEstablished,
                "a field declared AFTER the unknown keys was dropped, so the reader stopped early "
                + "rather than skipping them");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The same tolerance, for the OTHER loader — an operator-authored <c>bindings.json</c>.
    /// </summary>
    /// <remarks>
    /// `session.json` and `bindings.json` are read by different code with different
    /// <c>JsonSerializerOptions</c> (`LoadMetadataAsync` sets `PropertyNameCaseInsensitive`;
    /// <see cref="WorkerBindingConfigParser"/> passes none at all). Two readers with independently
    /// configurable strictness both had to tolerate the removed key, so both are pinned — testing
    /// only one would leave the other free to start rejecting operator config that AER itself wrote
    /// before #521.
    /// </remarks>
    [Fact]
    public void A_bindings_file_carrying_the_removed_field_still_parses()
    {
        // Shaped like a bindings.json authored before #521 -- PascalCase keys, matching
        // tests/Aer.Cli.SmokeTests/Fixtures/*.json, because this parser passes no
        // PropertyNameCaseInsensitive and a camelCase fixture fails for the wrong reason.
        var json = """
            {
              "chat-worker": {
                "Adapter": "claude",
                "PromptTemplate": "Hello",
                "MinimalOverhead": true,
                "AerNotARealField": 42,
                "Contract": {
                  "WorkerName": "chat-worker",
                  "RequiredInputs": [],
                  "ProducedOutputs": [{ "Name": "response.md" }],
                  "OptionalMetadata": []
                }
              }
            }
            """;

        var entries = WorkerBindingConfigParser.Parse(json);

        Assert.True(entries.ContainsKey("chat-worker"));
        var entry = entries["chat-worker"];
        Assert.Equal("claude", entry.Adapter);
        Assert.Equal("Hello", entry.PromptTemplate);
        Assert.Equal("chat-worker", entry.Contract.WorkerName);
    }
}
