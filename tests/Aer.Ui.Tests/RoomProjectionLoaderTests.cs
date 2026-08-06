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
/// through <see cref="RoomProjectionLoader"/> — never by constructing a <see cref="FlowState"/> by
/// hand.
/// </summary>
public class RoomProjectionLoaderTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");
    private static readonly StepId Publisher = new("publisher");

    [Fact]
    public async Task Loads_a_bound_snapshot_and_projects_state_from_a_real_task_directory()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-task-{Guid.NewGuid():N}");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

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

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                var reader = new FlowEventLogReader(logPath);
                var dispatcher = new CoreDispatcher(writer);

                await MutationInterface.StartWorkflowAsync(
                    new WorkflowId("wf-ui-e2e"),
                    roomDirectory,
                    snapshot,
                    bindings,
                    Path.Combine(roomDirectory, "artifacts"),
                    reader,
                    writer,
                    dispatcher,
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            var projection = await RoomProjectionLoader.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

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
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task LoadFleetStatusAsync_ReportsStatusAndArchivedStateWithoutRequiringLineageProjection()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-fleet-{Guid.NewGuid():N}");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

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

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                var reader = new FlowEventLogReader(logPath);
                var dispatcher = new CoreDispatcher(writer);
                await MutationInterface.StartWorkflowAsync(
                    new WorkflowId("wf-ui-fleet"), roomDirectory, snapshot, bindings,
                    Path.Combine(roomDirectory, "artifacts"), reader, writer, dispatcher,
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            var fleetItem = await RoomProjectionLoader.LoadFleetStatusAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.Equal(Path.GetFileName(roomDirectory), fleetItem.FriendlyName);
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
                new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(roomDirectory, "snapshot.json"))),
                fleetItem.Created);
            Assert.Equal(
                new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(roomDirectory, "flow.jsonl"))),
                fleetItem.Updated);

            await RoomLifecycle.ArchiveAsync(roomDirectory, TestContext.Current.CancellationToken);
            var archivedItem = await RoomProjectionLoader.LoadFleetStatusAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.True(archivedItem.IsArchived);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task LoadFleetStatusAsync_ForASessionNeverRun_ReportsNotYetRunInsteadOfThrowing()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-fleet-session-{Guid.NewGuid():N}");
        try
        {
            await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                "sess-fleet", roomDirectory, "claude", cancellationToken: TestContext.Current.CancellationToken);

            var fleetItem = await RoomProjectionLoader.LoadFleetStatusAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.Equal("interactive session", fleetItem.TypeLabel);
            Assert.Equal("Not yet run", fleetItem.StatusText);
            Assert.Equal(0, fleetItem.PausedStepCount);
            Assert.False(fleetItem.IsArchived);

            // #322: a session (even one that never ran, so has no snapshot) takes its created/updated
            // straight from the durable in-data source, .aer/room.json -- not from filesystem times.
            var metadata = await InteractiveSessionMaterializer.LoadMetadataAsync(
                Path.Combine(roomDirectory, ".aer", "room.json"), TestContext.Current.CancellationToken);
            Assert.NotNull(metadata);
            Assert.Equal(metadata.CreatedAt, fleetItem.Created);
            Assert.Equal(metadata.UpdatedAt, fleetItem.Updated);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task LoadFleetStatusAsync_ForAWorkflowRoom_LabelsWorkflowFromRoomKindMarker()
    {
        // Polarity partner to the interactive case above (#443): a workflow room writes .aer/room.json
        // with Kind=Workflow at materialization, and the fleet label must read that marker as
        // "workflow", never "interactive session". Together the two tests pin RoomProjectionLoader's
        // kind discrimination on room.json in both directions, from the marker itself rather than from
        // a file's mere presence.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-fleet-workflow-{Guid.NewGuid():N}");
        try
        {
            await BuiltInWorkflowTemplates.MaterializeToDirectoryAsync(
                "solo-run", "claude", null, roomDirectory, "a prompt",
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(
                RoomKind.Workflow,
                await InteractiveSessionMaterializer.ReadRoomKindAsync(roomDirectory, TestContext.Current.CancellationToken));

            var fleetItem = await RoomProjectionLoader.LoadFleetStatusAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.NotEqual("interactive session", fleetItem.TypeLabel);
            Assert.Equal("workflow", fleetItem.TypeLabel);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_directory_with_no_snapshot_is_reported_as_not_a_task_directory()
    {
        var notATaskDirectory = Path.Combine(Path.GetTempPath(), $"ui-not-a-task-{Guid.NewGuid():N}");
        Directory.CreateDirectory(notATaskDirectory);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidRoomDirectoryException>(
                () => RoomProjectionLoader.LoadAsync(notATaskDirectory, TestContext.Current.CancellationToken));

            Assert.Contains(notATaskDirectory, exception.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(notATaskDirectory);
        }
    }

    [Fact]
    public async Task LoadFleetStatusAsync_TaskWithJournalEvents_ReportsNewestEventTimestamp()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-lastact-{Guid.NewGuid():N}");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(new ExecutionId("exec-1")), TestContext.Current.CancellationToken);
            }

            var fleetItem = await RoomProjectionLoader.LoadFleetStatusAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.NotNull(fleetItem.LastActivityAt);
            Assert.True(fleetItem.LastActivityAt >= fleetItem.Created);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task LoadFleetStatusAsync_EmptyNoJournalTask_FallsBackToDurableCreatedAt()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-lastact-empty-{Guid.NewGuid():N}");
        try
        {
            await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                "sess-empty", roomDirectory, "claude", cancellationToken: TestContext.Current.CancellationToken);

            var fleetItem = await RoomProjectionLoader.LoadFleetStatusAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.NotNull(fleetItem.LastActivityAt);
            Assert.Equal(fleetItem.Created, fleetItem.LastActivityAt);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task LoadFleetStatusAsync_Polarity_AppendingNewEventReordersTaskToTop()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var dirA = Path.Combine(Path.GetTempPath(), $"ui-lastact-polarity-a-{Guid.NewGuid():N}");
        var dirB = Path.Combine(Path.GetTempPath(), $"ui-lastact-polarity-b-{Guid.NewGuid():N}");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(dirA, "snapshot.json"), TestContext.Current.CancellationToken);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(dirB, "snapshot.json"), TestContext.Current.CancellationToken);

            var logPathA = Path.Combine(dirA, "flow.jsonl");
            var logPathB = Path.Combine(dirB, "flow.jsonl");

            await using (var writerA = new FlowEventLogWriter(logPathA))
            {
                await writerA.AppendAsync(new FlowEvent.ExecutionSucceeded(new ExecutionId("exec-a1")), TestContext.Current.CancellationToken);
            }

            // Write B after A so B has a newer timestamp
            await using (var writerB = new FlowEventLogWriter(logPathB))
            {
                await writerB.AppendAsync(new FlowEvent.ExecutionSucceeded(new ExecutionId("exec-b1")), TestContext.Current.CancellationToken);
            }

            var itemA = await RoomProjectionLoader.LoadFleetStatusAsync(dirA, TestContext.Current.CancellationToken);
            var itemB = await RoomProjectionLoader.LoadFleetStatusAsync(dirB, TestContext.Current.CancellationToken);

            Assert.NotNull(itemA.LastActivityAt);
            Assert.NotNull(itemB.LastActivityAt);

            // Now append a new event to task A's journal
            await using (var writerA = new FlowEventLogWriter(logPathA))
            {
                await writerA.AppendAsync(new FlowEvent.ExecutionSucceeded(new ExecutionId("exec-a2")), TestContext.Current.CancellationToken);
            }

            var itemAUpdated = await RoomProjectionLoader.LoadFleetStatusAsync(dirA, TestContext.Current.CancellationToken);
            Assert.True(itemAUpdated.LastActivityAt > itemA.LastActivityAt);
            Assert.True(itemAUpdated.LastActivityAt >= itemB.LastActivityAt);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(dirA);
            DirectoryCleanup.DeleteRecursively(dirB);
        }
    }
}
