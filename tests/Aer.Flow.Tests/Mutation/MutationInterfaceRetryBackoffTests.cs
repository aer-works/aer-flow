using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Scheduling;
using Aer.Flow.Store;
using Aer.Flow.Tests.TestSupport;
using static Aer.Flow.Tests.TestSupport.ShellWorkerCommands;

namespace Aer.Flow.Tests.Mutation;

public class MutationInterfaceRetryBackoffTests
{
    private static readonly StepId StepA = new("step-a");
    private static readonly StepId StepB = new("step-b");

    // Every fake-clock advance below happens only AFTER the event that proves the pump committed
    // to a deadline is visible in the log. Advancing on a wall-clock guess (`await Task.Delay(100)`
    // then Advance) is a race: under load the advance can land before the first attempt has even
    // failed, the deferral deadline then lands after the already-spent advance, and the pump waits
    // on a fake instant nobody will ever reach — the test hangs rather than fails. The poll below
    // is the positive signal; the WaitAsync timeouts on the pump awaits are the backstop that turns
    // any future reintroduction of the race into a red test instead of a hung suite.
    private static async Task<T> WaitForEventAsync<T>(FlowEventLogReader reader, Task pumpTask, CancellationToken cancellationToken)
        where T : FlowEvent
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var events = await reader.ReadAllAsync(cancellationToken);
            if (events.OfType<T>().FirstOrDefault() is { } found)
            {
                return found;
            }

