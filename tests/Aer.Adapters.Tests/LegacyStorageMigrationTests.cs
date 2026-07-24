using Aer.Adapters;
using Xunit;

namespace Aer.Adapters.Tests;

/// <summary>
/// #333: the <c>tasks</c> -> <c>sessions</c> fold. This migration moves the user's real, irreplaceable
/// history, so the tests that matter are not the happy path -- they are the interrupted, repeated and
/// colliding ones. Each test owns a throwaway root and never touches <c>AER_HOME</c> or real
/// <c>~/.aer</c>.
/// </summary>
public sealed class LegacyStorageMigrationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "aer-333-" + Guid.NewGuid().ToString("N"));

    private string LegacyTasks => Path.Combine(_root, AerPaths.LegacyTasksDirectoryName);

    private string Sessions => Path.Combine(_root, AerPaths.SessionsDirectoryName);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Writes a plausible record: a snapshot, an event log and a nested artifact.</summary>
    private string GivenLegacyTask(string name, string marker = "content")
    {
        var directory = Path.Combine(LegacyTasks, name);
        Directory.CreateDirectory(Path.Combine(directory, "artifacts", "chat"));
        File.WriteAllText(Path.Combine(directory, "snapshot.json"), $$"""{"name":"{{name}}"}""");
        File.WriteAllText(Path.Combine(directory, "flow.jsonl"), $"{{\"event\":\"{marker}\"}}\n");
        File.WriteAllText(Path.Combine(directory, "artifacts", "chat", "response.md"), marker);
        return directory;
    }

    private string GivenExistingSession(string name)
    {
        var directory = Path.Combine(Sessions, name);
        Directory.CreateDirectory(Path.Combine(directory, ".aer"));
        File.WriteAllText(Path.Combine(directory, ".aer", "session.json"), $$"""{"SessionId":"{{name}}"}""");
        File.WriteAllText(Path.Combine(directory, "snapshot.json"), """{"kind":"pre-existing"}""");
        return directory;
    }

    [Fact]
    public async Task MigratesEveryRecordAndNeverTouchesTheOriginal()
    {
        GivenLegacyTask("review-run");

        var result = await LegacyStorageMigration.RunAsync(_root, TestContext.Current.CancellationToken);

        Assert.True(result.Ran);
        Assert.Equal(1, result.RecordsMigrated);

        // Arrived, whole -- including the nested artifact tree.
        Assert.True(File.Exists(Path.Combine(Sessions, "review-run", "snapshot.json")));
        Assert.True(File.Exists(Path.Combine(Sessions, "review-run", "flow.jsonl")));
        Assert.Equal("content", File.ReadAllText(Path.Combine(Sessions, "review-run", "artifacts", "chat", "response.md")));

        // Copy, never move: the source is still fully intact, so the fold stays reversible.
        Assert.True(File.Exists(Path.Combine(LegacyTasks, "review-run", "snapshot.json")));
        Assert.True(File.Exists(Path.Combine(LegacyTasks, "review-run", "artifacts", "chat", "response.md")));
    }

    [Fact]
    public async Task PreservesSnapshotMtime_SoIssue322sCreatedTimestampDoesNotStartLying()
    {
        // #322 derives a run's `created` from snapshot.json's mtime, valid only because that file is
        // written once and never mutated (spec §11.2). A plain copy restamps it with the migration's
        // own wall clock and silently rewrites user-visible history -- with nothing checking it.
        var source = GivenLegacyTask("dated-run");
        var createdAt = DateTime.UtcNow.AddDays(-45);
        var snapshot = Path.Combine(source, "snapshot.json");
        File.SetLastWriteTimeUtc(snapshot, createdAt);

        await LegacyStorageMigration.RunAsync(_root, TestContext.Current.CancellationToken);

        var migrated = File.GetLastWriteTimeUtc(Path.Combine(Sessions, "dated-run", "snapshot.json"));
        Assert.Equal(createdAt, migrated, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RunningTwiceIsANoOp_NotADoubleMigrate()
    {
        GivenLegacyTask("once");

        var first = await LegacyStorageMigration.RunAsync(_root, TestContext.Current.CancellationToken);
        var second = await LegacyStorageMigration.RunAsync(_root, TestContext.Current.CancellationToken);

        Assert.True(first.Ran);
        Assert.False(second.Ran);
        Assert.Equal(0, second.RecordsMigrated);
        Assert.Single(Directory.GetDirectories(Sessions));
    }

    [Fact]
    public async Task CancellationLeavesNoMarker_SoTheNextStartRetries()
    {
        GivenLegacyTask("alpha");
        using var alreadyCancelled = new CancellationTokenSource();
        await alreadyCancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => LegacyStorageMigration.RunAsync(_root, alreadyCancelled.Token));

        // The marker is the "never do this again" flag. Writing it on a run that did not verify
        // would permanently skip a migration that never happened.
        Assert.False(File.Exists(Path.Combine(_root, LegacyStorageMigration.CompletionMarkerFileName)));
        Assert.True(File.Exists(Path.Combine(LegacyTasks, "alpha", "snapshot.json")));
    }

    [Fact]
    public async Task ResumesFromAHalfCopiedRecord_WithoutLosingOrDuplicatingAnything()
    {
        // The teeth test, built deterministically rather than by racing a timer: a timing-based
        // cancellation on a handful of small files may never land mid-copy, and a test that quietly
        // stops interrupting stops testing resumption at all. So this reconstructs exactly what a
        // killed daemon leaves on disk -- plan persisted, one record whole, the next truncated, no
        // marker -- and asserts the next run finishes the job.
        GivenLegacyTask("alpha", marker: "alpha-content");
        GivenLegacyTask("beta", marker: "beta-content");

        Directory.CreateDirectory(_root);
        var plan = new Dictionary<string, string>
        {
            [Path.Combine(LegacyTasks, "alpha")] = Path.Combine(Sessions, "alpha"),
            [Path.Combine(LegacyTasks, "beta")] = Path.Combine(Sessions, "beta"),
        };
        await File.WriteAllTextAsync(
            Path.Combine(_root, LegacyStorageMigration.PlanFileName),
            System.Text.Json.JsonSerializer.Serialize(plan),
            TestContext.Current.CancellationToken);

        // alpha: copied whole. beta: cut off after the first file, artifact tree missing entirely.
        CopyTree(Path.Combine(LegacyTasks, "alpha"), Path.Combine(Sessions, "alpha"));
        Directory.CreateDirectory(Path.Combine(Sessions, "beta"));
        await File.WriteAllTextAsync(
            Path.Combine(Sessions, "beta", "snapshot.json"), "truncated", TestContext.Current.CancellationToken);

        var resumed = await LegacyStorageMigration.RunAsync(_root, TestContext.Current.CancellationToken);

        Assert.True(resumed.Ran);

        // Both records are now whole, and the truncated file was completed rather than left short.
        Assert.Equal("alpha-content", File.ReadAllText(Path.Combine(Sessions, "alpha", "artifacts", "chat", "response.md")));
        Assert.Equal("beta-content", File.ReadAllText(Path.Combine(Sessions, "beta", "artifacts", "chat", "response.md")));
        Assert.Equal(
            File.ReadAllText(Path.Combine(LegacyTasks, "beta", "snapshot.json")),
            File.ReadAllText(Path.Combine(Sessions, "beta", "snapshot.json")));

        // Resumed into the planned destinations -- no duplicate, differently-suffixed copies.
        Assert.Equal(2, Directory.GetDirectories(Sessions).Length);
        Assert.True(File.Exists(Path.Combine(_root, LegacyStorageMigration.CompletionMarkerFileName)));
        Assert.Equal(2, Directory.GetDirectories(LegacyTasks).Length);
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    [Fact]
    public async Task NameCollisionWithAnExistingSession_DoesNotOverwriteIt()
    {
        // A legacy task and a live session can share a folder name. Clobbering the session would
        // destroy real history, so the migrated record takes a suffixed destination.
        GivenExistingSession("shared-name");
        GivenLegacyTask("shared-name", marker: "from-legacy-task");

        var result = await LegacyStorageMigration.RunAsync(_root, TestContext.Current.CancellationToken);

        Assert.True(result.Ran);

        // The pre-existing session is byte-for-byte untouched.
        Assert.Equal(
            """{"kind":"pre-existing"}""",
            File.ReadAllText(Path.Combine(Sessions, "shared-name", "snapshot.json")));

        // And the legacy record still landed, beside it.
        var migrated = Path.Combine(Sessions, "shared-name-workflow");
        Assert.True(Directory.Exists(migrated));
        Assert.Equal("from-legacy-task", File.ReadAllText(Path.Combine(migrated, "artifacts", "chat", "response.md")));
    }

    [Fact]
    public async Task CollisionResolutionSurvivesAReRun_RatherThanSuffixingAgain()
    {
        // Recomputing destinations per run is the subtle failure the persisted plan exists to stop:
        // after one run the suffixed name is itself taken, so a naive "is it free?" test would pick
        // "shared-name-workflow-2" the second time and scatter duplicate half-copies.
        GivenExistingSession("shared-name");
        GivenLegacyTask("shared-name");

        await LegacyStorageMigration.RunAsync(_root, TestContext.Current.CancellationToken);
        await LegacyStorageMigration.RunAsync(_root, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(Sessions, "shared-name-workflow-2")));
        Assert.Equal(2, Directory.GetDirectories(Sessions).Length);
    }

    [Fact]
    public async Task NoLegacyRoot_MarksCompleteSoLaterStartsTakeTheFastPath()
    {
        Directory.CreateDirectory(_root);

        var result = await LegacyStorageMigration.RunAsync(_root, TestContext.Current.CancellationToken);

        Assert.False(result.Ran);
        Assert.True(File.Exists(Path.Combine(_root, LegacyStorageMigration.CompletionMarkerFileName)));
    }

    [Fact]
    public async Task ExistingSessionsAreLeftWhereTheyAre()
    {
        // The 24-sessions case: they are already in the surviving root, so migration must not move,
        // rewrite or re-stamp them.
        GivenExistingSession("untouched");
        var snapshot = Path.Combine(Sessions, "untouched", "snapshot.json");
        var originalMtime = DateTime.UtcNow.AddDays(-10);
        File.SetLastWriteTimeUtc(snapshot, originalMtime);
        GivenLegacyTask("some-task");

        await LegacyStorageMigration.RunAsync(_root, TestContext.Current.CancellationToken);

        Assert.Equal("""{"kind":"pre-existing"}""", File.ReadAllText(snapshot));
        Assert.Equal(originalMtime, File.GetLastWriteTimeUtc(snapshot), TimeSpan.FromSeconds(2));
    }
}
