using Aer.Adapters;
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
                    new WorkflowId("wf-ui-e2e"),
                    taskDirectory,
                    snapshot,
                    bindings,
                    Path.Combine(taskDirectory, "artifacts"),
                    reader,
                    writer,
                    dispatcher,
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            var projection = await TaskProjectionLoader.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            // Not Assert.Equal(snapshot, projection.Snapshot): WorkflowDefinitionSnapshot's Steps
            // is a List<T>, which has no value-equality override, so a record freshly deserialized
            // from disk never structurally equals the in-memory instance it was persisted from.
            Assert.Equal(snapshot.WorkflowDefinitionSnapshotId, projection.Snapshot.WorkflowDefinitionSnapshotId);
            Assert.Equal(WorkflowStatus.Terminal, projection.State.Status);
            var stepStatusByStepId = projection.State.Steps.ToDictionary(step => step.StepId, step => step.Status);
            Assert.Equal(StepStatus.Succeeded, stepStatusByStepId[Architect]);
            Assert.Equal(StepStatus.Succeeded, stepStatusByStepId[Critic]);
            Assert.Equal(StepStatus.Succeeded, stepStatusByStepId[Publisher]);

            // M14 Phase 4 (issue #121): the same run also projects real artifact lineage — actual
            // files on disk, and each downstream step's input traced back to the exact upstream
            // execution that produced it.
            var executionByStepId = projection.Lineage.Executions
                .Where(execution => execution.StepId is not null)
                .ToDictionary(execution => execution.StepId!.Value);

            Assert.Equal(["plan"], executionByStepId[Architect].OutputFiles);
            Assert.Empty(executionByStepId[Architect].Inputs);

            var criticInput = Assert.Single(executionByStepId[Critic].Inputs);
            Assert.Equal("plan", criticInput.InputName);
            Assert.Equal(Architect, criticInput.ProducerStepId);
            Assert.Equal(executionByStepId[Architect].ExecutionId, criticInput.ProducerExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    [Fact]
    public async Task LoadFleetStatusAsync_ReportsStatusAndArchivedStateWithoutRequiringLineageProjection()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-fleet-{Guid.NewGuid():N}");
        try
        {
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
                    new WorkflowId("wf-ui-fleet"), taskDirectory, snapshot, bindings,
                    Path.Combine(taskDirectory, "artifacts"), reader, writer, dispatcher,
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            var fleetItem = await TaskProjectionLoader.LoadFleetStatusAsync(taskDirectory, TestContext.Current.CancellationToken);
            Assert.Equal(Path.GetFileName(taskDirectory), fleetItem.FriendlyName);
            Assert.Equal(snapshot.WorkflowTemplateId.Value, fleetItem.TypeLabel);
            Assert.Equal(WorkflowStatus.Terminal.ToString(), fleetItem.StatusText);
            Assert.Equal(0, fleetItem.PausedStepCount);
            Assert.False(fleetItem.IsArchived);

            // #322: a DAG task carries no serialized timestamp, so created/updated come from its own
            // data files -- snapshot.json (written once at creation) and flow.jsonl (append-only).
            Assert.NotEqual(default, fleetItem.Created);
            Assert.NotEqual(default, fleetItem.Updated);
            Assert.True(fleetItem.Updated >= fleetItem.Created);
            Assert.Equal(
                new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(taskDirectory, "snapshot.json"))),
                fleetItem.Created);
            Assert.Equal(
                new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(taskDirectory, "flow.jsonl"))),
                fleetItem.Updated);

            await TaskLifecycle.ArchiveAsync(taskDirectory, TestContext.Current.CancellationToken);
            var archivedItem = await TaskProjectionLoader.LoadFleetStatusAsync(taskDirectory, TestContext.Current.CancellationToken);
            Assert.True(archivedItem.IsArchived);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    [Fact]
    public async Task LoadFleetStatusAsync_ForASessionNeverRun_ReportsNotYetRunInsteadOfThrowing()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-fleet-session-{Guid.NewGuid():N}");
        try
        {
            await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                "sess-fleet", taskDirectory, "claude", cancellationToken: TestContext.Current.CancellationToken);

            var fleetItem = await TaskProjectionLoader.LoadFleetStatusAsync(taskDirectory, TestContext.Current.CancellationToken);
            Assert.Equal("interactive session", fleetItem.TypeLabel);
            Assert.Equal("Not yet run", fleetItem.StatusText);
            Assert.Equal(0, fleetItem.PausedStepCount);
            Assert.False(fleetItem.IsArchived);

            // #322: a session (even one that never ran, so has no snapshot) takes its created/updated
            // straight from the durable in-data source, .aer/session.json -- not from filesystem times.
            var metadata = await InteractiveSessionMaterializer.LoadMetadataAsync(
                Path.Combine(taskDirectory, ".aer", "session.json"), TestContext.Current.CancellationToken);
            Assert.NotNull(metadata);
            Assert.Equal(metadata.CreatedAt, fleetItem.Created);
            Assert.Equal(metadata.UpdatedAt, fleetItem.Updated);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
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
                () => TaskProjectionLoader.LoadAsync(notATaskDirectory, TestContext.Current.CancellationToken));

            Assert.Contains(notATaskDirectory, exception.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(notATaskDirectory);
        }
    }
}
