using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;

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
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
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
                ["architect"] = new WorkerBinding(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "architect"),
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
                new WorkflowId("wf-1"), snapshot, bindings, artifactsRoot, reader, writer, dispatcher);

            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));

            var publisherExecutionId = finalState.Steps.Single(s => s.StepId == Publisher).LatestExecutionId!.Value;
            var summaryPath = Path.Combine(artifactsRoot, $"execution_{publisherExecutionId}", "summary");
            Assert.True(File.Exists(summaryPath));
            Assert.Equal("architect", (await File.ReadAllTextAsync(summaryPath)).Trim());
        }
        finally
        {
            Directory.Delete(artifactsRoot, recursive: true);
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_classifies_a_clean_exit_with_no_output_as_ExecutionFailed()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
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
                ["silent"] = new WorkerBinding(
                    new WorkerContract("silent", [], [new ProducedOutput("output.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-2"), snapshot, bindings, artifactsRoot, reader, writer, dispatcher);

            var stepState = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, stepState.Status);
        }
        finally
        {
            Directory.Delete(artifactsRoot, recursive: true);
            File.Delete(logPath);
        }
    }

    private static CoreDispatchTarget WriteFile(string outputName, string content) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", $"echo {content}>%AER_OUTPUT_DIR%\\{outputName}"])
        : new CoreDispatchTarget("sh", ["-c", $"echo {content} > \"$AER_OUTPUT_DIR/{outputName}\""]);

    private static CoreDispatchTarget CopyFirstInputTo(string outputName) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", $"type %AER_INPUT_0% >%AER_OUTPUT_DIR%\\{outputName}"])
        : new CoreDispatchTarget("sh", ["-c", $"cat \"$AER_INPUT_0\" > \"$AER_OUTPUT_DIR/{outputName}\""]);

    private static CoreDispatchTarget ExitCleanlyWithoutWriting() => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", "exit 0"])
        : new CoreDispatchTarget("sh", ["-c", "exit 0"]);
}