            if (pumpTask.IsCompleted)
            {
                await pumpTask; // surfaces the pump's own exception if it faulted
                Assert.Fail($"Pump completed without appending {typeof(T).Name}.");
            }

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Timed out waiting for {typeof(T).Name}.");
            await Task.Delay(10, cancellationToken);
        }
    }

    private static readonly TimeSpan PumpCompletionTimeout = TimeSpan.FromSeconds(30);

    // A single Advance is not enough to release the pump, even after the deferral event is visible:
    // the pump reads the clock and then creates its relative Task.Delay in two steps, so an advance
    // landing in that gap starts the timer from the already-advanced clock — due at deadline +
    // delay, an instant nothing will ever advance to. Harmless under a real clock (time keeps
    // moving and the pump re-checks readiness on every wake, so it just wakes late); a strand only
    // a fake clock can produce. Advancing repeatedly until the pump returns guarantees some advance
    // lands after the timer exists. Overshooting the deadline is safe — readiness is `now >=
    // notBefore`, never an exact-instant match.
    private static async Task<FlowState> AdvanceUntilPumpCompletesAsync(
        FakeTimeProvider fakeTime, Task<FlowState> pumpTask, TimeSpan step)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!pumpTask.IsCompleted)
        {
            Assert.True(
                stopwatch.Elapsed < PumpCompletionTimeout,
                "Pump did not complete while the clock kept advancing past every deferral deadline.");
            fakeTime.Advance(step);
            await Task.Delay(10);
        }

        return await pumpTask;
    }

    // 1. Fails on a zero-delay retry (Test 1 from §6)
    // Mutation control note: Zeroing the delay in GetRetryObligations causes test 1 to fail (dispatch occurs at t+0) while test 2 remains green.
    [Fact]
    public async Task Test1_Fails_on_zero_delay_retry_steady_backoff_defers_execution()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        var markerPath = Path.Combine(taskDirectory, "attempt-marker");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-1"),
                new WorkflowTemplateId("template-retry-1"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        StepA,
                        "worker-a",
                        Inputs: [],
                        Outputs: ["out.txt"],
                        DependsOn: [],
                        RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    FailOnFirstAttemptThenSucceed(markerPath, "out.txt", "content"),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            // Run pump in background
            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-1"),
                taskDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0, // floor sample: Steady initial 1s * 0.5 = 500ms
                cancellationToken: TestContext.Current.CancellationToken);

            // Positive signal that attempt 1 failed and the pump committed to a deadline.
            var retryEvent = await WaitForEventAsync<FlowEvent.StepRetryScheduled>(reader, pumpTask, TestContext.Current.CancellationToken);

            Assert.True(retryEvent.RetryDelayMs >= 500, $"Expected DelayMs >= 500, got {retryEvent.RetryDelayMs}");

            // No second attempt at t+0. This is the assertion the mutation control keys on: with the
            // delay zeroed, attempt 2 dispatches in real time before any advance, and this reads 2.
            var eventsMid = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(eventsMid.OfType<FlowEvent.ExecutionRequestAccepted>());

            // Advance to t + DelayMs - 1ms: still no second attempt. Best-effort as a negative (the
            // grace period can only catch a dispatch that happens promptly); the exact boundary
            // semantics are pinned deterministically by DependencyResolverTests' clamp tests.
            fakeTime.Advance(TimeSpan.FromMilliseconds(retryEvent.RetryDelayMs - 1));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            var eventsBefore = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(eventsBefore.OfType<FlowEvent.ExecutionRequestAccepted>());

            // Advance to t + DelayMs and beyond: second attempt dispatches and succeeds
            var finalState = await AdvanceUntilPumpCompletesAsync(
                fakeTime, pumpTask, TimeSpan.FromMilliseconds(retryEvent.RetryDelayMs));

            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == StepA).Status);

            var eventsFinal = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var acceptedFinal = eventsFinal.OfType<FlowEvent.ExecutionRequestAccepted>().ToList();
            Assert.Equal(2, acceptedFinal.Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    // 2. Polarity (Test 2 from §6)
    [Fact]
    public async Task Test2_Backoff_none_dispatches_retry_immediately_at_t0()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        var markerPath = Path.Combine(taskDirectory, "attempt-marker");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-2"),
                new WorkflowTemplateId("template-retry-2"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        StepA,
                        "worker-a",
                        Inputs: [],
                        Outputs: ["out.txt"],
                        DependsOn: [],
                        RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.None))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    FailOnFirstAttemptThenSucceed(markerPath, "out.txt", "content"),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-2"),
                taskDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == StepA).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var retryEvent = events.OfType<FlowEvent.StepRetryScheduled>().Single();
            Assert.Equal(0, retryEvent.RetryDelayMs);

            var accepted = events.OfType<FlowEvent.ExecutionRequestAccepted>().ToList();
            Assert.Equal(2, accepted.Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    // 4. Replay determinism, falsifiable (Test 4 from §6)
    [Fact]
    public void Test4_Replay_determinism_under_throwing_time_provider_and_jitter_source()
    {
        var execId1 = new ExecutionId("exec-1");
        var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        var notBefore = now.AddMilliseconds(500);

        var events = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(execId1, new WorkflowId("wf-4"), StepA, "worker-a", [], ["out.txt"], null, [], new Dictionary<StepId, ExecutionId>())),
            new FlowEvent.ExecutionFailed(execId1, FailureClassification.Retryable, "Transient error"),
            new FlowEvent.StepRetryScheduled(StepA, execId1, notBefore, 500)
        };

        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snapshot-retry-4"),
            new WorkflowTemplateId("template-retry-4"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))
            ]);

        // StateProjector.Project is pure and does not consult any time provider or jitter source
        var state = StateProjector.Project(events, snapshot);

        var stepState = Assert.Single(state.Steps);
        Assert.Equal(StepStatus.Failed, stepState.Status);
        Assert.Equal(notBefore, stepState.RetryNotBefore);
        Assert.Equal(500, stepState.RetryDelayMs);
        Assert.Equal(execId1, stepState.RetryScheduledForExecutionId);
        Assert.Equal(WorkflowStatus.Running, state.Status);
    }

    // 7. Abandoned-crash corner (Test 7 from §6)
    [Fact]
    public async Task Test7_Abandoned_crash_recovery_execution_failed_gets_retry_scheduled()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var execId = new ExecutionId("abandoned-exec-1");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-7"),
                new WorkflowTemplateId("template-retry-7"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            // Simulate a crash after the process spawned: an ExecutionRequestAccepted plus the
            // Core half's ExecutionStarted, with no ExecutionExited. Both live in the one
            // flow.jsonl (§5.1) — Core events are LogEntry-wrapped lines in the same file, so they
            // go through the writer's own CoreEvent overload, never a hand-built sidecar file.
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var request = new ExecutionRequest(execId, new WorkflowId("wf-7"), StepA, "worker-a", [], ["out.txt"], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>());
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
                await writerInit.AppendAsync(new CoreEvent.ExecutionStarted(execId, Pid: 12345), TestContext.Current.CancellationToken);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-7"),
                taskDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            // StepRetryScheduled is appended after the abandonment's ExecutionFailed, so its
            // presence proves both halves of the recovery happened.
            var retryEvent = await WaitForEventAsync<FlowEvent.StepRetryScheduled>(reader, pumpTask, TestContext.Current.CancellationToken);
            Assert.Equal(execId, retryEvent.ForExecutionId);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.ExecutionFailed f && f.ExecutionId == execId && f.Reason!.Contains("Abandoned"));

            await AdvanceUntilPumpCompletesAsync(fakeTime, pumpTask, TimeSpan.FromSeconds(10));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    // 8a. Polarity floor for append-exactly-once: at MaxAttempts = 1 MayRetry is never true, so no
    // StepRetryScheduled may appear at all — and the pump must reach terminal without deferring.
    [Fact]
    public async Task Test8_No_StepRetryScheduled_when_retry_budget_is_exhausted_on_first_failure()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-8"),
                new WorkflowTemplateId("template-retry-8"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 1, Backoff: BackoffPolicy.None))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-8"),
                taskDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            // The await itself carries half the claim: fakeTime never advances, so if a deferral
            // were wrongly scheduled for the budget-exhausted step, the pump would never return.
            Assert.Equal(StepStatus.Failed, finalState.Steps.Single(s => s.StepId == StepA).Status);
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.StepRetryScheduled>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    [Fact]
    public async Task Test8_StepRetryScheduled_appended_exactly_once_per_failed_attempt()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-8b"),
                new WorkflowTemplateId("template-retry-8b"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.None))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    ExitCleanlyWithoutWriting(), // Always fails
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-8b"),
                taskDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var retryEvents = events.OfType<FlowEvent.StepRetryScheduled>().ToList();

            // Attempt 1 fails -> 1 StepRetryScheduled. Attempt 2 fails -> MaxAttempts 2 reached, no more retries.
            Assert.Single(retryEvents);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    // 9. Operator RetryWithRevision is not deferred (Test 9 from §6)
    [Fact]
    public async Task Test9_Operator_RetryWithRevision_dispatches_immediately_clearing_deadline()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        var markerPath = Path.Combine(taskDirectory, "attempt-marker");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-9"),
                new WorkflowTemplateId("template-retry-9"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        StepA,
                        "worker-a",
                        Inputs: [],
                        Outputs: ["out.txt"],
                        DependsOn: [],
                        RetryPolicy: new RetryPolicy(MaxAttempts: 1, Backoff: BackoffPolicy.Patient),
                        PausePoint: new PausePoint([]))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    FailOnFirstAttemptThenSucceed(markerPath, "out.txt", "content"),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            // Attempt 1 runs and fails, pause point triggers WorkflowPaused
            var pausedState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-9"),
                taskDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Paused, pausedState.Status);

            var failedExecId = pausedState.Steps.Single(s => s.StepId == StepA).LatestExecutionId!.Value;

            // Operator issues RetryWithRevision
            var finalState = await MutationInterface.RecordDecisionAsync(
                new WorkflowId("wf-9"),
                taskDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                referencedExecutionId: failedExecId,
                decisionType: DecisionType.RetryWithRevision,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            // The await above returning is itself the claim: fakeTime never advances, so if the
            // operator's retry had been machine-deferred (Patient's initial delay is minutes), the
            // pump would still be waiting. The step lands Paused again rather than Succeeded — its
            // PausePoint pauses after every outcome, success included, same shape as Test11's
            // StepB — so the immediacy reads off the log: a second accepted execution, its
            // success, and no StepRetryScheduled ever appended for the operator-initiated attempt.
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, events.OfType<FlowEvent.ExecutionRequestAccepted>().Count());
            Assert.Single(events.OfType<FlowEvent.ExecutionSucceeded>());
            Assert.Empty(events.OfType<FlowEvent.StepRetryScheduled>());

            var stepState = finalState.Steps.Single(s => s.StepId == StepA);
            Assert.NotEqual(failedExecId, stepState.LatestExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    // 10. Not Terminal while deferred (Test 10 from §6)
    [Fact]
    public async Task Test10_WorkflowStatus_remains_Running_while_step_is_deferred()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-10"),
                new WorkflowTemplateId("template-retry-10"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Patient))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-10"),
                taskDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            var retryEvent = await WaitForEventAsync<FlowEvent.StepRetryScheduled>(reader, pumpTask, TestContext.Current.CancellationToken);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var projectedState = StateProjector.Project(events, snapshot);

            Assert.Equal(WorkflowStatus.Running, projectedState.Status);

            // Step by the recorded delay rather than a guessed 20 minutes — the event is the
            // authority on how long Patient actually deferred.
            var finalState = await AdvanceUntilPumpCompletesAsync(
                fakeTime, pumpTask, TimeSpan.FromMilliseconds(retryEvent.RetryDelayMs));
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    // 11. Paused sibling keeps aer decide reachable (Test 11 from §6)
    [Fact]
    public async Task Test11_Paused_sibling_keeps_aer_decide_reachable_pump_returns_paused()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-11"),
                new WorkflowTemplateId("template-retry-11"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["outA.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Patient)),
                    new WorkflowStepDefinition(StepB, "worker-b", [], ["outB.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 1), PausePoint: new PausePoint([]))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("outA.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30)),
                ["worker-b"] = new WorkerBinding.Process(
                    new WorkerContract("worker-b", [], [new ProducedOutput("outB.txt")], []),
                    WriteFile("outB.txt", "contentB"),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            // StepA fails and defers; StepB succeeds and pauses.
            // Pump should return WorkflowStatus.Paused immediately without blocking on StepA's deferral wait.
            var state = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-11"),
                taskDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Paused, state.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    // 13. An expired deferral whose step is blocked on a terminally failed dependency is a fixed
    // point. Before the future-deadline filter in the idle wait, this state was a zero-delay spin:
    // nothing ready (the dependency is not Succeeded), nothing in flight, and a deadline in the
    // past producing delay <= 0 -> continue -> re-project -> repeat, forever, at full CPU.
    [Fact]
    public async Task Test13_Expired_deferral_blocked_on_failed_dependency_is_a_fixed_point_not_a_spin()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-13"),
                new WorkflowTemplateId("template-retry-13"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["outA.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 1)),
                    new WorkflowStepDefinition(StepB, "worker-b", [], ["outB.txt"], DependsOn: [StepA], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("outA.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30)),
                ["worker-b"] = new WorkerBinding.Process(
                    new WorkerContract("worker-b", [], [new ProducedOutput("outB.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            // The stranded shape, written as history: A succeeded, B failed and was deferred, then
            // A reran (a supersede consequence) and failed permanently -- all before this pump
            // starts, with B's deadline already in the past.
            var aFirst = new ExecutionId("a-1");
            var bAttempt = new ExecutionId("b-1");
            var aRerun = new ExecutionId("a-2");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(aFirst, new WorkflowId("wf-13"), StepA, "worker-a", [], ["outA.txt"], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionSucceeded(aFirst), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(bAttempt, new WorkflowId("wf-13"), StepB, "worker-b", [], ["outB.txt"], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId> { [StepA] = aFirst })), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(bAttempt, FailureClassification.Retryable, "boom"), ct);
                await writerInit.AppendAsync(new FlowEvent.StepRetryScheduled(
                    StepB, bAttempt, fakeTime.GetUtcNow().AddSeconds(-10), 500), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(aRerun, new WorkflowId("wf-13"), StepA, "worker-a", [], ["outA.txt"], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(aRerun, FailureClassification.Permanent, "dead"), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            // Nothing ever advances fakeTime: the pump must return on its own, promptly.
            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-13"),
                taskDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken)
                .WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Failed, finalState.Steps.Single(s => s.StepId == StepA).Status);
            Assert.Equal(StepStatus.Failed, finalState.Steps.Single(s => s.StepId == StepB).Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    // 12. Host stop during a deferral wait (Test 12 from §6)
    [Fact]
    public async Task Test12_Host_stop_during_deferral_wait_returns_promptly()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
        using var cts = new CancellationTokenSource();

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-12"),
                new WorkflowTemplateId("template-retry-12"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Patient))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-12"),
                taskDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: cts.Token);

            // Wait until the deferral is committed, so the stop provably lands during (or after
            // entering) the wait rather than before the first attempt even ran.
            await WaitForEventAsync<FlowEvent.StepRetryScheduled>(reader, pumpTask, TestContext.Current.CancellationToken);

            // Signal host stop while pump is waiting on the Patient deferral
            cts.Cancel();

            var finalState = await pumpTask.WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);
            Assert.NotNull(finalState);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }
}
