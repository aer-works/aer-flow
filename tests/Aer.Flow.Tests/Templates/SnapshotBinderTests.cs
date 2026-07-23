using Aer.Flow.Tests.TestSupport;
using System.Text.Json;
using Aer.Flow.Domain;
using Aer.Flow.Templates;

namespace Aer.Flow.Tests.Templates;

public class SnapshotBinderTests
{
    private static WorkflowDefinition SampleDefinition() => new(
        new WorkflowTemplateId("architect-critic-synth"),
        WorkflowTemplateVersion: 3,
        Steps:
        [
            new WorkflowStepDefinition(
                new StepId("architect"),
                "architect",
                Inputs: ["goal"],
                Outputs: ["plan"],
                DependsOn: [],
                RetryPolicy: new RetryPolicy(MaxAttempts: 3)),
        ]);

    [Fact]
    public void Bind_freezes_the_template_id_and_version_alongside_a_new_snapshot_id()
    {
        var definition = SampleDefinition();

        var snapshot = SnapshotBinder.Bind(definition);

        Assert.Equal(definition.WorkflowTemplateId, snapshot.WorkflowTemplateId);
        Assert.Equal(definition.WorkflowTemplateVersion, snapshot.WorkflowTemplateVersion);
        Assert.Equal(definition.Steps, snapshot.Steps);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.WorkflowDefinitionSnapshotId.Value));
    }

    [Fact]
    public void Bind_generates_a_distinct_SnapshotId_on_every_call()
    {
        var definition = SampleDefinition();

        var first = SnapshotBinder.Bind(definition);
        var second = SnapshotBinder.Bind(definition);

        Assert.NotEqual(first.WorkflowDefinitionSnapshotId, second.WorkflowDefinitionSnapshotId);
    }

    [Fact]
    public void Bind_rejects_an_invalid_definition_even_when_not_parsed_from_a_file()
    {
        var invalid = new WorkflowDefinition(
            new WorkflowTemplateId("bad"),
            1,
            Steps:
            [
                new WorkflowStepDefinition(new StepId("a"), "worker", [], [], [new StepId("ghost")], new RetryPolicy(1)),
            ]);

        Assert.Throws<WorkflowDefinitionValidationException>(() => SnapshotBinder.Bind(invalid));
    }

    [Fact]
    public async Task PersistAsync_writes_a_snapshot_that_round_trips_through_JSON()
    {
        var snapshot = SnapshotBinder.Bind(SampleDefinition());
        var path = Path.Combine(Path.GetTempPath(), $"snapshot-{Guid.NewGuid():N}.json");
        try
        {
            await SnapshotBinder.PersistAsync(snapshot, path, TestContext.Current.CancellationToken);

            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            var reloaded = JsonSerializer.Deserialize<WorkflowDefinitionSnapshot>(json);

            Assert.NotNull(reloaded);
            Assert.Equal(JsonSerializer.Serialize(snapshot), JsonSerializer.Serialize(reloaded));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PersistAsync_creates_missing_parent_directories()
    {
        var snapshot = SnapshotBinder.Bind(SampleDefinition());
        var directory = Path.Combine(Path.GetTempPath(), $"snapshot-dir-{Guid.NewGuid():N}", "nested");
        var path = Path.Combine(directory, "snapshot.json");
        try
        {
            await SnapshotBinder.PersistAsync(snapshot, path, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(directory)))
            {
                DirectoryCleanup.DeleteRecursively(Path.GetDirectoryName(directory)!);
            }
        }
    }

    [Fact]
    public void PausePoint_deserialized_without_a_Kind_defaults_to_ReadyForReview()
    {
        // #334 backward-compat: a snapshot persisted before Kind existed has no "Kind" property on
        // its pause points. STJ materializes the missing constructor value as default(PausePointKind),
        // so ReadyForReview MUST be the zero value for every replayed pause to keep its original
        // approval-gate meaning — this test fails loudly if the enum members are ever reordered.
        var pausePoint = JsonSerializer.Deserialize<PausePoint>("""{"SupersedeTargets":[]}""");

        Assert.NotNull(pausePoint);
        Assert.Equal(PausePointKind.ReadyForReview, pausePoint.Kind);
        Assert.Equal(0, (int)PausePointKind.ReadyForReview);
    }

    [Fact]
    public void Bind_preserves_a_NeedsInput_pause_kind_through_its_JSON_round_trip()
    {
        // Bind serializes then re-parses the definition (freezing it), so this proves the kind
        // survives the durable-snapshot JSON round trip — the route #334 carries the distinction by,
        // in place of an event-format change.
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("session-like"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(new StepId("chat"), "w", [], ["out"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(
                    new StepId("anchor"), "w2", ["out"], ["marker"], [new StepId("chat")], new RetryPolicy(1),
                    PausePoint: new PausePoint([new StepId("chat")], PausePointKind.NeedsInput)),
            ]);

        var snapshot = SnapshotBinder.Bind(definition);

        var anchor = snapshot.Steps.Single(step => step.StepId.Value == "anchor");
        Assert.Equal(PausePointKind.NeedsInput, anchor.PausePoint!.Kind);
    }

    [Fact]
    public async Task Editing_the_source_template_file_after_binding_has_no_effect_on_the_persisted_snapshot()
    {
        var templatePath = Path.Combine(Path.GetTempPath(), $"template-{Guid.NewGuid():N}.json");
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"snapshot-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(templatePath, JsonSerializer.Serialize(SampleDefinition()), TestContext.Current.CancellationToken);
        try
        {
            var loaded = await WorkflowDefinitionParser.LoadFromFileAsync(templatePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(loaded);
            await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

            // Edit the template on disk after the snapshot was bound and persisted.
            var edited = SampleDefinition() with { WorkflowTemplateVersion = 4 };
            await File.WriteAllTextAsync(templatePath, JsonSerializer.Serialize(edited), TestContext.Current.CancellationToken);

            var reloaded = JsonSerializer.Deserialize<WorkflowDefinitionSnapshot>(await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken));

            Assert.NotNull(reloaded);
            Assert.Equal(3, reloaded.WorkflowTemplateVersion);
        }
        finally
        {
            File.Delete(templatePath);
            File.Delete(snapshotPath);
        }
    }
}
