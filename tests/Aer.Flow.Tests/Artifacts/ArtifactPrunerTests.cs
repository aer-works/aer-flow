using Aer.Flow.Artifacts;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Flow.Tests.TestSupport;

namespace Aer.Flow.Tests.Artifacts;

public class ArtifactPrunerTests
{
    private static readonly StepId StepA = new("stepA");

    private static WorkflowDefinitionSnapshot SingleStepSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("single-step"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(StepA, "worker", [], ["output.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
        ]);

    private static ExecutionRequest TestRequest(ExecutionId execId) => new(
        execId,
        new WorkflowId("wf-1"),
        StepA,
        "worker",
        Inputs: [],
        Outputs: ["output.txt"],
        Timeout: TimeSpan.FromMinutes(1),
        Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>()
    );

    private static async Task WriteLogEventsAsync(string logPath, params FlowEvent[] events)
    {
        await using var writer = new FlowEventLogWriter(logPath);
        foreach (var @event in events)
        {
            await writer.AppendAsync(@event);
        }
    }

    [Fact]
    public async Task PruneAsync_moves_completed_terminal_run_artifacts_to_pruned_location()
    {
        var taskDir = Path.Combine(Path.GetTempPath(), $"prune-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(taskDir);
            var snapshotPath = Path.Combine(taskDir, "snapshot.json");
            var logPath = Path.Combine(taskDir, "flow.jsonl");

            await SnapshotBinder.PersistAsync(SingleStepSnapshot(), snapshotPath, TestContext.Current.CancellationToken);

            var execId = new ExecutionId("exec-101");
            await WriteLogEventsAsync(
                logPath,
                new FlowEvent.ExecutionRequestAccepted(TestRequest(execId)),
                new FlowEvent.ExecutionSucceeded(execId)
            );

            var artifactsRoot = Path.Combine(taskDir, ArtifactManager.ArtifactsDirectoryName);
            var execDir = ArtifactManager.AllocateOutputDirectory(artifactsRoot, execId);
            var artifactFile = Path.Combine(execDir, "output.txt");
            await File.WriteAllTextAsync(artifactFile, "artifact-data", TestContext.Current.CancellationToken);

            // Verify active state before prune
            Assert.True(Directory.Exists(execDir));
            Assert.True(File.Exists(artifactFile));

            var result = await ArtifactPruner.PruneAsync(taskDir, TestContext.Current.CancellationToken);

            Assert.True(result);
            Assert.False(Directory.Exists(execDir));

            var prunedDir = ArtifactManager.ResolvePrunedOutputDirectory(artifactsRoot, execId);
            Assert.True(Directory.Exists(prunedDir));
            Assert.True(File.Exists(Path.Combine(prunedDir, "output.txt")));
            Assert.Equal("artifact-data", await File.ReadAllTextAsync(Path.Combine(prunedDir, "output.txt"), TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDir);
        }
    }

    [Fact]
    public async Task PruneAsync_untouches_running_or_paused_runs()
    {
        var taskDir = Path.Combine(Path.GetTempPath(), $"prune-running-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(taskDir);
            var snapshotPath = Path.Combine(taskDir, "snapshot.json");
            var logPath = Path.Combine(taskDir, "flow.jsonl");

            await SnapshotBinder.PersistAsync(SingleStepSnapshot(), snapshotPath, TestContext.Current.CancellationToken);

            var execId = new ExecutionId("exec-102");
            await WriteLogEventsAsync(
                logPath,
                new FlowEvent.ExecutionRequestAccepted(TestRequest(execId))
            );

            var artifactsRoot = Path.Combine(taskDir, ArtifactManager.ArtifactsDirectoryName);
            var execDir = ArtifactManager.AllocateOutputDirectory(artifactsRoot, execId);
            await File.WriteAllTextAsync(Path.Combine(execDir, "output.txt"), "in-flight", TestContext.Current.CancellationToken);

            var result = await ArtifactPruner.PruneAsync(taskDir, TestContext.Current.CancellationToken);

            Assert.False(result);
            Assert.True(Directory.Exists(execDir));
            var prunedDir = ArtifactManager.ResolvePrunedOutputDirectory(artifactsRoot, execId);
            Assert.False(Directory.Exists(prunedDir));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDir);
        }
    }

    [Fact]
    public async Task PruneAsync_untouches_keep_marked_runs()
    {
        var taskDir = Path.Combine(Path.GetTempPath(), $"prune-keep-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(taskDir);
            var snapshotPath = Path.Combine(taskDir, "snapshot.json");
            var logPath = Path.Combine(taskDir, "flow.jsonl");

            await SnapshotBinder.PersistAsync(SingleStepSnapshot(), snapshotPath, TestContext.Current.CancellationToken);

            var execId = new ExecutionId("exec-103");
            await WriteLogEventsAsync(
                logPath,
                new FlowEvent.ExecutionRequestAccepted(TestRequest(execId)),
                new FlowEvent.ExecutionSucceeded(execId)
            );

            await KeepMarker.MarkKeepAsync(taskDir, TestContext.Current.CancellationToken);
            Assert.True(KeepMarker.IsKept(taskDir));

            var artifactsRoot = Path.Combine(taskDir, ArtifactManager.ArtifactsDirectoryName);
            var execDir = ArtifactManager.AllocateOutputDirectory(artifactsRoot, execId);
            await File.WriteAllTextAsync(Path.Combine(execDir, "output.txt"), "kept-artifact", TestContext.Current.CancellationToken);

            var result = await ArtifactPruner.PruneAsync(taskDir, TestContext.Current.CancellationToken);

            Assert.False(result);
            Assert.True(Directory.Exists(execDir));
            var prunedDir = ArtifactManager.ResolvePrunedOutputDirectory(artifactsRoot, execId);
            Assert.False(Directory.Exists(prunedDir));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDir);
        }
    }

