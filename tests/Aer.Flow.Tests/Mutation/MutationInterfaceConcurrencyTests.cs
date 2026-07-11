using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Tests.TestSupport;

namespace Aer.Flow.Tests.Mutation;

/// <summary>
/// M8 Phase 3 (reactive concurrent dispatch): mutation-level tests against a
/// <see cref="StubCoreDispatcher"/> with <see cref="TaskCompletionSource{TResult}"/>-controlled
/// completion order, proving <see cref="MutationInterface.StartWorkflowAsync"/> dispatches every
/// ready step of a round concurrently and reacts to each completion independently — no real
/// processes, no timing-based assertions beyond a bounded "nothing else happened yet" check.
/// </summary>
public class MutationInterfaceConcurrencyTests
{
    private static readonly StepId A = new("a");
    private static readonly StepId B = new("b");
    private static readonly StepId C = new("c");
    private static readonly StepId D = new("d");
    private static readonly StepId F = new("f");

    private static readonly WorkerContract Contract = new("stub-worker", [], [], []);
    private static readonly CoreDispatchTarget Target = new("stub", []);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NoFurtherDispatchWindow = TimeSpan.FromMilliseconds(300);

    private static readonly CoreDispatchResult Succeeded = new(0, CoreExitReason.Natural);
    private static readonly CoreDispatchResult Failed = new(1, CoreExitReason.Natural);

    [Fact]
    public async Task StartWorkflowAsync_dispatches_ready_steps_concurrently_and_reacts_per_completion()
    {
        // A -> B, C (fan-out); D depends only on B; F is a true join on both B and C.
        var snapshot = MakeSnapshot(
            Step(A, dependsOn: []),
            Step(B, dependsOn: [A]),
            Step(C, dependsOn: [A]),
            Step(D, dependsOn: [B]),
            Step(F, dependsOn: [B, C]));

        var (taskDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);
            var bResult = stub.EnqueueResult(B);
            var cResult = stub.EnqueueResult(C);
            var dResult = stub.EnqueueResult(D);
            var fResult = stub.EnqueueResult(F);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();

            var workflowTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf"), taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub);

            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);

            // A's completion makes both B and C ready in the same round — both must dispatch before
            // either is allowed to finish, i.e. neither B's nor C's downstream may appear yet.
            var firstOfBC = await ReadNextDispatchAsync(stub);
            var secondOfBC = await ReadNextDispatchAsync(stub);
            Assert.Equal(new HashSet<StepId> { B, C }, new HashSet<StepId> { firstOfBC, secondOfBC });
            await AssertNoFurtherDispatchAsync(stub);

            // Completing B alone must dispatch D (depends only on B) without waiting for C — a slow
            // step must never delay unrelated ready work.
            bResult.SetResult(Succeeded);
            Assert.Equal(D, await ReadNextDispatchAsync(stub));
            await AssertNoFurtherDispatchAsync(stub);

            // F needs both B and C; only completing C now unblocks it.
            cResult.SetResult(Succeeded);
            Assert.Equal(F, await ReadNextDispatchAsync(stub));

            dResult.SetResult(Succeeded);
            fResult.SetResult(Succeeded);

            var finalState = await workflowTask;
            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_retries_a_failed_step_while_an_unrelated_step_stays_in_flight()
    {
        // Two independent roots: Slow never completes until the test says so; Flaky fails once then
        // succeeds. Slow being in flight must never block Flaky's retry from dispatching.
        var slow = new StepId("slow");
        var flaky = new StepId("flaky");
        var snapshot = MakeSnapshot(
            Step(slow, dependsOn: [], maxAttempts: 1),
            Step(flaky, dependsOn: [], maxAttempts: 2));

        var (taskDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            var slowResult = stub.EnqueueResult(slow); // left pending deliberately
            var flakyAttempt1 = stub.EnqueueResult(flaky); // fails
            var flakyAttempt2 = stub.EnqueueResult(flaky); // succeeds

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();

            var workflowTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf"), taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub);

            var firstRound = new[] { await ReadNextDispatchAsync(stub), await ReadNextDispatchAsync(stub) };
            Assert.Equal(new HashSet<StepId> { slow, flaky }, new HashSet<StepId>(firstRound));

            flakyAttempt1.SetResult(Failed);
            Assert.Equal(flaky, await ReadNextDispatchAsync(stub));
            await AssertNoFurtherDispatchAsync(stub);

            flakyAttempt2.SetResult(Succeeded);
            await AssertNoFurtherDispatchAsync(stub);

            slowResult.SetResult(Succeeded);

            var finalState = await workflowTask;
            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));
            Assert.Equal(0, finalState.Steps.Single(s => s.StepId == flaky).ConsecutiveFailureCount);

            var events = await reader.ReadAllAsync();
            var flakyAttempts = events
                .OfType<FlowEvent.ExecutionRequestAccepted>()
                .Where(e => e.Request.StepId == flaky)
                .Select(e => e.Request.ExecutionId)
                .ToList();
            Assert.Equal(2, flakyAttempts.Count);
            Assert.Equal(flakyAttempts.Distinct().Count(), flakyAttempts.Count);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_emits_a_rounds_accepted_events_in_snapshot_declaration_order()
    {
        // Three independent roots declared out of alphabetical/natural order, to distinguish
        // "declaration order" from any other incidental ordering the ready set might produce.
        var third = new StepId("third");
        var first = new StepId("first");
        var second = new StepId("second");
        var snapshot = MakeSnapshot(
            Step(third, dependsOn: []),
            Step(first, dependsOn: []),
            Step(second, dependsOn: []));

        var (taskDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            var thirdResult = stub.EnqueueResult(third);
            var firstResult = stub.EnqueueResult(first);
            var secondResult = stub.EnqueueResult(second);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();

            var workflowTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf"), taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub);

            await ReadNextDispatchAsync(stub);
            await ReadNextDispatchAsync(stub);
            await ReadNextDispatchAsync(stub);
            thirdResult.SetResult(Succeeded);
            firstResult.SetResult(Succeeded);
            secondResult.SetResult(Succeeded);

            await workflowTask;

            var events = await reader.ReadAllAsync();
            var acceptedOrder = events
                .OfType<FlowEvent.ExecutionRequestAccepted>()
                .Select(e => e.Request.StepId)
                .ToList();

            Assert.Equal([third, first, second], acceptedOrder);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    private static WorkflowStepDefinition Step(StepId stepId, IReadOnlyList<StepId> dependsOn, int maxAttempts = 1) =>
        new(stepId, "stub-worker", [], [], dependsOn, new RetryPolicy(maxAttempts));

    private static WorkflowDefinitionSnapshot MakeSnapshot(params WorkflowStepDefinition[] steps) => new(
        new WorkflowDefinitionSnapshotId($"snapshot-{Guid.NewGuid():N}"),
        new WorkflowTemplateId("concurrency-test"),
        WorkflowTemplateVersion: 1,
        Steps: steps);

    private static Dictionary<string, WorkerBinding> MakeBindings() =>
        new() { ["stub-worker"] = new WorkerBinding(Contract, Target, Timeout) };

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

    private static async Task AssertNoFurtherDispatchAsync(StubCoreDispatcher stub)
    {
        var waitTask = stub.DispatchStarted.WaitToReadAsync().AsTask();
        var completed = await Task.WhenAny(waitTask, Task.Delay(NoFurtherDispatchWindow));
        Assert.NotSame(waitTask, completed);
    }
}
