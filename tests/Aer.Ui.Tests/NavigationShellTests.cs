using Aer.Ui.Tests.TestSupport;
using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Aer.Ui.Tests;

/// <summary>
/// M19 Phase 2 (issue #187): the navigation shell and Home's decision inbox — section switching,
/// the paused-step inbox item with its artifact preview, and §3's stale-recents card. Task
/// directories are built from hand-written <see cref="FlowEvent"/>s, matching
/// <see cref="MainWindowProjectionTests"/>' convention.
/// </summary>
public class NavigationShellTests
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

    private static ExecutionRequest MakeRequest(ExecutionId executionId, StepId stepId)
        => new(
            executionId,
            new WorkflowId("wf-1"),
            stepId,
            "worker",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-shell-config-{Guid.NewGuid():N}", "recent-task-directories.json");

    private static async Task<string> CreateTaskDirectoryAsync(
        WorkflowDefinitionSnapshot snapshot, IEnumerable<FlowEvent> events, CancellationToken cancellationToken)
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}");
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

    /// <summary>A task paused at critic, with a durable output file for the inbox preview.</summary>
    private static async Task<string> CreatePausedTaskDirectoryAsync(string reviewContent, CancellationToken cancellationToken)
    {
        var architectExecutionId = new ExecutionId("a-1");
        var criticExecutionId = new ExecutionId("c-1");
        var taskDirectory = await CreateTaskDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
                new FlowEvent.ExecutionSucceeded(architectExecutionId),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
                new FlowEvent.ExecutionSucceeded(criticExecutionId),
                new FlowEvent.WorkflowPaused(criticExecutionId, Critic),
            ],
            cancellationToken);

        var outputDirectory = Path.Combine(taskDirectory, "artifacts", "execution_c-1");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "review.md"), reviewContent, cancellationToken);
        return taskDirectory;
    }

    /// <summary>A task paused at a NeedsInput pause point (#334) — the shape an interactive session settles into: "your turn to reply", not an approval gate.</summary>
    private static async Task<string> CreateNeedsInputTaskDirectoryAsync(string replyContent, CancellationToken cancellationToken)
    {
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("session-like"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(3)),
                new WorkflowStepDefinition(
                    Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1),
                    PausePoint: new PausePoint(SupersedeTargets: [Architect], Kind: PausePointKind.NeedsInput)),
            ]));

        var architectExecutionId = new ExecutionId("a-1");
        var criticExecutionId = new ExecutionId("c-1");
        var taskDirectory = await CreateTaskDirectoryAsync(
            snapshot,
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
                new FlowEvent.ExecutionSucceeded(architectExecutionId),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
                new FlowEvent.ExecutionSucceeded(criticExecutionId),
                new FlowEvent.WorkflowPaused(criticExecutionId, Critic),
            ],
            cancellationToken);

        var outputDirectory = Path.Combine(taskDirectory, "artifacts", "execution_c-1");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "review.md"), replyContent, cancellationToken);
        return taskDirectory;
    }

    [AvaloniaFact]
    public async Task InitializeAsync_starts_on_the_home_section()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        await window.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ShellSection.Home, window.ViewModel.CurrentSection);
        Assert.True(window.ViewModel.IsHomeVisible);
        Assert.False(window.ViewModel.IsTaskVisible);
        Assert.Equal("Nothing is waiting on you.", window.ViewModel.Home.InboxSummaryText);
    }

    [AvaloniaFact]
    public async Task OpenAsync_navigates_to_the_task_section()
    {
        var executionId = new ExecutionId("a-1");
        var taskDirectory = await CreateTaskDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
                new FlowEvent.ExecutionSucceeded(executionId),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(taskDirectory, TestContext.Current.CancellationToken);

            Assert.Equal(ShellSection.Task, window.ViewModel.CurrentSection);
            Assert.True(window.ViewModel.IsTaskVisible);
            Assert.False(window.ViewModel.IsHomeVisible);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    /// <summary>M24 Phase 1 desktop chat UI (issue #262): opening a directory that materialized an interactive session (.aer/session.json present) routes to the dedicated Chat view instead of the generic Task view — see <c>MainWindow.OpenAsync</c>'s remarks.</summary>
    [AvaloniaFact]
    public async Task OpenAsync_routes_an_interactive_session_directory_to_the_chat_section()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-shell-chat-{Guid.NewGuid():N}");
        try
        {
            var metadata = await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                sessionId: "sess-nav-test",
                taskDirectoryPath: taskDirectory,
                adapter: "claude",
                cancellationToken: TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(taskDirectory, TestContext.Current.CancellationToken);

            Assert.Equal(ShellSection.Chat, window.ViewModel.CurrentSection);
            Assert.True(window.ViewModel.IsChatVisible);
            Assert.False(window.ViewModel.IsTaskVisible);
            Assert.Equal(metadata.SessionId, window.ViewModel.Chat.SessionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    [AvaloniaFact]
    public async Task InitializeAsync_surfaces_a_paused_recent_as_an_inbox_item_with_its_artifact_preview()
    {
        var configFilePath = NewConfigFilePath();
        var taskDirectory = await CreatePausedTaskDirectoryAsync(
            "The plan looks solid overall.", TestContext.Current.CancellationToken);
        try
        {
            await new LocalUiConfigurationStore(configFilePath)
                .RecordOpenedAsync(taskDirectory, TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(configFilePath));
            await window.InitializeAsync(TestContext.Current.CancellationToken);

            var card = Assert.Single(window.ViewModel.Home.TaskCards);
            Assert.Equal(TaskCardStatus.NeedsYou, card.Status);
            Assert.Equal("Waiting for your review", card.StatusText);

            var item = Assert.Single(window.ViewModel.Home.InboxItems);
            Assert.Equal("critic", item.StepName);
            Assert.Equal("Waiting for your review — review.md ready", item.StatusText);
            Assert.True(item.HasPreview);
            Assert.Equal("The plan looks solid overall.", item.PreviewText);
            Assert.Equal("1 step is waiting for your review.", window.ViewModel.Home.InboxSummaryText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    [AvaloniaFact]
    public async Task InitializeAsync_surfaces_a_needs_input_pause_as_a_reply_not_a_review()
    {
        // #334: the exact bug — a settled chat turn showed "Waiting for your review" and a [Review]
        // button. A NeedsInput pause must read as "your turn to reply" on every Home surface.
        var configFilePath = NewConfigFilePath();
        var taskDirectory = await CreateNeedsInputTaskDirectoryAsync("ok", TestContext.Current.CancellationToken);
        try
        {
            await new LocalUiConfigurationStore(configFilePath)
                .RecordOpenedAsync(taskDirectory, TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(configFilePath));
            await window.InitializeAsync(TestContext.Current.CancellationToken);

            var card = Assert.Single(window.ViewModel.Home.TaskCards);
            Assert.Equal(TaskCardStatus.NeedsYou, card.Status);
            Assert.Equal("Waiting for your reply", card.StatusText);

            var item = Assert.Single(window.ViewModel.Home.InboxItems);
            Assert.Equal(PausePointKind.NeedsInput, item.Kind);
            Assert.Equal("Waiting for your reply", item.StatusText);
            Assert.Equal("Reply", item.ActionLabel);
            Assert.Equal("1 session is waiting for your reply.", window.ViewModel.Home.InboxSummaryText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    [AvaloniaFact]
    public async Task Inbox_review_opens_the_task_and_navigates_to_the_task_section()
    {
        var configFilePath = NewConfigFilePath();
        var taskDirectory = await CreatePausedTaskDirectoryAsync(
            "Needs another pass at the error handling.", TestContext.Current.CancellationToken);
        try
        {
            await new LocalUiConfigurationStore(configFilePath)
                .RecordOpenedAsync(taskDirectory, TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(configFilePath));
            await window.InitializeAsync(TestContext.Current.CancellationToken);

            var item = Assert.Single(window.ViewModel.Home.InboxItems);
            await item.ReviewCommand.ExecuteAsync(null);

            Assert.Equal(ShellSection.Task, window.ViewModel.CurrentSection);
            Assert.Equal(
                Path.GetFullPath(taskDirectory), window.FindViewControl<TextBox>("TaskDirectoryPathBox")!.Text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    /// <summary>M24 Phase 5 (#278): the sixth nav destination — a fleet management view distinct from Home's capped recents cards.</summary>
    [AvaloniaFact]
    public async Task NavigatingToTasks_showsTheTasksSectionAndHidesEverythingElse()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        await window.InitializeAsync(TestContext.Current.CancellationToken);

        window.ViewModel.CurrentSection = ShellSection.Tasks;

        Assert.True(window.ViewModel.IsTasksVisible);
        Assert.False(window.ViewModel.IsHomeVisible);
        Assert.False(window.ViewModel.IsTaskVisible);
        Assert.False(window.ViewModel.IsChatVisible);
        Assert.False(window.ViewModel.IsRemoteVisible);
    }

    [AvaloniaFact]
    public async Task A_recent_that_no_longer_loads_renders_as_an_unavailable_card_not_an_error()
    {
        var configFilePath = NewConfigFilePath();
        var notATaskDirectory = Path.Combine(Path.GetTempPath(), $"ui-shell-stale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(notATaskDirectory);
        try
        {
            await new LocalUiConfigurationStore(configFilePath)
                .RecordOpenedAsync(notATaskDirectory, TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(configFilePath));
            await window.InitializeAsync(TestContext.Current.CancellationToken);

            var card = Assert.Single(window.ViewModel.Home.TaskCards);
            Assert.Equal(TaskCardStatus.Unavailable, card.Status);
            Assert.Equal("Not available — moved, deleted, or not a task", card.StatusText);
            Assert.Empty(window.ViewModel.Home.InboxItems);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(notATaskDirectory);
        }
    }
}