    [Fact]
    public async Task PruneAsync_is_idempotent_on_repeated_calls()
    {
        var taskDir = Path.Combine(Path.GetTempPath(), $"prune-idempotent-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(taskDir);
            var snapshotPath = Path.Combine(taskDir, "snapshot.json");
            var logPath = Path.Combine(taskDir, "flow.jsonl");

            await SnapshotBinder.PersistAsync(SingleStepSnapshot(), snapshotPath, TestContext.Current.CancellationToken);

            var execId = new ExecutionId("exec-104");
            await WriteLogEventsAsync(
                logPath,
                new FlowEvent.ExecutionRequestAccepted(TestRequest(execId)),
                new FlowEvent.ExecutionSucceeded(execId)
            );

            var artifactsRoot = Path.Combine(taskDir, ArtifactManager.ArtifactsDirectoryName);
            var execDir = ArtifactManager.AllocateOutputDirectory(artifactsRoot, execId);
            await File.WriteAllTextAsync(Path.Combine(execDir, "data.bin"), "data", TestContext.Current.CancellationToken);

            var firstRun = await ArtifactPruner.PruneAsync(taskDir, TestContext.Current.CancellationToken);
            Assert.True(firstRun);

            var secondRun = await ArtifactPruner.PruneAsync(taskDir, TestContext.Current.CancellationToken);
            Assert.False(secondRun);

            var prunedDir = ArtifactManager.ResolvePrunedOutputDirectory(artifactsRoot, execId);
            Assert.True(Directory.Exists(prunedDir));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDir);
        }
    }

    [Fact]
    public void PruneDirectory_handles_existing_target_directory_crash_recovery()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"prune-crash-{Guid.NewGuid():N}");
        try
        {
            var sourceDir = Path.Combine(tempRoot, "artifacts", "execution_exec-105");
            var targetDir = Path.Combine(tempRoot, "artifacts", "pruned", "execution_exec-105");

            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "valid.txt"), "new-content");

            // Simulate pre-existing target from interrupted attempt
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(Path.Combine(targetDir, "stale.txt"), "stale-content");

            ArtifactPruner.PruneDirectory(sourceDir, targetDir);

            Assert.False(Directory.Exists(sourceDir));
            Assert.True(Directory.Exists(targetDir));
            Assert.True(File.Exists(Path.Combine(targetDir, "valid.txt")));
            Assert.False(File.Exists(Path.Combine(targetDir, "stale.txt")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempRoot);
        }
    }

    [Fact]
    public void ResolvePrunedOutputDirectory_returns_expected_path()
    {
        var path = ArtifactManager.ResolvePrunedOutputDirectory("/artifacts", new ExecutionId("exec-999"));
        Assert.Equal(Path.Combine("/artifacts", "pruned", "execution_exec-999"), path);
    }
}
