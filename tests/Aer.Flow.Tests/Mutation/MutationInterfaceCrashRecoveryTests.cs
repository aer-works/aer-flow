using Aer.Flow.Artifacts;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Tests.TestSupport;

namespace Aer.Flow.Tests.Mutation;

/// <summary>
/// M10 Phase 3 (§7 full robustness): mutation-level tests proving each of the four crash states a
/// process-bound step's unfinalized latest attempt can be in is reconciled correctly by reading back
/// the Core half of the log (§6). Every fixture manufactures the crash window directly — appending
/// exactly the <see cref="LogEntry"/> lines a real crash would leave behind via
/// <see cref="FlowEventLogWriter"/>'s <see cref="IEventLogWriter"/>/<see cref="ICoreEventLogWriter"/>
/// halves — rather than actually killing a process (Phase 4's job).
/// </summary>
public class MutationInterfaceCrashRecoveryTests
{
    private static readonly StepId A = new("a");
    private static readonly StepId C = new("c");

    private static readonly WorkerContract ProcessContract = new("stub-worker", [], [], []);
    private static readonly CoreDispatchTarget Target = new("stub", []);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly CoreDispatchResult Succeeded = new(0, CoreExitReason.Natural);

    [Fact]
    public async Task StartWorkflowAsync_resubmits_an_execution_with_no_recorded_ExecutionStarted_under_the_same_ExecutionId()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));

        var (taskDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            // §7's named safe pre-spawn crash state: the intent is durable, but Core never got a
            // chance to run it (crash between accept-fsync and spawn).
            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);

            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);
            Assert.Equal(executionId, state.Steps.Single().LatestExecutionId);
            Assert.Equal(0, state.Steps.Single().ConsecutiveFailureCount);

            // The same attempt, not a retry: no new ExecutionRequestAccepted for this step.
            var events = await reader.ReadAllAsync();
            Assert.Single(events.OfType<FlowEvent.ExecutionRequestAccepted>());
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_finalizes_an_unfulfilled_cancellation_for_a_never_started_execution_and_never_dispatches_it()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: []), Step(C, dependsOn: [A]));

        var (taskDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);
            await writer.AppendAsync(new FlowEvent.CancellationRequested(executionId));

            // Nothing enqueued: if the cancel didn't win, StartWorkflowAsync would try to dispatch A
            // (or, worse, C) and StubCoreDispatcher would throw.
            var stub = new StubCoreDispatcher();

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId, taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub);

            Assert.Equal(StepStatus.Cancelled, state.Steps.Single(s => s.StepId == A).Status);
            Assert.Equal(StepStatus.Pending, state.Steps.Single(s => s.StepId == C).Status);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_classifies_a_recorded_exit_with_no_outcome_as_if_the_completion_had_just_arrived()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: []), Step(C, dependsOn: [A]));

        var (taskDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);

            // Ran to a natural, successful exit while Flow was down (§6) — Core recorded both
            // lifecycle events, but no Flow-side outcome was ever appended for them.
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242));
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural));

            var stub = new StubCoreDispatcher();
            var cResult = stub.EnqueueResult(C);

            // Nothing enqueued for A: it must be classified from the recorded exit, never dispatched.
            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub);
            Assert.Equal(C, await ReadNextDispatchAsync(stub));
            cResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single(s => s.StepId == A).Status);
            Assert.Equal(StepStatus.Succeeded, state.Steps.Single(s => s.StepId == C).Status);

            var events = await reader.ReadAllAsync();
            Assert.Single(events, e => e is FlowEvent.ExecutionSucceeded es && es.ExecutionId == executionId);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_classifies_a_recorded_nonzero_exit_as_Failed_and_retries_it()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], maxAttempts: 2));

        var (taskDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242));
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 1, CoreExitReason.Natural));

            var stub = new StubCoreDispatcher();
            var retryResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            retryResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);

            // Two attempts total: the crash-recovered classification (Failed) plus the retry (Succeeded).
            var events = await reader.ReadAllAsync();
            Assert.Equal(2, events.OfType<FlowEvent.ExecutionRequestAccepted>().Count());
            Assert.Single(events, e => e is FlowEvent.ExecutionFailed ef && ef.ExecutionId == executionId);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_finalizes_an_orphan_as_a_retryable_failed_attempt_and_retries_it()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], maxAttempts: 2));

        var (taskDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var orphanExecutionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);

            // §7's third crash state: Core recorded the start, but this pump's predecessor died
            // before an exit was ever recorded — nothing to classify against.
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(orphanExecutionId, Pid: 4242));

            var stub = new StubCoreDispatcher();
            var retryResult = stub.EnqueueResult(A);

            // Nothing enqueued for the orphan's own ExecutionId: it must never be dispatched again.
            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            retryResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);
            Assert.NotEqual(orphanExecutionId, state.Steps.Single().LatestExecutionId);

            var events = await reader.ReadAllAsync();
            var abandoned = Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
            Assert.Equal(orphanExecutionId, abandoned.ExecutionId);
            Assert.Equal(FailureClassification.Retryable, abandoned.FailureClassification);

            // §16: the orphaned attempt's own artifact directory is untouched by the retry, which
            // gets its own fresh directory under the new ExecutionId.
            Assert.True(Directory.Exists(ArtifactManager.ResolveOutputDirectory(artifactsRoot, orphanExecutionId)));
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_finalizes_an_orphan_as_terminally_failed_once_its_retry_budget_is_exhausted()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], maxAttempts: 1));

        var (taskDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var orphanExecutionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(orphanExecutionId, Pid: 4242));

            // Nothing enqueued at all: MaxAttempts: 1 forecloses the retry, so the pump must reach
            // its fixed point without dispatching anything.
            var stub = new StubCoreDispatcher();

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId, taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub);

            Assert.Equal(StepStatus.Failed, state.Steps.Single().Status);
            Assert.Equal(orphanExecutionId, state.Steps.Single().LatestExecutionId);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    private static async Task<ExecutionId> AcceptRequestAsync(
        FlowEventLogWriter writer, WorkflowId workflowId, string artifactsRoot, StepId stepId)
    {
        var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
        var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
        var request = new ExecutionRequest(
            executionId,
            workflowId,
            stepId,
            "stub-worker",
            Inputs: [],
            Outputs: [],
            Timeout,
            ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request));
        return executionId;
    }

    private static WorkflowStepDefinition Step(
        StepId stepId,
        IReadOnlyList<StepId> dependsOn,
        int maxAttempts = 1,
        string worker = "stub-worker") =>
        new(stepId, worker, [], [], dependsOn, new RetryPolicy(maxAttempts));

    private static WorkflowDefinitionSnapshot MakeSnapshot(params WorkflowStepDefinition[] steps) => new(
        new WorkflowDefinitionSnapshotId($"snapshot-{Guid.NewGuid():N}"),
        new WorkflowTemplateId("crash-recovery-test"),
        WorkflowTemplateVersion: 1,
        Steps: steps);

    private static Dictionary<string, WorkerBinding> MakeBindings() => new()
    {
        ["stub-worker"] = new WorkerBinding.Process(ProcessContract, Target, Timeout),
    };

    private static (string TaskDirectory, string ArtifactsRoot, string LogPath) MakeTaskPaths()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        return (taskDirectory, Path.Combine(taskDirectory, "artifacts"), Path.Combine(taskDirectory, "flow.jsonl"));
    }

    private static async Task<StepId> ReadNextDispatchAsync(StubCoreDispatcher stub)
    {
        var readTask = stub.DispatchStarted.ReadAsync().AsTask();
        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(readTask, completed);
        return await readTask;
    }
}
