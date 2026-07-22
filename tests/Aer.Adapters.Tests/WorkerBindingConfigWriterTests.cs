using Aer.Adapters.Tests.TestSupport;
using Aer.Flow.Domain;

namespace Aer.Adapters.Tests;

/// <summary>
/// The bindings write seam's round-trip bar (M16 Phase 4, issue #153): a saved file must
/// round-trip through the exact <see cref="WorkerBindingConfigParser.Parse"/> every other consumer
/// uses — provable at this layer precisely because the writer lives beside its parser (the phase's
/// placement decision of record).
/// </summary>
public class WorkerBindingConfigWriterTests
{
    private static Dictionary<string, WorkerBindingConfigEntry> TwoWorkerConfig() => new()
    {
        ["architect"] = new WorkerBindingConfigEntry(
            "claude",
            new WorkerContract(
                "architect",
                RequiredInputs: [],
                ProducedOutputs:
                [
                    // Exercises every JsonScalar variant through OutputCondition — the one spot the
                    // opaque produced-outputs round trip (Aer.Ui's WorkerBindingEntryViewModel) could
                    // silently lose fidelity if it were tested with a bare { "Name": ... } only.
                    new ProducedOutput("plan", new OutputCondition("/status", new JsonScalar.String("done"))),
                ],
                OptionalMetadata: ["priority"]),
            "Draft a plan and write it to your output file.",
            TimeSpan.FromMinutes(5),
            Model: "claude-opus-4",
            PermissionScope: "write-only",
            WorkingDirectory: "/home/user/my-project"),
        ["critic"] = new WorkerBindingConfigEntry(
            "gemini",
            new WorkerContract(
                "critic",
                RequiredInputs: ["plan"],
                ProducedOutputs:
                [
                    new ProducedOutput("review", new OutputCondition("/score", new JsonScalar.Number(1))),
                    new ProducedOutput("flag", new OutputCondition("/approved", new JsonScalar.Boolean(true))),
                    new ProducedOutput("note", new OutputCondition("/reason", JsonScalar.Null.Instance)),
                ],
                OptionalMetadata: []),
            "Review the plan.",
            TimeSpan.FromMinutes(1)),
    };

    [Fact]
    public async Task A_saved_config_round_trips_through_the_engines_own_parser()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bindings-writer-{Guid.NewGuid():N}.json");
        try
        {
            var config = TwoWorkerConfig();

            await WorkerBindingConfigWriter.SaveToFileAsync(config, path, TestContext.Current.CancellationToken);
            var parsed = await WorkerBindingConfigParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(config.Keys.OrderBy(k => k), parsed.Keys.OrderBy(k => k));
            foreach (var (workerName, entry) in config)
            {
                var parsedEntry = parsed[workerName];
                Assert.Equal(entry.Adapter, parsedEntry.Adapter);
                Assert.Equal(entry.PromptTemplate, parsedEntry.PromptTemplate);
                Assert.Equal(entry.Timeout, parsedEntry.Timeout);
                Assert.Equal(entry.Model, parsedEntry.Model);
                Assert.Equal(entry.PermissionScope, parsedEntry.PermissionScope);
                Assert.Equal(entry.WorkingDirectory, parsedEntry.WorkingDirectory);
                Assert.Equal(entry.Contract.WorkerName, parsedEntry.Contract.WorkerName);
                Assert.Equal(entry.Contract.RequiredInputs, parsedEntry.Contract.RequiredInputs);
                Assert.Equal(entry.Contract.OptionalMetadata, parsedEntry.Contract.OptionalMetadata);
                Assert.Equal(entry.Contract.ProducedOutputs.Count, parsedEntry.Contract.ProducedOutputs.Count);
                for (var i = 0; i < entry.Contract.ProducedOutputs.Count; i++)
                {
                    Assert.Equal(entry.Contract.ProducedOutputs[i].Name, parsedEntry.Contract.ProducedOutputs[i].Name);
                    Assert.Equal(entry.Contract.ProducedOutputs[i].Condition, parsedEntry.Contract.ProducedOutputs[i].Condition);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task An_empty_config_is_valid_and_round_trips()
    {
        // The editor's New action mints exactly this shape (M16 Phase 4) — an empty config passes
        // the parser's checks (nothing to iterate), so a just-created bindings file is already a
        // parseable file.
        var path = Path.Combine(Path.GetTempPath(), $"bindings-writer-empty-{Guid.NewGuid():N}.json");
        try
        {
            await WorkerBindingConfigWriter.SaveToFileAsync(new Dictionary<string, WorkerBindingConfigEntry>(), path, TestContext.Current.CancellationToken);
            var parsed = await WorkerBindingConfigParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);

            Assert.Empty(parsed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task An_entry_with_a_blank_adapter_is_rejected_at_write_time_and_nothing_is_written()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bindings-writer-invalid-{Guid.NewGuid():N}.json");
        var invalid = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                string.Empty,
                new WorkerContract("architect", [], [], []),
                "Draft a plan.",
                TimeSpan.FromMinutes(5)),
        };

        var exception = await Assert.ThrowsAsync<WorkerBindingConfigException>(
            () => WorkerBindingConfigWriter.SaveToFileAsync(invalid, path, TestContext.Current.CancellationToken));

        Assert.Contains("Adapter", exception.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task SaveToFileAsync_creates_missing_parent_directories()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bindings-writer-dirs-{Guid.NewGuid():N}", "nested");
        var path = Path.Combine(directory, "bindings.json");
        try
        {
            await WorkerBindingConfigWriter.SaveToFileAsync(TwoWorkerConfig(), path, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(Path.GetDirectoryName(directory)!);
        }
    }

    [Fact]
    public void Serialize_emits_indented_human_editable_json()
    {
        var json = WorkerBindingConfigWriter.Serialize(TwoWorkerConfig());

        Assert.Contains("\n", json);
        Assert.Equal(2, WorkerBindingConfigParser.Parse(json).Count);
    }
}
