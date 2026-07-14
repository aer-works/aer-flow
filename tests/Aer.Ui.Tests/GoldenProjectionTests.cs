using System.Runtime.CompilerServices;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Ui.Tests.TestSupport;

namespace Aer.Ui.Tests;

/// <summary>
/// M14's completion gate (issue #122): three recorded task-directory fixtures — a completed run, a
/// paused run mid-decision, and a failed-and-retried run — each pumped through the real
/// <c>MutationInterface</c>/<c>CoreDispatcher</c> path every other <c>Aer.Ui.Tests</c> fixture uses
/// (no live vendor involved, shell-stub workers only, the <c>RunCommandEndToEndTests</c> convention),
/// then read back exclusively through <see cref="TaskProjectionLoader"/> and <see cref="DagLayoutEngine"/>
/// — never a <see cref="FlowState"/> built by hand — and asserted against a checked-in golden file via
/// <see cref="GoldenProjectionCanonicalizer"/>. This is UI spec §11 made executable: identical durable
/// state must always project to identical rendered state, on all three CI OSes. Wired into default CI
/// simply by being an ordinary <c>dotnet test</c> fact in this project — nothing here needs live
/// vendor auth, so (M13 Phase 4's reasoning) the gated-runbook pattern would be the wrong default;
/// <c>pixi run test</c> already runs this project on every OS in <c>ci.yml</c>'s <c>test</c> matrix.
/// </summary>
public class GoldenProjectionTests
{
    [Fact]
    public async Task A_completed_three_step_run_matches_its_golden_projection()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-golden-completed-{Guid.NewGuid():N}");
        try
        {
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(taskDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    ShellWorkerCommands.WriteFile("plan", "the-plan"),
                    TimeSpan.FromSeconds(30)),
                ["critic"] = new WorkerBinding.Process(
                    new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                    ShellWorkerCommands.CopyFirstInputTo("review"),
                    TimeSpan.FromSeconds(30)),
                ["publisher"] = new WorkerBinding.Process(
                    new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                    ShellWorkerCommands.CopyFirstInputTo("summary"),
                    TimeSpan.FromSeconds(30)),
            };

            var logPath = Path.Combine(taskDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                var reader = new FlowEventLogReader(logPath);
                var dispatcher = new CoreDispatcher(writer);

                await MutationInterface.StartWorkflowAsync(
                    new WorkflowId("wf-golden-completed"),
                    taskDirectory,
                    snapshot,
                    bindings,
                    Path.Combine(taskDirectory, "artifacts"),
                    reader,
                    writer,
                    dispatcher,
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            await AssertMatchesGoldenProjectionAsync(taskDirectory, "completed-run.golden.json");
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_run_paused_mid_decision_matches_its_golden_projection()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-golden-paused-{Guid.NewGuid():N}");
        try
        {
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "paused-run-workflow.json");
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(taskDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["a"] = new WorkerBinding.Process(
                    new WorkerContract("a", [], [new ProducedOutput("a-out")], []),
                    ShellWorkerCommands.WriteFile("a-out", "a-content"),
                    TimeSpan.FromSeconds(30)),
                ["b"] = new WorkerBinding.Process(
                    new WorkerContract("b", ["a-out"], [new ProducedOutput("b-out")], []),
                    ShellWorkerCommands.CopyFirstInputTo("b-out"),
                    TimeSpan.FromSeconds(30)),
                ["c"] = new WorkerBinding.Process(
                    new WorkerContract("c", ["b-out"], [new ProducedOutput("c-out")], []),
                    ShellWorkerCommands.CopyFirstInputTo("c-out"),
                    TimeSpan.FromSeconds(30)),
            };

            var logPath = Path.Combine(taskDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                var reader = new FlowEventLogReader(logPath);
                var dispatcher = new CoreDispatcher(writer);

                var pausedState = await MutationInterface.StartWorkflowAsync(
                    new WorkflowId("wf-golden-paused"),
                    taskDirectory,
                    snapshot,
                    bindings,
                    Path.Combine(taskDirectory, "artifacts"),
                    reader,
                    writer,
                    dispatcher,
                    cancellationToken: TestContext.Current.CancellationToken);

                Assert.Equal(WorkflowStatus.Paused, pausedState.Status);
            }

            await AssertMatchesGoldenProjectionAsync(taskDirectory, "paused-run.golden.json");
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_failed_and_retried_run_matches_its_golden_projection()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-golden-retried-{Guid.NewGuid():N}");
        try
        {
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "flaky-retry-workflow.json");
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(taskDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

            var markerFilePath = Path.Combine(taskDirectory, "flaky.marker");
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["flaky"] = new WorkerBinding.Process(
                    new WorkerContract("flaky", [], [new ProducedOutput("result")], []),
                    ShellWorkerCommands.FailOnFirstAttemptThenSucceed(markerFilePath, "result", "the-result"),
                    TimeSpan.FromSeconds(30)),
                ["downstream"] = new WorkerBinding.Process(
                    new WorkerContract("downstream", ["result"], [new ProducedOutput("final")], []),
                    ShellWorkerCommands.CopyFirstInputTo("final"),
                    TimeSpan.FromSeconds(30)),
            };

            var logPath = Path.Combine(taskDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                var reader = new FlowEventLogReader(logPath);
                var dispatcher = new CoreDispatcher(writer);

                var finalState = await MutationInterface.StartWorkflowAsync(
                    new WorkflowId("wf-golden-retried"),
                    taskDirectory,
                    snapshot,
                    bindings,
                    Path.Combine(taskDirectory, "artifacts"),
                    reader,
                    writer,
                    dispatcher,
                    cancellationToken: TestContext.Current.CancellationToken);

                Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            }

            await AssertMatchesGoldenProjectionAsync(taskDirectory, "failed-and-retried-run.golden.json");
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Projects the real task directory through <see cref="TaskProjectionLoader"/> and
    /// <see cref="DagLayoutEngine"/>, canonicalizes it, and compares against the checked-in golden
    /// file — or, with <c>AER_UPDATE_GOLDEN_FILES=1</c> set, (re)writes it. The golden file is
    /// resolved via <see cref="CallerFilePathAttribute"/> against this file's own source location
    /// (not <c>AppContext.BaseDirectory</c>, which only ever holds a copy under the build output),
    /// so update mode edits the source tree a developer would actually commit.
    /// </summary>
    private static async Task AssertMatchesGoldenProjectionAsync(
        string taskDirectory, string goldenFileName, [CallerFilePath] string testFilePath = "")
    {
        var projection = await TaskProjectionLoader.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);
        var dagLayout = DagLayoutEngine.Layout(projection.Snapshot.Steps);
        var actual = GoldenProjectionCanonicalizer.Canonicalize(projection, dagLayout);

        var goldenFilePath = Path.Combine(Path.GetDirectoryName(testFilePath)!, "Fixtures", "GoldenProjections", goldenFileName);

        if (Environment.GetEnvironmentVariable("AER_UPDATE_GOLDEN_FILES") == "1")
        {
            await File.WriteAllTextAsync(goldenFilePath, actual, TestContext.Current.CancellationToken);
            return;
        }

        var expected = await File.ReadAllTextAsync(goldenFilePath, TestContext.Current.CancellationToken);
        Assert.Equal(NormalizeLineEndings(expected), NormalizeLineEndings(actual));
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n");
}
