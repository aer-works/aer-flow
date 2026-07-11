using Aer.Flow.Concurrency;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using static Aer.Flow.Tests.TestSupport.ShellWorkerCommands;

namespace Aer.Flow.Tests.EndToEnd;

/// <summary>
/// M7's completion gate (issue #14): loads a real <c>WorkflowDefinition</c> template from a
/// fixture file — not one constructed in-memory — binds it, and runs the full linear happy path
/// through the single mutation surface, on a real filesystem, with the §15 concurrency guard
/// engaged for the whole run. No mocking of Aer.Core itself.
/// </summary>
public class WorkflowEndToEndTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");
    private static readonly StepId Publisher = new("publisher");

    [Fact]
    public async Task A_three_step_linear_workflow_loaded_from_a_fixture_file_runs_to_completion()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        var snapshotPath = Path.Combine(taskDirectory, "snapshot.json");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, snapshotPath);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "the-plan"),
                    TimeSpan.FromSeconds(30)),
                ["critic"] = new WorkerBinding(
                    new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                    CopyFirstInputTo("review"),
                    TimeSpan.FromSeconds(30)),
                ["publisher"] = new WorkerBinding(
                    new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                    CopyFirstInputTo("summary"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-e2e"), taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher);

            Assert.Equal(3, finalState.Steps.Count);
            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));

            var stepStateById = finalState.Steps.ToDictionary(s => s.StepId);
            await AssertOutputExistsAsync(artifactsRoot, stepStateById[Architect], "plan", "the-plan");
            await AssertOutputExistsAsync(artifactsRoot, stepStateById[Critic], "review", "the-plan");
            await AssertOutputExistsAsync(artifactsRoot, stepStateById[Publisher], "summary", "the-plan");

            // The guard (§15) was held for the whole run above; its lock file is left on disk once
            // released, proving the run actually went through it and that release doesn't erase
            // the file (a sentinel-file scheme would instead delete it to signal "unlocked").
            Assert.True(File.Exists(Path.Combine(taskDirectory, "flow.lock")));
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_second_concurrent_run_against_the_same_task_directory_is_rejected()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath);
            var snapshot = SnapshotBinder.Bind(definition);

            using var heldByAnotherInstance = ConcurrencyGuard.Acquire(taskDirectory);

            await using var writer = new FlowEventLogWriter(Path.Combine(taskDirectory, "flow.jsonl"));
            var reader = new FlowEventLogReader(Path.Combine(taskDirectory, "flow.jsonl"));
            var dispatcher = new CoreDispatcher(writer);

            await Assert.ThrowsAsync<WorkflowLockedException>(() => MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-e2e-locked"),
                taskDirectory,
                snapshot,
                new Dictionary<string, WorkerBinding>(),
                Path.Combine(taskDirectory, "artifacts"),
                reader,
                writer,
                dispatcher));
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    private static async Task AssertOutputExistsAsync(
        string artifactsRoot, StepState stepState, string outputName, string expectedContent)
    {
        var executionId = stepState.LatestExecutionId!.Value;
        var outputPath = Path.Combine(artifactsRoot, $"execution_{executionId}", outputName);

        Assert.True(File.Exists(outputPath));
        Assert.Equal(expectedContent, (await File.ReadAllTextAsync(outputPath)).Trim());
    }
}
