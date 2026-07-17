using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Aer.Ui.Tests;

/// <summary>
/// M15 Phase 3 (issue #139): §7's artifact-carrying decisions — Retry-with-revision and Send-back —
/// proven end to end through the real <see cref="MainWindow"/>, the same discipline
/// <see cref="MainWindowDecisionTests"/> established for Approve/Reject: a task is first driven to
/// <see cref="WorkflowStatus.Paused"/> through <see cref="MainWindow.RunAsync"/>, then resolved
/// through <see cref="PausedStepViewModel.RetryCommand"/>/<see cref="SendBackTargetViewModel.SendBackCommand"/>
/// — the same commands the bound "Retry"/"Send back to X" buttons in <c>MainWindow.axaml</c> invoke —
/// which themselves drive the <c>aer supply</c> → <c>aer decide</c> two-call round trip
/// (<see cref="MainWindow"/>'s private <c>DecideAsync</c>) via a deterministic shell-stub
/// <see cref="IWorkerAdapter"/>, CI-safe on every OS.
/// </summary>
public class MainWindowRetryAndSendBackTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-retry-config-{Guid.NewGuid():N}", "recent-task-directories.json");

    [AvaloniaFact]
    public async Task Retry_with_a_revision_file_supplies_the_artifact_then_reruns_the_paused_step_and_the_workflow_runs_to_terminal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-retry-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteRetryWithRevisionWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteRetryWithRevisionBindingsAsync(testRoot);
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), Adapters);

            await window.RunAsync(taskDirectory, workflowFilePath, bindingsFilePath, TestContext.Current.CancellationToken);

            var statusText = window.FindViewControl<TextBlock>("StatusText")!;
            Assert.Equal("Workflow status: Paused", statusText.Text);

            var pausedStep = Assert.Single(window.ViewModel.PausedSteps);
            Assert.Equal(new StepId("flaky"), pausedStep.StepId);
            Assert.Empty(pausedStep.SendBackTargets);

            var revisionFilePath = Path.Combine(testRoot, "revised.txt");
            await File.WriteAllTextAsync(revisionFilePath, "revised-result", TestContext.Current.CancellationToken);
            pausedStep.RevisionFilePath = revisionFilePath;
            pausedStep.SupplementaryWorker = "human";
            pausedStep.SupplementaryOutputName = "revision";
            Assert.True(pausedStep.RetryCommand.CanExecute(null));

            await pausedStep.RetryCommand.ExecuteAsync(null);

            // RetryWithRevision resolves the pause but re-pauses the step at its new (successful)
            // outcome — the same fixed point DecideCommandEndToEndTests observes at the CLI layer —
            // so a second decision (Approve) is what actually reaches terminal.
            Assert.Equal("Workflow status: Paused", statusText.Text);
            var repausedStep = Assert.Single(window.ViewModel.PausedSteps);
            Assert.Equal(new StepId("flaky"), repausedStep.StepId);
            Assert.NotEqual(pausedStep.ExecutionId, repausedStep.ExecutionId);

            await repausedStep.ApproveCommand.ExecuteAsync(null);

            Assert.Equal("Workflow status: Terminal", statusText.Text);
            var stepsPanel = window.FindViewControl<StackPanel>("StepsPanel")!;
            Assert.Equal(
                ["flaky: Succeeded", "downstream: Succeeded"],
                stepsPanel.Children.OfType<TextBlock>().Select(block => block.Text).ToList());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Send_back_offers_only_declared_SupersedeTargets_supplies_the_artifact_and_reruns_the_target()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-sendback-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteSupersedeWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteSupersedeBindingsAsync(testRoot);
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), Adapters);

            await window.RunAsync(taskDirectory, workflowFilePath, bindingsFilePath, TestContext.Current.CancellationToken);

            var statusText = window.FindViewControl<TextBlock>("StatusText")!;
            Assert.Equal("Workflow status: Paused", statusText.Text);

            var pausedStep = Assert.Single(window.ViewModel.PausedSteps);
            Assert.Equal(new StepId("reviewer"), pausedStep.StepId);
            var sendBackTarget = Assert.Single(pausedStep.SendBackTargets);
            Assert.Equal(new StepId("source"), sendBackTarget.TargetStepId);
            Assert.Equal("Send back to source", sendBackTarget.Label);

            // Mandatory-artifact constraint (§7): Send back cannot be submitted before the revision
            // file (and the worker/output-name pair aer supply needs) are filled in.
            Assert.False(sendBackTarget.SendBackCommand.CanExecute(null));

            var revisionFilePath = Path.Combine(testRoot, "revised-plan.txt");
            await File.WriteAllTextAsync(revisionFilePath, "revised-plan", TestContext.Current.CancellationToken);
            pausedStep.RevisionFilePath = revisionFilePath;
            pausedStep.SupplementaryWorker = "human";
            pausedStep.SupplementaryOutputName = "revision";
            Assert.True(sendBackTarget.SendBackCommand.CanExecute(null));

            await sendBackTarget.SendBackCommand.ExecuteAsync(null);

            Assert.Equal("Workflow status: Paused", statusText.Text);
            var repausedStep = Assert.Single(window.ViewModel.PausedSteps);
            Assert.Equal(new StepId("reviewer"), repausedStep.StepId);
            Assert.NotEqual(pausedStep.ExecutionId, repausedStep.ExecutionId);

            var lineagePanel = window.FindViewControl<StackPanel>("LineagePanel")!;
            Assert.NotEmpty(lineagePanel.Children);

            await repausedStep.ApproveCommand.ExecuteAsync(null);

            Assert.Equal("Workflow status: Terminal", statusText.Text);
            var stepsPanel = window.FindViewControl<StackPanel>("StepsPanel")!;
            Assert.Equal(
                ["source: Succeeded", "reviewer: Succeeded"],
                stepsPanel.Children.OfType<TextBlock>().Select(block => block.Text).ToList());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
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
