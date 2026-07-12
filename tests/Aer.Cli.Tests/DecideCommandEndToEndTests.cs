using System.Text.Json;
using Aer.Adapters;
using Aer.Cli.Tests.TestSupport;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Templates;

namespace Aer.Cli.Tests;

/// <summary>
/// M12 Phase 3's completion gate for <c>aer decide</c> (issue #97): pause → each of §17.2's four
/// decision types → fixed point, driven through the real <see cref="DecideCommand.ExecuteAsync"/>
/// entry point — the exact call <c>Program.cs</c> makes — mirroring
/// <see cref="RunCommandEndToEndTests"/>'s discipline of never mocking <c>Aer.Core</c> itself.
/// Decision semantics stay proven at the <c>MutationInterface</c> layer
/// (<c>PauseDecisionSupersedeHumanEndToEndTests</c>, M9); this only proves the CLI reaches it.
/// </summary>
public class DecideCommandEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task An_approval_gate_pauses_A_then_aer_decide_Resume_runs_B_to_the_fixed_point()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-decide-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);

            var pausedResult = await RunCommand.ExecuteAsync(runOptions, Adapters);
            Assert.Equal(WorkflowStatus.Paused, pausedResult.State.Status);
            var pausedExecutionId = pausedResult.State.Steps.Single(s => s.StepId.Value == "a").LatestExecutionId!.Value;

            var decideOptions = new DecideOptions(
                taskDirectory, pausedExecutionId.Value, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);

            var finalResult = await DecideCommand.ExecuteAsync(decideOptions, Adapters);

            Assert.Equal(WorkflowStatus.Terminal, finalResult.State.Status);
            Assert.All(finalResult.State.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Reject_on_a_successful_outcome_projects_A_terminally_failed_and_B_never_dispatches()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-decide-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);

            var pausedResult = await RunCommand.ExecuteAsync(runOptions, Adapters);
            var pausedExecutionId = pausedResult.State.Steps.Single(s => s.StepId.Value == "a").LatestExecutionId!.Value;

            var decideOptions = new DecideOptions(
                taskDirectory, pausedExecutionId.Value, DecisionType.Reject, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);

            var finalResult = await DecideCommand.ExecuteAsync(decideOptions, Adapters);

            Assert.Equal(WorkflowStatus.Terminal, finalResult.State.Status);
            Assert.Equal(StepStatus.Rejected, finalResult.State.Steps.Single(s => s.StepId.Value == "a").Status);
            Assert.Equal(StepStatus.Pending, finalResult.State.Steps.Single(s => s.StepId.Value == "b").Status);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Exhaustion_then_aer_supply_then_aer_decide_RetryWithRevision_succeeds_and_downstream_runs()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-decide-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteRetryWithRevisionWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteRetryWithRevisionBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);

            var pausedResult = await RunCommand.ExecuteAsync(runOptions, Adapters);
            Assert.Equal(WorkflowStatus.Paused, pausedResult.State.Status);
            var flakyPausedState = pausedResult.State.Steps.Single(s => s.StepId.Value == "flaky");
            Assert.Equal(StepStatus.Failed, flakyPausedState.PausedOutcome);
            var pausedExecutionId = flakyPausedState.LatestExecutionId!.Value;

            var revisionFilePath = Path.Combine(testRoot, "revised.md");
            await File.WriteAllTextAsync(revisionFilePath, "revised-result");
            var supplyOptions = new SupplyOptions(taskDirectory, "human", "revision", revisionFilePath, bindingsFilePath);
            var supplyResult = await SupplyCommand.ExecuteAsync(supplyOptions, Adapters);
            Assert.Empty(supplyResult.Command.State.StepLessExecutions);

            var decideOptions = new DecideOptions(
                taskDirectory, pausedExecutionId.Value, DecisionType.RetryWithRevision, TargetStepId: null,
                supplyResult.ExecutionId.Value, bindingsFilePath);
            var retriedResult = await DecideCommand.ExecuteAsync(decideOptions, Adapters);

            var flakyAfterRetry = retriedResult.State.Steps.Single(s => s.StepId.Value == "flaky");
            Assert.Equal(StepStatus.Paused, flakyAfterRetry.Status);
            Assert.Equal(StepStatus.Succeeded, flakyAfterRetry.PausedOutcome);
            Assert.NotEqual(pausedExecutionId, flakyAfterRetry.LatestExecutionId);

            var resumeOptions = new DecideOptions(
                taskDirectory, flakyAfterRetry.LatestExecutionId!.Value.Value, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);
            var finalResult = await DecideCommand.ExecuteAsync(resumeOptions, Adapters);

            Assert.Equal(WorkflowStatus.Terminal, finalResult.State.Status);
            Assert.Equal(StepStatus.Succeeded, finalResult.State.Steps.Single(s => s.StepId.Value == "flaky").Status);
            Assert.Equal(StepStatus.Succeeded, finalResult.State.Steps.Single(s => s.StepId.Value == "downstream").Status);

            var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
            var downstreamOutput = Path.Combine(
                artifactsRoot,
                $"execution_{finalResult.State.Steps.Single(s => s.StepId.Value == "downstream").LatestExecutionId}",
                "final");
            Assert.Equal("revised-result", (await File.ReadAllTextAsync(downstreamOutput)).Trim());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Aer_supply_then_aer_decide_Supersede_reruns_the_target_step_and_a_final_Resume_reaches_terminal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-decide-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteSupersedeWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteSupersedeBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);

            var firstPauseResult = await RunCommand.ExecuteAsync(runOptions, Adapters);
            Assert.Equal(WorkflowStatus.Paused, firstPauseResult.State.Status);
            var reviewerExecutionId1 = firstPauseResult.State.Steps.Single(s => s.StepId.Value == "reviewer").LatestExecutionId!.Value;
            var sourceExecutionId1 = firstPauseResult.State.Steps.Single(s => s.StepId.Value == "source").LatestExecutionId!.Value;

            var revisionFilePath = Path.Combine(testRoot, "revision.txt");
            await File.WriteAllTextAsync(revisionFilePath, "revised-plan");
            var supplyOptions = new SupplyOptions(taskDirectory, "human", "revision", revisionFilePath, bindingsFilePath);
            var supplyResult = await SupplyCommand.ExecuteAsync(supplyOptions, Adapters);

            var supersedeOptions = new DecideOptions(
                taskDirectory, reviewerExecutionId1.Value, DecisionType.Supersede, new StepId("source"),
                supplyResult.ExecutionId.Value, bindingsFilePath);
            var secondPauseResult = await DecideCommand.ExecuteAsync(supersedeOptions, Adapters);

            Assert.Equal(WorkflowStatus.Paused, secondPauseResult.State.Status);
            var sourceExecutionId2 = secondPauseResult.State.Steps.Single(s => s.StepId.Value == "source").LatestExecutionId!.Value;
            var reviewerExecutionId2 = secondPauseResult.State.Steps.Single(s => s.StepId.Value == "reviewer").LatestExecutionId!.Value;
            Assert.NotEqual(sourceExecutionId1, sourceExecutionId2);
            Assert.NotEqual(reviewerExecutionId1, reviewerExecutionId2);

            var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
            var sourceOutput2 = Path.Combine(artifactsRoot, $"execution_{sourceExecutionId2}", "plan");
            Assert.Equal("revised-plan", (await File.ReadAllTextAsync(sourceOutput2)).Trim());

            var resumeOptions = new DecideOptions(
                taskDirectory, reviewerExecutionId2.Value, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);
            var finalResult = await DecideCommand.ExecuteAsync(resumeOptions, Adapters);

            Assert.Equal(WorkflowStatus.Terminal, finalResult.State.Status);
            Assert.Equal(StepStatus.Succeeded, finalResult.State.Steps.Single(s => s.StepId.Value == "source").Status);
            Assert.Equal(StepStatus.Succeeded, finalResult.State.Steps.Single(s => s.StepId.Value == "reviewer").Status);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task A_decision_against_a_non_paused_execution_throws_a_typed_error_and_appends_nothing()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-decide-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);

            var pausedResult = await RunCommand.ExecuteAsync(runOptions, Adapters);
            var pausedExecutionId = pausedResult.State.Steps.Single(s => s.StepId.Value == "a").LatestExecutionId!.Value;

            var invalidOptions = new DecideOptions(
                taskDirectory, "not-a-real-execution-id", DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);
            await Assert.ThrowsAsync<InvalidExternalDecisionException>(() => DecideCommand.ExecuteAsync(invalidOptions, Adapters));

            // The paused workflow is still perfectly resolvable by a valid decision afterward.
            var validOptions = new DecideOptions(
                taskDirectory, pausedExecutionId.Value, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);
            var finalResult = await DecideCommand.ExecuteAsync(validOptions, Adapters);
            Assert.Equal(WorkflowStatus.Terminal, finalResult.State.Status);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Deciding_against_a_task_directory_with_no_snapshot_throws_a_typed_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-decide-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var options = new DecideOptions(
                taskDirectory, "exec-1", DecisionType.Resume, TargetStepId: null, SupplementaryExecutionId: null, bindingsFilePath);

            await Assert.ThrowsAsync<SnapshotLoadException>(() => DecideCommand.ExecuteAsync(options, Adapters));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task<string> WriteApprovalGateWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("approval-gate"),
            1,
            [
                new WorkflowStepDefinition(
                    new StepId("a"), "a", [], ["out_a"], [], new RetryPolicy(1), new PausePoint([])),
                new WorkflowStepDefinition(
                    new StepId("b"), "b", ["out_a"], ["out_b"], [new StepId("a")], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteApprovalGateBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                WriteFileCommand("out_a", "a-out"), TimeSpan.FromSeconds(30)),
            ["b"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("b", ["out_a"], [new ProducedOutput("out_b")], []),
                CopyFirstInputCommand("out_b"), TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteRetryWithRevisionWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("retry-with-revision"),
            1,
            [
                new WorkflowStepDefinition(
                    new StepId("flaky"), "flaky", [], ["result"], [], new RetryPolicy(1), new PausePoint([])),
                new WorkflowStepDefinition(
                    new StepId("downstream"), "downstream", ["result"], ["final"], [new StepId("flaky")], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteRetryWithRevisionBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["flaky"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("flaky", [], [new ProducedOutput("result")], []),
                ConsumeSupplementaryInputElseFailCommand("result", "revision"), TimeSpan.FromSeconds(30)),
            ["downstream"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("downstream", ["result"], [new ProducedOutput("final")], []),
                CopyFirstInputCommand("final"), TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteSupersedeWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("supersede"),
            1,
            [
                new WorkflowStepDefinition(new StepId("source"), "source", [], ["plan"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(
                    new StepId("reviewer"), "reviewer", ["plan"], ["verdict"], [new StepId("source")],
                    new RetryPolicy(1), new PausePoint([new StepId("source")])),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteSupersedeBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["source"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("source", [], [new ProducedOutput("plan")], []),
                ConsumeSupplementaryInputElseWriteCommand("plan", "revision", "original-plan"), TimeSpan.FromSeconds(30)),
            ["reviewer"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("reviewer", ["plan"], [new ProducedOutput("verdict")], []),
                CopyFirstInputCommand("verdict"), TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) => OperatingSystem.IsWindows()
        ? $"echo {content}>%AER_OUTPUT_DIR%\\{outputName}"
        : $"echo {content} > \"$AER_OUTPUT_DIR/{outputName}\"";

    private static string CopyFirstInputCommand(string outputName) => OperatingSystem.IsWindows()
        ? $"type %AER_INPUT_0% >%AER_OUTPUT_DIR%\\{outputName}"
        : $"cat \"$AER_INPUT_0\" > \"$AER_OUTPUT_DIR/{outputName}\"";

    private static string ConsumeSupplementaryInputElseFailCommand(string outputName, string supplementaryFileName) => OperatingSystem.IsWindows()
        ? $"if defined AER_SUPPLEMENTARY_INPUT (copy /y %AER_SUPPLEMENTARY_INPUT%\\{supplementaryFileName} %AER_OUTPUT_DIR%\\{outputName} >nul) else (exit /b 1)"
        : $"if [ -n \"$AER_SUPPLEMENTARY_INPUT\" ]; then cp \"$AER_SUPPLEMENTARY_INPUT/{supplementaryFileName}\" \"$AER_OUTPUT_DIR/{outputName}\"; else exit 1; fi";

    private static string ConsumeSupplementaryInputElseWriteCommand(string outputName, string supplementaryFileName, string baseContent) => OperatingSystem.IsWindows()
        ? $"if defined AER_SUPPLEMENTARY_INPUT (copy /y %AER_SUPPLEMENTARY_INPUT%\\{supplementaryFileName} %AER_OUTPUT_DIR%\\{outputName} >nul) else (echo {baseContent}>%AER_OUTPUT_DIR%\\{outputName})"
        : $"if [ -n \"$AER_SUPPLEMENTARY_INPUT\" ]; then cp \"$AER_SUPPLEMENTARY_INPUT/{supplementaryFileName}\" \"$AER_OUTPUT_DIR/{outputName}\"; else echo {baseContent} > \"$AER_OUTPUT_DIR/{outputName}\"; fi";
}
