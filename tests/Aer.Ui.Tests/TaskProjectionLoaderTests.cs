using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Ui.Tests.TestSupport;

namespace Aer.Ui.Tests;

/// <summary>
/// M14 Phase 1's completion gate (issue #118): proves the seam end to end against a real task
/// directory — a real bound snapshot and a real Flow Event Store, produced through the exact same
/// <c>MutationInterface.StartWorkflowAsync</c> write path <c>Aer.Cli</c>'s <c>aer run</c> uses
/// (<c>Aer.Flow.Tests.EndToEnd.WorkflowEndToEndTests</c>' convention), then read back exclusively
/// through <see cref="TaskProjectionLoader"/> — never by constructing a <see cref="FlowState"/> by
/// hand.
/// </summary>
public class TaskProjectionLoaderTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");
    private static readonly StepId Publisher = new("publisher");

    [Fact]
    public async Task Loads_a_bound_snapshot_and_projects_state_from_a_real_task_directory()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-task-{Guid.NewGuid():N}");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(taskDirectory, "snapshot.json"));

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
                    new WorkflowId("wf-ui-e2e"),
                    taskDirectory,
                    snapshot,
                    bindings,
                    Path.Combine(taskDirectory, "artifacts"),
                    reader,
                    writer,
                    dispatcher);
            }

            var projection = await TaskProjectionLoader.LoadAsync(taskDirectory);

            // Not Assert.Equal(snapshot, projection.Snapshot): WorkflowDefinitionSnapshot's Steps
            // is a List<T>, which has no value-equality override, so a record freshly deserialized
            // from disk never structurally equals the in-memory instance it was persisted from.
            Assert.Equal(snapshot.WorkflowDefinitionSnapshotId, projection.Snapshot.WorkflowDefinitionSnapshotId);
            Assert.Equal(WorkflowStatus.Terminal, projection.State.Status);
            var stepStatusByStepId = projection.State.Steps.ToDictionary(step => step.StepId, step => step.Status);
            Assert.Equal(StepStatus.Succeeded, stepStatusByStepId[Architect]);
            Assert.Equal(StepStatus.Succeeded, stepStatusByStepId[Critic]);
            Assert.Equal(StepStatus.Succeeded, stepStatusByStepId[Publisher]);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_directory_with_no_snapshot_is_reported_as_not_a_task_directory()
    {
        var notATaskDirectory = Path.Combine(Path.GetTempPath(), $"ui-not-a-task-{Guid.NewGuid():N}");
        Directory.CreateDirectory(notATaskDirectory);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidTaskDirectoryException>(
                () => TaskProjectionLoader.LoadAsync(notATaskDirectory));

            Assert.Contains(notATaskDirectory, exception.Message);
        }
        finally
        {
            Directory.Delete(notATaskDirectory, recursive: true);
        }
    }
}
