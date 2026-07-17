using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Aer.Ui.Tests;

/// <summary>
/// M14 Phase 2 (issue #119): the full read-model surface plus change observation, driven through
/// the real <see cref="MainWindow"/> exactly like <see cref="MainWindowTests"/> already does for
/// Phase 1's rendering, but building task directories directly from hand-written
/// <see cref="FlowEvent"/>s (matching <c>Aer.Flow.Tests.Projection.StateProjectorTests</c>'
/// convention) rather than driving a full <c>MutationInterface</c> pump — the point here is what the
/// UI renders from a given event history, not re-proving dispatch behavior Aer.Flow's own tests
/// already cover. Every assertion drives <see cref="MainWindow.OpenAsync"/>/<c>RefreshAsync</c>
/// directly rather than simulating a button click, the same reason <see cref="MainWindow.LoadAsync"/>
/// is public and awaitable (issue #118): deterministic, no dispatcher-timer pumping.
/// </summary>
public class MainWindowProjectionTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");

    private static WorkflowDefinitionSnapshot TwoStepSnapshot() => SnapshotBinder.Bind(new WorkflowDefinition(
        new WorkflowTemplateId("architect-critic"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(3)),
            new WorkflowStepDefinition(
                Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1),
                PausePoint: new PausePoint(SupersedeTargets: [Architect])),
        ]));

    /// <summary>An ordinary process dispatch — always a real (non-null) <c>Timeout</c>.</summary>
    private static ExecutionRequest MakeRequest(ExecutionId executionId, StepId? stepId, string worker = "worker")
        => new(
            executionId,
            new WorkflowId("wf-1"),
            stepId,
            worker,
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    /// <summary>A non-process dispatch (spec §17.3) — always a <c>null</c> <c>Timeout</c>; a distinct helper, not an optional parameter on <see cref="MakeRequest"/>, so a call can never get the wrong one via a defaulted argument.</summary>
    private static ExecutionRequest MakeNonProcessRequest(ExecutionId executionId, StepId? stepId, string worker = "human")
        => new(
            executionId,
            new WorkflowId("wf-1"),
            stepId,
            worker,
            Inputs: [],
            Outputs: [],
            Timeout: null,
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-window-config-{Guid.NewGuid():N}", "recent-task-directories.json");

    private static async Task<string> CreateTaskDirectoryAsync(
        WorkflowDefinitionSnapshot snapshot, IEnumerable<FlowEvent> events, CancellationToken cancellationToken)
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-window-history-{Guid.NewGuid():N}");
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(taskDirectory, "snapshot.json"), cancellationToken);

        await using (var writer = new FlowEventLogWriter(Path.Combine(taskDirectory, "flow.jsonl")))
        {
            foreach (var flowEvent in events)
            {
                await writer.AppendAsync(flowEvent, cancellationToken);
            }
        }

        return taskDirectory;
    }

    private static List<string> TextsOf(StackPanel panel) => panel.Children.OfType<TextBlock>().Select(block => block.Text!).ToList();

    [AvaloniaFact]
    public async Task LoadAsync_renders_full_attempt_history_and_retry_state_in_the_history_panel()
    {
        var snapshot = TwoStepSnapshot();
        var firstArchitectAttempt = new ExecutionId("a-1");
        var secondArchitectAttempt = new ExecutionId("a-2");
        var criticExecutionId = new ExecutionId("c-1");
        var taskDirectory = await CreateTaskDirectoryAsync(
            snapshot,
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(firstArchitectAttempt, Architect)),
                new FlowEvent.ExecutionFailed(firstArchitectAttempt, FailureClassification.Retryable),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(secondArchitectAttempt, Architect)),
                new FlowEvent.ExecutionSucceeded(secondArchitectAttempt),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
                new FlowEvent.ExecutionSucceeded(criticExecutionId),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            var historyPanel = window.FindViewControl<StackPanel>("HistoryPanel")!;
            Assert.Equal(
                [
                    "architect attempt 1/2: a-1 -> Failed (Retryable)",
                    "architect attempt 2/2: a-2 -> Succeeded",
                    "architect: consecutive failures=0",
                    "critic attempt 1/1: c-1 -> Succeeded",
                    "critic: consecutive failures=0",
                ],
                TextsOf(historyPanel));
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task LoadAsync_renders_pause_state_supersede_targets_and_unresolved_decisions()
    {
        var snapshot = TwoStepSnapshot();
        var architectExecutionId = new ExecutionId("a-1");
        var criticExecutionId = new ExecutionId("c-1");
        var decisionId = new DecisionId("decision-1");
        var taskDirectory = await CreateTaskDirectoryAsync(
            snapshot,
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
                new FlowEvent.ExecutionSucceeded(architectExecutionId),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
                new FlowEvent.ExecutionSucceeded(criticExecutionId),
                new FlowEvent.WorkflowPaused(criticExecutionId, Critic),
                new FlowEvent.ExternalDecisionRecorded(decisionId, criticExecutionId, DecisionType.RetryWithRevision, null, null),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            var historyPanel = window.FindViewControl<StackPanel>("HistoryPanel")!;
            var historyTexts = TextsOf(historyPanel);
            Assert.Contains(
                "critic: consecutive failures=0, paused (underlying outcome=Succeeded), supersede targets=[architect]",
                historyTexts);

            var decisionsPanel = window.FindViewControl<StackPanel>("DecisionsPanel")!;
            Assert.Equal(
                ["decision-1: RetryWithRevision on c-1 (unresolved)"],
                TextsOf(decisionsPanel));
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task LoadAsync_renders_settled_supplementary_and_human_executions()
    {
        var snapshot = TwoStepSnapshot();
        var humanExecutionId = new ExecutionId("supplement-1");
        var taskDirectory = await CreateTaskDirectoryAsync(
            snapshot,
            [
                new FlowEvent.ExecutionRequestAccepted(MakeNonProcessRequest(humanExecutionId, null)),
                new FlowEvent.ExecutionSucceeded(humanExecutionId),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            var supplementaryPanel = window.FindViewControl<StackPanel>("SupplementaryPanel")!;
            Assert.Equal(["supplement-1 (human): Succeeded [non-process]"], TextsOf(supplementaryPanel));
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task OpenAsync_records_the_opened_directory_as_a_recent()
    {
        var snapshot = TwoStepSnapshot();
        var executionId = new ExecutionId("a-1");
        var taskDirectory = await CreateTaskDirectoryAsync(
            snapshot,
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
                new FlowEvent.ExecutionSucceeded(executionId),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(taskDirectory, TestContext.Current.CancellationToken);

            // M19 Phase 2 (#187): the recents list is projected as Home's task cards now.
            var card = Assert.Single(window.ViewModel.Home.TaskCards);
            Assert.Equal(Path.GetFullPath(taskDirectory), card.TaskDirectoryPath);
            Assert.Equal(taskDirectory, window.FindViewControl<TextBox>("TaskDirectoryPathBox")!.Text);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task OpenAsync_does_not_record_a_directory_that_failed_to_load()
    {
        var notATaskDirectory = Path.Combine(Path.GetTempPath(), $"ui-window-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(notATaskDirectory);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(notATaskDirectory, TestContext.Current.CancellationToken);

            // M19 Phase 2 (#187): the recents list is projected as Home's task cards now.
            Assert.Empty(window.ViewModel.Home.TaskCards);
        }
        finally
        {
            Directory.Delete(notATaskDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task InitializeAsync_populates_the_recents_panel_from_local_configuration_at_startup()
    {
        var configFilePath = NewConfigFilePath();
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-window-recent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(taskDirectory);
        try
        {
            var configurationStore = new LocalUiConfigurationStore(configFilePath);
            await configurationStore.RecordOpenedAsync(taskDirectory, TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(configFilePath));
            await window.InitializeAsync(TestContext.Current.CancellationToken);

            // M19 Phase 2 (#187): the recents list is projected as Home's task cards now.
            var card = Assert.Single(window.ViewModel.Home.TaskCards);
            Assert.Equal(Path.GetFullPath(taskDirectory), card.TaskDirectoryPath);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task RefreshAsync_picks_up_events_appended_after_the_initial_open_while_still_running()
    {
        var snapshot = TwoStepSnapshot();
        var architectExecutionId = new ExecutionId("a-1");
        var criticExecutionId = new ExecutionId("c-1");
        var taskDirectory = await CreateTaskDirectoryAsync(
            snapshot,
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(taskDirectory, TestContext.Current.CancellationToken);

            var stepsPanel = window.FindViewControl<StackPanel>("StepsPanel")!;
            Assert.Equal(["architect: Running", "critic: Running"], TextsOf(stepsPanel));
            Assert.True(window.IsLiveRefreshTimerEnabled);

            await using (var writer = new FlowEventLogWriter(Path.Combine(taskDirectory, "flow.jsonl")))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(architectExecutionId), TestContext.Current.CancellationToken);
            }

            await window.RefreshAsync(TestContext.Current.CancellationToken);

            Assert.Equal(["architect: Succeeded", "critic: Running"], TextsOf(stepsPanel));
            // Critic is still running — nothing further to observe yet, so polling continues.
            Assert.True(window.IsLiveRefreshTimerEnabled);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Live_refresh_stops_once_the_workflow_reaches_a_terminal_state()
    {
        var snapshot = TwoStepSnapshot();
        var architectExecutionId = new ExecutionId("a-1");
        var criticExecutionId = new ExecutionId("c-1");
        var taskDirectory = await CreateTaskDirectoryAsync(
            snapshot,
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
                new FlowEvent.ExecutionSucceeded(architectExecutionId),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(taskDirectory, TestContext.Current.CancellationToken);
            Assert.True(window.IsLiveRefreshTimerEnabled);

            await using (var writer = new FlowEventLogWriter(Path.Combine(taskDirectory, "flow.jsonl")))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(criticExecutionId), TestContext.Current.CancellationToken);
            }

            await window.RefreshAsync(TestContext.Current.CancellationToken);

            Assert.Equal("Workflow status: Terminal", window.FindViewControl<TextBlock>("StatusText")!.Text);
            Assert.False(window.IsLiveRefreshTimerEnabled);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task RefreshAsync_before_anything_has_been_opened_is_a_no_op()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));

        await window.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("No task directory loaded.", window.FindViewControl<TextBlock>("StatusText")!.Text);
    }
}
