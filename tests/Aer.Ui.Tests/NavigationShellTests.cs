using Aer.Ui.Tests.TestSupport;
using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

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
        Path.Combine(Path.GetTempPath(), $"aer-ui-shell-config-{Guid.NewGuid():N}", "recent-room-directories.json");

    private static async Task<string> CreateRoomDirectoryAsync(
        WorkflowDefinitionSnapshot snapshot, IEnumerable<FlowEvent> events, CancellationToken cancellationToken)
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}");
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), cancellationToken);

        await using (var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, "flow.jsonl")))
        {
            foreach (var flowEvent in events)
            {
                await writer.AppendAsync(flowEvent, cancellationToken);
            }
        }

        return roomDirectory;
    }

    /// <summary>
    /// #461: a cancelled run reaches <see cref="WorkflowStatus.Terminal"/> like any other — there is
    /// no cancelled workflow status — so the card derivation fell through to "Finished" and told you
    /// a task you had just stopped had completed. Cancellation is only visible in the steps.
    /// </summary>
    [AvaloniaFact]
    public async Task InitializeAsync_shows_a_cancelled_task_as_cancelled_and_not_as_finished()
    {
        var configFilePath = NewConfigFilePath();
        var executionId = new ExecutionId("a-1");
        var roomDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
                new FlowEvent.ExecutionCancelled(executionId),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            await new LocalUiConfigurationStore(configFilePath)
                .RecordOpenedAsync(roomDirectory, TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(configFilePath));
            await window.InitializeAsync(TestContext.Current.CancellationToken);

            var card = Assert.Single(window.ViewModel.Home.RoomCards);

            Assert.Equal(RoomCardStatus.Cancelled, card.Status);
            Assert.Equal("Cancelled", card.StatusText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>A task paused at critic, with a durable output file for the inbox preview.</summary>
    private static async Task<string> CreatePausedRoomDirectoryAsync(string reviewContent, CancellationToken cancellationToken)
    {
        var architectExecutionId = new ExecutionId("a-1");
        var criticExecutionId = new ExecutionId("c-1");
        var roomDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
                new FlowEvent.ExecutionSucceeded(architectExecutionId),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
                new FlowEvent.ExecutionSucceeded(criticExecutionId),
                new FlowEvent.WorkflowPaused(criticExecutionId, Critic),
            ],
            cancellationToken);

        var outputDirectory = Path.Combine(roomDirectory, "artifacts", "execution_c-1");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "review.md"), reviewContent, cancellationToken);
        return roomDirectory;
    }

    /// <summary>A task paused at a NeedsInput pause point (#334) — the shape an interactive session settles into: "your turn to reply", not an approval gate.</summary>
    private static async Task<string> CreateNeedsInputRoomDirectoryAsync(string replyContent, CancellationToken cancellationToken)
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
        var roomDirectory = await CreateRoomDirectoryAsync(
            snapshot,
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
                new FlowEvent.ExecutionSucceeded(architectExecutionId),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
                new FlowEvent.ExecutionSucceeded(criticExecutionId),
                new FlowEvent.WorkflowPaused(criticExecutionId, Critic),
            ],
            cancellationToken);

        var outputDirectory = Path.Combine(roomDirectory, "artifacts", "execution_c-1");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "review.md"), replyContent, cancellationToken);
        return roomDirectory;
    }

    /// <summary>
    /// #616: pins the summary sentence — why cancelled and failed runs sit in neither count is
    /// documented once, at <see cref="HomeViewModel"/>'s counting switch. This fixture read
    /// "2 finished" before that switch keyed on the card's derivation.
    /// </summary>
    [AvaloniaFact]
    public async Task InitializeAsync_does_not_count_a_cancelled_task_as_finished_in_the_summary()
    {
        var configFilePath = NewConfigFilePath();
        var finishedId = new ExecutionId("f-1");
        var finishedCriticId = new ExecutionId("f-2");
        var cancelledId = new ExecutionId("c-1");
        var finishedDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(finishedId, Architect)),
                new FlowEvent.ExecutionSucceeded(finishedId),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(finishedCriticId, Critic)),
                new FlowEvent.ExecutionSucceeded(finishedCriticId),
            ],
            TestContext.Current.CancellationToken);
        var cancelledDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(cancelledId, Architect)),
                new FlowEvent.ExecutionCancelled(cancelledId),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var store = new LocalUiConfigurationStore(configFilePath);
            await store.RecordOpenedAsync(finishedDirectory, TestContext.Current.CancellationToken);
            await store.RecordOpenedAsync(cancelledDirectory, TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(configFilePath));
            await window.InitializeAsync(TestContext.Current.CancellationToken);

            Assert.Equal(
                "Nothing is waiting on you — 0 working, 1 finished.",
                window.ViewModel.Home.InboxSummaryText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(finishedDirectory);
            DirectoryCleanup.DeleteRecursively(cancelledDirectory);
        }
    }

    [AvaloniaFact]
    public async Task InitializeAsync_starts_on_the_home_section()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        await window.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ShellSection.Home, window.ViewModel.CurrentSection);
        Assert.True(window.ViewModel.IsHomeVisible);
        Assert.False(window.ViewModel.IsRoomVisible);
        Assert.Equal("Nothing is waiting on you.", window.ViewModel.Home.InboxSummaryText);
    }

    [AvaloniaFact]
    public async Task LandOnTopRoom_opens_the_top_room_instead_of_staying_on_home()
    {
        // Rooms-as-root (#1055, 02-screens.md "Both surfaces open on rooms"): with a room in the
        // switcher, startup lands in the work, not the Home dashboard. The fleet is seeded directly
        // because GetFleetAsync is daemon-only (RoomClient.Fleet.cs) and no daemon runs headless — the
        // real daemon population is covered by DaemonIntegrationTests. The directory is real so
        // OpenAsync can load it, exactly as OpenAsync_navigates_to_the_task_section does.
        var executionId = new ExecutionId("a-1");
        var roomDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
                new FlowEvent.ExecutionSucceeded(executionId),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            window.ViewModel.Rooms.AddTestItem(new RoomFleetItem(
                roomDirectory, FriendlyName: roomDirectory, TypeLabel: "solo-run-template",
                StatusText: "Idle", PausedStepCount: 0, IsArchived: false,
                Created: DateTimeOffset.UnixEpoch, Updated: DateTimeOffset.UnixEpoch));

            await window.LandOnTopRoomAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ShellSection.Task, window.ViewModel.CurrentSection);
            Assert.True(window.ViewModel.IsRoomVisible);
            Assert.False(window.ViewModel.IsHomeVisible);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task LandOnTopRoom_stays_on_home_when_the_fleet_is_empty()
    {
        // An empty fleet must leave the landing exactly as it was — the no-rooms first-run ("Point
        // Baton at a folder") is a later slice and is J8's outcome, not this one's.
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));

        await window.LandOnTopRoomAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ShellSection.Home, window.ViewModel.CurrentSection);
        Assert.True(window.ViewModel.IsHomeVisible);
    }

    [AvaloniaFact]
    public async Task OpenAsync_navigates_to_the_task_section()
    {
        var executionId = new ExecutionId("a-1");
        var roomDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
                new FlowEvent.ExecutionSucceeded(executionId),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            Assert.Equal(ShellSection.Task, window.ViewModel.CurrentSection);
            Assert.True(window.ViewModel.IsRoomVisible);
            Assert.False(window.ViewModel.IsHomeVisible);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>M24 Phase 1 desktop chat UI (issue #262): opening a directory that materialized an interactive session (.aer/session.json present) routes to the dedicated Chat view instead of the generic Task view — see <c>MainWindow.OpenAsync</c>'s remarks.</summary>
    [AvaloniaFact]
    public async Task OpenAsync_routes_an_interactive_session_directory_to_the_chat_section()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-shell-chat-{Guid.NewGuid():N}");
        try
        {
            var metadata = await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                sessionId: "sess-nav-test",
                roomDirectoryPath: roomDirectory,
                adapter: "claude",
                cancellationToken: TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            Assert.Equal(ShellSection.Chat, window.ViewModel.CurrentSection);
            Assert.True(window.ViewModel.IsChatVisible);
            Assert.False(window.ViewModel.IsRoomVisible);
            Assert.Equal(metadata.SessionId, window.ViewModel.Chat.SessionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task Enter_in_the_composer_sends_but_shift_enter_does_not()
    {
        // The composer's send rule wired end-to-end, not just IsSendKeystroke in isolation: the KeyDown
        // handler is actually attached to the composer. A bare Enter runs SendChatMessageAsync, whose
        // synchronous BeginSend clears the input; Shift+Enter must not send, so the text survives.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-shell-composer-{Guid.NewGuid():N}");
        try
        {
            await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                sessionId: "sess-composer-test", roomDirectoryPath: roomDirectory, adapter: "claude",
                cancellationToken: TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.Equal(ShellSection.Chat, window.ViewModel.CurrentSection);

            // Shift+Enter is a newline, not a send — the composer text survives.
            window.ViewModel.Chat.InputText = "keep me";
            window.ChatInputBox.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Enter,
                KeyModifiers = KeyModifiers.Shift,
            });
            Assert.Equal("keep me", window.ViewModel.Chat.InputText);

            // A bare Enter sends — SendChatMessageAsync's synchronous BeginSend clears the input.
            window.ViewModel.Chat.InputText = "send me";
            window.ChatInputBox.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Enter,
                KeyModifiers = KeyModifiers.None,
            });
            Assert.Equal(string.Empty, window.ViewModel.Chat.InputText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task InitializeAsync_surfaces_a_paused_recent_as_an_inbox_item_with_its_artifact_preview()
    {
        var configFilePath = NewConfigFilePath();
        var roomDirectory = await CreatePausedRoomDirectoryAsync(
            "The plan looks solid overall.", TestContext.Current.CancellationToken);
        try
        {
            await new LocalUiConfigurationStore(configFilePath)
                .RecordOpenedAsync(roomDirectory, TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(configFilePath));
            await window.InitializeAsync(TestContext.Current.CancellationToken);

            var card = Assert.Single(window.ViewModel.Home.RoomCards);
            Assert.Equal(RoomCardStatus.NeedsYou, card.Status);
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
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task InitializeAsync_surfaces_a_needs_input_pause_as_a_reply_not_a_review()
    {
        // #334: the exact bug — a settled chat turn showed "Waiting for your review" and a [Review]
        // button. A NeedsInput pause must read as "your turn to reply" on every Home surface.
        var configFilePath = NewConfigFilePath();
        var roomDirectory = await CreateNeedsInputRoomDirectoryAsync("ok", TestContext.Current.CancellationToken);
        try
        {
            await new LocalUiConfigurationStore(configFilePath)
                .RecordOpenedAsync(roomDirectory, TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(configFilePath));
            await window.InitializeAsync(TestContext.Current.CancellationToken);

            var card = Assert.Single(window.ViewModel.Home.RoomCards);
            Assert.Equal(RoomCardStatus.NeedsYou, card.Status);
            Assert.Equal("Waiting for your reply", card.StatusText);

            var item = Assert.Single(window.ViewModel.Home.InboxItems);
            Assert.Equal(PausePointKind.NeedsInput, item.Kind);
            Assert.Equal("Waiting for your reply", item.StatusText);
            Assert.Equal("Reply", item.ActionLabel);
            Assert.Equal("1 room is waiting for your reply.", window.ViewModel.Home.InboxSummaryText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task Inbox_review_opens_the_task_and_navigates_to_the_task_section()
    {
        var configFilePath = NewConfigFilePath();
        var roomDirectory = await CreatePausedRoomDirectoryAsync(
            "Needs another pass at the error handling.", TestContext.Current.CancellationToken);
        try
        {
            await new LocalUiConfigurationStore(configFilePath)
                .RecordOpenedAsync(roomDirectory, TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(configFilePath));
            await window.InitializeAsync(TestContext.Current.CancellationToken);

            var item = Assert.Single(window.ViewModel.Home.InboxItems);
            await item.ReviewCommand.ExecuteAsync(null);

            Assert.Equal(ShellSection.Task, window.ViewModel.CurrentSection);
            Assert.Equal(
                Path.GetFullPath(roomDirectory), window.FindViewControl<TextBox>("RoomDirectoryPathBox")!.Text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>M24 Phase 5 (#278): the sixth nav destination — a fleet management view distinct from Home's capped recents cards.</summary>
    [AvaloniaFact]
    public async Task NavigatingToRooms_showsTheRoomsSectionAndHidesEverythingElse()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        await window.InitializeAsync(TestContext.Current.CancellationToken);

        window.ViewModel.CurrentSection = ShellSection.Rooms;

        Assert.True(window.ViewModel.IsRoomsVisible);
        Assert.False(window.ViewModel.IsHomeVisible);
        Assert.False(window.ViewModel.IsRoomVisible);
        Assert.False(window.ViewModel.IsChatVisible);
        Assert.False(window.ViewModel.IsRemoteVisible);
    }

    [AvaloniaFact]
    public async Task A_recent_that_no_longer_loads_renders_as_an_unavailable_card_not_an_error()
    {
        var configFilePath = NewConfigFilePath();
        var notARoomDirectory = Path.Combine(Path.GetTempPath(), $"ui-shell-stale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(notARoomDirectory);
        try
        {
            await new LocalUiConfigurationStore(configFilePath)
                .RecordOpenedAsync(notARoomDirectory, TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(configFilePath));
            await window.InitializeAsync(TestContext.Current.CancellationToken);

            var card = Assert.Single(window.ViewModel.Home.RoomCards);
            Assert.Equal(RoomCardStatus.Unavailable, card.Status);
            Assert.Equal("Not available — moved, deleted, or not a room", card.StatusText);
            Assert.Empty(window.ViewModel.Home.InboxItems);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(notARoomDirectory);
        }
    }

    /// <summary>
    /// docs/design/02-screens.md:58 — the rooms list header reads "Rooms + New", "+ New" starting a
    /// room. Guards that the switcher header actually carries a "+ New" affordance (the invisible-but-
    /// green failure class: a wired handler with no button to fire it), and that restructuring the
    /// header to add it did not drop the existing refresh affordance beside the "Rooms" label.
    /// </summary>
    [AvaloniaFact]
    public void The_switcher_header_carries_a_plus_new_affordance_beside_the_rooms_label()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));

        var newButton = window.FindControl<Button>("SwitcherNewButton");
        Assert.NotNull(newButton);
        var label = Assert.IsType<TextBlock>(newButton.Content);
        Assert.Equal("+ New", label.Text);

        // The refresh affordance the header already had must survive the "+ New" restructure.
        Assert.NotNull(window.FindControl<Button>("SwitcherRefreshButton"));
    }

    /// <summary>
    /// #1062: Home used to carry a "Start from template" button beside its "Rooms" heading that
    /// duplicated the empty-state card's identical button (both fired OnStartTemplateClick), so the
    /// empty state showed two stacked. With the switcher's "+ New" now the always-available new-room
    /// affordance (#1061), that header button is gone — the empty-state card's one remains.
    /// </summary>
    [AvaloniaFact]
    public void Home_no_longer_carries_a_duplicate_start_from_template_button_beside_its_heading()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));

        Assert.Null(window.FindViewControl<Button>("HeaderStartTemplateButton"));
        Assert.NotNull(window.FindViewControl<Button>("StartTemplateButton"));
    }
}
