using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Aer.Ui.Tests;

/// <summary>
/// M15 Phase 2 (issue #138): §7's Approve/Reject decisions proven end to end through the real
/// <see cref="MainWindow"/> — in-process reuse of <c>Aer.Cli.DecideCommand.ExecuteAsync</c>, driven
/// through a deterministic shell-stub <see cref="IWorkerAdapter"/> exactly like
/// <see cref="MainWindowRunTests"/>, so this is CI-safe on every OS. A task is first driven to
/// <see cref="WorkflowStatus.Paused"/> through the real <see cref="MainWindow.RunAsync"/> (Phase 1's
/// seam), then resolved through <see cref="PausedStepViewModel.ApproveCommand"/>/
/// <see cref="PausedStepViewModel.RejectCommand"/> — the same commands the bound "Approve"/"Reject"
/// buttons in <c>MainWindow.axaml</c> invoke.
/// </summary>
public class MainWindowDecisionTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-decide-config-{Guid.NewGuid():N}", "recent-task-directories.json");

    [AvaloniaFact]
    public async Task Approve_resolves_the_pause_to_its_underlying_outcome_and_the_workflow_runs_to_terminal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-decide-approve-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), Adapters);

            await window.RunAsync(roomDirectory, workflowFilePath, bindingsFilePath, TestContext.Current.CancellationToken);

            var statusText = window.FindViewControl<TextBlock>("StatusText")!;
            Assert.Equal("Workflow status: Paused", statusText.Text);

            var pausedStep = Assert.Single(window.ViewModel.PausedSteps);
            Assert.Equal(new StepId("a"), pausedStep.StepId);

            await pausedStep.ApproveCommand.ExecuteAsync(null);

            Assert.Equal("Workflow status: Terminal", statusText.Text);
            Assert.Empty(window.ViewModel.PausedSteps);
            Assert.Equal(string.Empty, window.ViewModel.DecisionStatusText);
            Assert.False(window.IsLiveRefreshTimerEnabled);

            var stepsPanel = window.FindViewControl<StackPanel>("StepsPanel")!;
            Assert.Equal(
                ["a: Succeeded", "b: Succeeded"],
                stepsPanel.Children.OfType<TextBlock>().Select(block => block.Text).ToList());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [AvaloniaFact]
    public async Task Reject_projects_the_paused_step_terminally_failed_and_the_downstream_step_never_dispatches()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-decide-reject-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), Adapters);

            await window.RunAsync(roomDirectory, workflowFilePath, bindingsFilePath, TestContext.Current.CancellationToken);

            var pausedStep = Assert.Single(window.ViewModel.PausedSteps);

            await pausedStep.RejectCommand.ExecuteAsync(null);

            var statusText = window.FindViewControl<TextBlock>("StatusText")!;
            Assert.Equal("Workflow status: Terminal", statusText.Text);
            Assert.Empty(window.ViewModel.PausedSteps);

            var stepsPanel = window.FindViewControl<StackPanel>("StepsPanel")!;
            Assert.Equal(
                ["a: Rejected", "b: Pending"],
                stepsPanel.Children.OfType<TextBlock>().Select(block => block.Text).ToList());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [AvaloniaFact]
    public async Task An_invalid_decision_renders_as_an_in_window_message_not_a_crash_and_leaves_the_pause_intact()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-decide-invalid-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), Adapters);

            await window.RunAsync(roomDirectory, workflowFilePath, bindingsFilePath, TestContext.Current.CancellationToken);

            // A bindings file naming an adapter this window's registry doesn't have makes
            // DecideCommand throw UnknownWorkerAdapterException before it ever reaches the mutation
            // interface — standing in for any AerFlowException a real decide call can surface (a
            // competing external pump's WorkflowLockedException included), which this phase's
            // decision surface must render as a message, never crash the window.
            var unresolvableBindingsFilePath = await WriteUnresolvableBindingsAsync(testRoot);
            window.FindViewControl<TextBox>("BindingsFilePathBox")!.Text = unresolvableBindingsFilePath;
            var pausedStep = Assert.Single(window.ViewModel.PausedSteps);

            await pausedStep.ApproveCommand.ExecuteAsync(null);

            Assert.NotEqual(string.Empty, window.ViewModel.DecisionStatusText);
            Assert.False(window.ViewModel.IsMutationInFlight);
            Assert.True(pausedStep.IsEnabled);

            var statusText = window.FindViewControl<TextBlock>("StatusText")!;
            Assert.Equal("Workflow status: Paused", statusText.Text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [AvaloniaFact]
    public async Task Answering_gate_retires_inbox_item_without_home_refresh()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-decide-retire-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var configStore = new LocalUiConfigurationStore(NewConfigFilePath());
            await configStore.RecordOpenedAsync(roomDirectory);

            var window = new MainWindow(configStore, Adapters);

            await window.RunAsync(roomDirectory, workflowFilePath, bindingsFilePath, TestContext.Current.CancellationToken);

            // Seed the inbox item in Home
            await window.ViewModel.Home.RefreshAsync(
                window.Session,
                path => window.OpenAsync(path));

            var inboxItem = Assert.Single(window.ViewModel.Home.InboxItems);
            Assert.Equal("a", inboxItem.StepName);

            var pausedStep = Assert.Single(window.ViewModel.PausedSteps);

            // Decide inline without calling Home's refresh
            await pausedStep.ApproveCommand.ExecuteAsync(null);

            // Assert inbox item is gone immediately
            Assert.Empty(window.ViewModel.Home.InboxItems);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void Decision_polarity_does_not_retire_unmatched_inbox_items()
    {
        var home = new Aer.Ui.Core.HomeViewModel();
        var taskPath1 = Path.Combine(Path.GetTempPath(), "task1");
        var taskPath2 = Path.Combine(Path.GetTempPath(), "task2");

        var item1 = new Aer.Ui.Core.InboxItemViewModel(
            taskPath1, "Task 1", "step-a", "Status", "Preview",
            Aer.Flow.Domain.PausePointKind.ReadyForReview, _ => Task.CompletedTask, "exec-1");

        var item2 = new Aer.Ui.Core.InboxItemViewModel(
            taskPath1, "Task 1", "step-b", "Status", "Preview",
            Aer.Flow.Domain.PausePointKind.ReadyForReview, _ => Task.CompletedTask, "exec-1");

        var item3 = new Aer.Ui.Core.InboxItemViewModel(
            taskPath2, "Task 2", "step-a", "Status", "Preview",
            Aer.Flow.Domain.PausePointKind.ReadyForReview, _ => Task.CompletedTask, "exec-1");

        home.InboxItems.Add(item1);
        home.InboxItems.Add(item2);
        home.InboxItems.Add(item3);

        // Retire step-a of task 1
        home.RetireInboxItem(taskPath1, new StepId("step-a"), new ExecutionId("exec-1"));

        Assert.Equal(2, home.InboxItems.Count);
        Assert.DoesNotContain(item1, home.InboxItems);
        Assert.Contains(item2, home.InboxItems);
        Assert.Contains(item3, home.InboxItems);
    }

    [AvaloniaFact]
    public async Task Loading_a_locked_task_renders_waiting_on_lock_banner_with_holder_text()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-lock-banner-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var configStore = new LocalUiConfigurationStore(NewConfigFilePath());

            // Acquire lock in fixture before opening/loading
            using var lockGuard = Aer.Flow.Concurrency.ConcurrencyGuard.Acquire(roomDirectory, "Other Room (pid 777)");

            var window = new MainWindow(configStore, Adapters);
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            Assert.True(window.ViewModel.HasWaitingOnLockBanner);
            Assert.NotNull(window.ViewModel.WaitingOnLockBanner);
            Assert.Equal("Waiting on another room's lock", window.ViewModel.WaitingOnLockBanner.Title);
            Assert.Equal("Other Room (pid 777)", window.ViewModel.WaitingOnLockBanner.HolderText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [AvaloniaFact]
    public async Task Loading_an_unlocked_task_does_not_render_waiting_on_lock_banner()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-lock-banner-polarity-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var configStore = new LocalUiConfigurationStore(NewConfigFilePath());

            var window = new MainWindow(configStore, Adapters);
            await window.RunAsync(roomDirectory, workflowFilePath, bindingsFilePath, TestContext.Current.CancellationToken);

            Assert.False(window.ViewModel.HasWaitingOnLockBanner);
            Assert.Null(window.ViewModel.WaitingOnLockBanner);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
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

    private static async Task<string> WriteUnresolvableBindingsAsync(string directory)
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "not-a-registered-adapter", new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                WriteFileCommand("out_a", "a-out"), TimeSpan.FromSeconds(30)),
            ["b"] = new WorkerBindingConfigEntry(
                "not-a-registered-adapter", new WorkerContract("b", ["out_a"], [new ProducedOutput("out_b")], []),
                CopyFirstInputCommand("out_b"), TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "unresolvable-bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) => OperatingSystem.IsWindows()
        ? $"echo {content}>%AER_OUTPUT_DIR%\\{outputName}"
        : $"echo {content} > \"$AER_OUTPUT_DIR/{outputName}\"";

    private static string CopyFirstInputCommand(string outputName) => OperatingSystem.IsWindows()
        ? $"type %AER_INPUT_0% >%AER_OUTPUT_DIR%\\{outputName}"
        : $"cat \"$AER_INPUT_0\" > \"$AER_OUTPUT_DIR/{outputName}\"";
}
