using System.Diagnostics;
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

    private static readonly StepId A = new("a");
    private static readonly StepId B = new("b");
    private static readonly StepId C = new("c");
    private static readonly StepId D = new("d");

    private static readonly StepId Flaky = new("flaky");
    private static readonly StepId Downstream = new("downstream");
    private static readonly StepId Reviewer = new("reviewer");
    private static readonly StepId Permanent = new("permanent");

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
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "the-plan"),
                    TimeSpan.FromSeconds(30)),
                ["critic"] = new WorkerBinding.Process(
                    new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                    CopyFirstInputTo("review"),
                    TimeSpan.FromSeconds(30)),
                ["publisher"] = new WorkerBinding.Process(
                    new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                    CopyFirstInputTo("summary"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-e2e"), taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

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
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
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
                dispatcher, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_diamond_dag_workflow_loaded_from_a_fixture_file_runs_all_four_steps_to_completion()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "diamond-dag-workflow.json");
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["a"] = new WorkerBinding.Process(
                    new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                    WriteFile("out_a", "a-out"),
                    TimeSpan.FromSeconds(30)),
                ["b"] = new WorkerBinding.Process(
                    new WorkerContract("b", ["out_a"], [new ProducedOutput("out_b")], []),
                    CopyFirstInputTo("out_b"),
                    TimeSpan.FromSeconds(30)),
                ["c"] = new WorkerBinding.Process(
                    new WorkerContract("c", ["out_a"], [new ProducedOutput("out_c")], []),
                    CopyFirstInputTo("out_c"),
                    TimeSpan.FromSeconds(30)),
                ["d"] = new WorkerBinding.Process(
                    new WorkerContract("d", ["out_b", "out_c"], [new ProducedOutput("out_d")], []),
                    ConcatBothInputsTo("out_d"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-diamond"), taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(4, finalState.Steps.Count);
            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));

            var stepStateById = finalState.Steps.ToDictionary(s => s.StepId);

            // D's UpstreamExecutionIds reference B's and C's successful executions.
            Assert.Equal(stepStateById[B].LatestExecutionId, stepStateById[D].UpstreamExecutionIds[B]);
            Assert.Equal(stepStateById[C].LatestExecutionId, stepStateById[D].UpstreamExecutionIds[C]);

            await AssertOutputExistsAsync(artifactsRoot, stepStateById[A], "out_a", "a-out");
            await AssertOutputExistsAsync(artifactsRoot, stepStateById[B], "out_b", "a-out");
            await AssertOutputExistsAsync(artifactsRoot, stepStateById[C], "out_c", "a-out");
            await AssertOutputExistsAsync(artifactsRoot, stepStateById[D], "out_d");
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_mechanically_flaky_worker_retries_and_downstream_uses_the_successful_attempts_output()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "flaky-retry-workflow.json");
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        var markerFilePath = Path.Combine(taskDirectory, "flaky.marker");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["flaky"] = new WorkerBinding.Process(
                    new WorkerContract("flaky", [], [new ProducedOutput("result")], []),
                    FailOnFirstAttemptThenSucceed(markerFilePath, "result", "second-attempt-result"),
                    TimeSpan.FromSeconds(30)),
                ["downstream"] = new WorkerBinding.Process(
                    new WorkerContract("downstream", ["result"], [new ProducedOutput("final")], []),
                    CopyFirstInputTo("final"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-flaky"), taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var stepStateById = finalState.Steps.ToDictionary(s => s.StepId);
            Assert.Equal(StepStatus.Succeeded, stepStateById[Flaky].Status);
            Assert.Equal(StepStatus.Succeeded, stepStateById[Downstream].Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var flakyExecutionIds = GetAcceptedExecutionIds(events, Flaky);

            // §10's history shape: two attempts, distinct ExecutionIds, first failed then succeeded.
            Assert.Equal(2, flakyExecutionIds.Count);
            Assert.NotEqual(flakyExecutionIds[0], flakyExecutionIds[1]);
            Assert.Equal(StepStatus.Failed, GetTerminalStatus(events, flakyExecutionIds[0]));
            Assert.Equal(StepStatus.Succeeded, GetTerminalStatus(events, flakyExecutionIds[1]));

            // History is never cleaned up (§10, §16): both attempts' artifact directories persist.
            Assert.True(Directory.Exists(Path.Combine(artifactsRoot, $"execution_{flakyExecutionIds[0]}")));
            Assert.True(Directory.Exists(Path.Combine(artifactsRoot, $"execution_{flakyExecutionIds[1]}")));

            // Downstream ran against the successful attempt's output, not the failed one's.
            await AssertOutputExistsAsync(artifactsRoot, stepStateById[Downstream], "final", "second-attempt-result");
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_worker_using_bounded_self_iteration_retries_until_its_output_condition_is_satisfied()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "self-iteration-workflow.json");
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        var scriptDirectory = Path.Combine(taskDirectory, "scripts");
        var markerFilePath = Path.Combine(taskDirectory, "reviewer.marker");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["reviewer"] = new WorkerBinding.Process(
                    new WorkerContract(
                        "reviewer",
                        [],
                        [new ProducedOutput("verdict", new OutputCondition("/status", new JsonScalar.String("approved")))],
                        []),
                    WriteVerdictNeedsRevisionThenApproved(scriptDirectory, markerFilePath, "verdict"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-self-iteration"), taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var reviewerState = finalState.Steps.Single(s => s.StepId == Reviewer);
            Assert.Equal(StepStatus.Succeeded, reviewerState.Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var executionIds = GetAcceptedExecutionIds(events, Reviewer);
            Assert.Equal(2, executionIds.Count);
            Assert.Equal(StepStatus.Failed, GetTerminalStatus(events, executionIds[0]));
            Assert.Equal(StepStatus.Succeeded, GetTerminalStatus(events, executionIds[1]));

            // Exit 0 with an unsatisfied OutputCondition classifies ExecutionFailed with no
            // self-reported classification — only the condition, not the worker, drove the retry.
            var firstAttemptOutcome = events.OfType<FlowEvent.ExecutionFailed>().Single(e => e.ExecutionId == executionIds[0]);
            Assert.Null(firstAttemptOutcome.FailureClassification);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_worker_reporting_a_permanent_failure_classification_is_not_retried_despite_remaining_budget()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "permanent-failure-workflow.json");
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        var scriptDirectory = Path.Combine(taskDirectory, "scripts");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["permanent"] = new WorkerBinding.Process(
                    new WorkerContract("permanent", [], [new ProducedOutput("result")], ["result-metadata.json"]),
                    FailPermanently(scriptDirectory, "result-metadata.json"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-permanent"), taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var permanentState = finalState.Steps.Single(s => s.StepId == Permanent);
            Assert.Equal(StepStatus.Failed, permanentState.Status);
            Assert.Equal(FailureClassification.Permanent, permanentState.LatestFailureClassification);

            // Exactly one attempt despite MaxAttempts: 3 remaining — the Permanent short-circuit (§8.1).
            var executionIds = GetAcceptedExecutionIds(await reader.ReadAllAsync(TestContext.Current.CancellationToken), Permanent);
            Assert.Single(executionIds);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task An_always_failing_worker_is_retried_exactly_up_to_MaxAttempts_then_stays_terminally_failed()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "exhaustion-workflow.json");
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["flaky"] = new WorkerBinding.Process(
                    new WorkerContract("flaky", [], [new ProducedOutput("result")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30)),
                ["downstream"] = new WorkerBinding.Process(
                    new WorkerContract("downstream", ["result"], [new ProducedOutput("final")], []),
                    CopyFirstInputTo("final"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-exhaustion"), taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var stepStateById = finalState.Steps.ToDictionary(s => s.StepId);
            Assert.Equal(StepStatus.Failed, stepStateById[Flaky].Status);

            // Downstream never dispatched — the workflow reached a fixed point instead.
            Assert.Equal(StepStatus.Pending, stepStateById[Downstream].Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var executionIds = GetAcceptedExecutionIds(events, Flaky);
            Assert.Equal(2, executionIds.Count);
            Assert.All(executionIds, id => Assert.Equal(StepStatus.Failed, GetTerminalStatus(events, id)));
            Assert.Empty(GetAcceptedExecutionIds(events, Downstream));

            // Both attempts' artifact directories persist — history is never cleaned up (§10, §16).
            Assert.True(Directory.Exists(Path.Combine(artifactsRoot, $"execution_{executionIds[0]}")));
            Assert.True(Directory.Exists(Path.Combine(artifactsRoot, $"execution_{executionIds[1]}")));
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Re_reading_the_full_event_log_every_scheduling_round_stays_fast_at_realistic_scale()
    {
        // Measures §21's "manifest cache if scale demands" question directly: the M8 Phase 3
        // reactive loop re-reads the entire flow.jsonl every scheduling round rather than tailing
        // it. This measures that re-read's cost at a log size already larger than any workflow in
        // this suite reaches, to record whether a manifest cache (§12.1) is warranted yet — see
        // IMPLEMENTATION_PLAN.md's Phase 4 entry for the measured figure this bound is based on.
        var logPath = Path.Combine(Path.GetTempPath(), $"perf-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                for (var i = 0; i < 200; i++)
                {
                    var executionId = new ExecutionId($"exec-{i}");
                    var request = new ExecutionRequest(
                        executionId,
                        new WorkflowId("wf-perf"),
                        new StepId($"step-{i}"),
                        "worker",
                        [],
                        ["out"],
                        TimeSpan.FromSeconds(30),
                        [],
                        new Dictionary<StepId, ExecutionId>());

                    await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
                    await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(executionId), TestContext.Current.CancellationToken);
                }
            }

            var reader = new FlowEventLogReader(logPath);
            const int rounds = 50;
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < rounds; i++)
            {
                await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            }

            stopwatch.Stop();
            var millisecondsPerRound = stopwatch.Elapsed.TotalMilliseconds / rounds;

            Assert.True(
                millisecondsPerRound < 50,
                $"Full re-read averaged {millisecondsPerRound:F2}ms/round at 400 events, exceeding the 50ms budget.");
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    private static async Task AssertOutputExistsAsync(
        string artifactsRoot, StepState stepState, string outputName, string? expectedContent = null)
    {
        var executionId = stepState.LatestExecutionId!.Value;
        var outputPath = Path.Combine(artifactsRoot, $"execution_{executionId}", outputName);

        Assert.True(File.Exists(outputPath));

        if (expectedContent is not null)
        {
            Assert.Equal(expectedContent, (await File.ReadAllTextAsync(outputPath)).Trim());
        }
    }

    private static IReadOnlyList<ExecutionId> GetAcceptedExecutionIds(IReadOnlyList<FlowEvent> events, StepId stepId) => events
        .OfType<FlowEvent.ExecutionRequestAccepted>()
        .Where(e => e.Request.StepId == stepId)
        .Select(e => e.Request.ExecutionId)
        .ToList();

    private static StepStatus? GetTerminalStatus(IReadOnlyList<FlowEvent> events, ExecutionId executionId) => events
        .Select(flowEvent => flowEvent switch
        {
            FlowEvent.ExecutionSucceeded succeeded when succeeded.ExecutionId == executionId => StepStatus.Succeeded,
            FlowEvent.ExecutionFailed failed when failed.ExecutionId == executionId => StepStatus.Failed,
            FlowEvent.ExecutionCancelled cancelled when cancelled.ExecutionId == executionId => StepStatus.Cancelled,
            _ => (StepStatus?)null,
        })
        .FirstOrDefault(status => status is not null);
}
