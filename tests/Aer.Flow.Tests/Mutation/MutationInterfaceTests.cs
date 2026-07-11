using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using static Aer.Flow.Tests.TestSupport.ShellWorkerCommands;

namespace Aer.Flow.Tests.Mutation;

/// <summary>
/// Integration tests: these spawn real processes through the aer-core M5 <c>AerTask</c> binding
/// (M7 Phase 7's acceptance criteria — a three-step linear workflow runs end-to-end through
/// <see cref="MutationInterface.StartWorkflowAsync"/> and a clean exit with no output is
/// classified <c>ExecutionFailed</c>). No mocking of Aer.Core itself.
/// </summary>
public class MutationInterfaceTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");
    private static readonly StepId Publisher = new("publisher");

    [Fact]
    public async Task StartWorkflowAsync_runs_a_three_step_linear_workflow_to_completion()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1"),
                new WorkflowTemplateId("architect-critic-publisher"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
                    new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
                    new WorkflowStepDefinition(Publisher, "publisher", ["review"], ["summary"], DependsOn: [Critic], RetryPolicy: new RetryPolicy(1)),
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "architect"),
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
                new WorkflowId("wf-1"), taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher);

            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));

            var publisherExecutionId = finalState.Steps.Single(s => s.StepId == Publisher).LatestExecutionId!.Value;
            var summaryPath = Path.Combine(artifactsRoot, $"execution_{publisherExecutionId}", "summary");
            Assert.True(File.Exists(summaryPath));
            Assert.Equal("architect", (await File.ReadAllTextAsync(summaryPath)).Trim());
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_retries_a_step_that_fails_once_then_succeeds()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        var markerFilePath = Path.Combine(taskDirectory, "attempt-marker");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-3"),
                new WorkflowTemplateId("flaky-architect-critic"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2)),
                    new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    FailOnFirstAttemptThenSucceed(markerFilePath, "plan", "architect"),
                    TimeSpan.FromSeconds(30)),
                ["critic"] = new WorkerBinding.Process(
                    new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                    CopyFirstInputTo("review"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-3"), taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher);

            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));
            Assert.Equal(0, finalState.Steps.Single(s => s.StepId == Architect).ConsecutiveFailureCount);

            // §10's history shape: two distinct ExecutionIds for Architect, the first failed and
            // the second succeeded — neither event mutated or removed.
            var events = await reader.ReadAllAsync();
            var architectAttempts = events
                .OfType<FlowEvent.ExecutionRequestAccepted>()
                .Where(e => e.Request.StepId == Architect)
                .Select(e => e.Request.ExecutionId)
                .ToList();
            Assert.Equal(2, architectAttempts.Count);
            Assert.Equal(architectAttempts.Distinct().Count(), architectAttempts.Count);
            Assert.Contains(events, e => e is FlowEvent.ExecutionFailed failed && architectAttempts.Contains(failed.ExecutionId));
            Assert.Contains(events, e => e is FlowEvent.ExecutionSucceeded succeeded && architectAttempts.Contains(succeeded.ExecutionId));
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_classifies_a_clean_exit_with_no_output_as_ExecutionFailed()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        try
        {
            var stepId = new StepId("silent-step");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-2"),
                new WorkflowTemplateId("silent"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(stepId, "silent", [], ["output.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["silent"] = new WorkerBinding.Process(
                    new WorkerContract("silent", [], [new ProducedOutput("output.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-2"), taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher);

            var stepState = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, stepState.Status);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }
}
