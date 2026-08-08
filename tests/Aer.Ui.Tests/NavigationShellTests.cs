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
    /// no cancelled workflow status — so the status derivation fell through to "Finished" and told you
    /// a task you had just stopped had completed. Cancellation is only visible in the steps. The
    /// derivation is <see cref="RoomCardViewModel.DeriveStatus"/>, shared by the switcher and the fleet
    /// loader (#1071 retired the Home cards this used to read it through).
    /// </summary>
    [Fact]
    public async Task A_cancelled_task_derives_as_cancelled_and_not_as_finished()
    {
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
            var projection = await RoomProjectionLoader.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var (statusText, status) = RoomCardViewModel.DeriveStatus(projection);
            Assert.Equal(RoomCardStatus.Cancelled, status);
            Assert.Equal("Cancelled", statusText);
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


    [AvaloniaFact]
    public async Task InitializeAsync_starts_on_the_home_section()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        await window.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ShellSection.Home, window.ViewModel.CurrentSection);
        Assert.True(window.ViewModel.IsHomeVisible);
        Assert.False(window.ViewModel.IsRoomVisible);
        // #1071: a bare launch lands on the ▤ front door's first-run surface, with no room open.
        Assert.True(window.ViewModel.Home.HasNoRooms);
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

    // #1072: the Home decision inbox relocated to the switcher's "needs you" filter, which renders each
    // paused step from the same HomeViewModel.BuildInboxItem derivation the inbox used (and the same
    // RoomCardViewModel.DeriveStatus for the row status). These verify that derivation directly — what
    // the filter draws — from the paused-room fixtures the inbox tests used. The switcher's per-row load
    // and the filter narrowing are covered by RoomsViewModelNeedsYouFilterTests.
    [Fact]
    public async Task A_paused_review_derives_a_needs_you_status_and_an_inbox_item_with_its_preview()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync(
            "The plan looks solid overall.", TestContext.Current.CancellationToken);
        try
        {
            var projection = await RoomProjectionLoader.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var (statusText, status) = RoomCardViewModel.DeriveStatus(projection);
            Assert.Equal(RoomCardStatus.NeedsYou, status);
            Assert.Equal("Waiting for your review", statusText);

            var pausedStep = projection.State.Steps.Single(s => s.Status == StepStatus.Paused);
            var item = HomeViewModel.BuildInboxItem(roomDirectory, projection, pausedStep, _ => Task.CompletedTask);
            Assert.Equal("critic", item.StepName);
            Assert.Equal("Waiting for your review — review.md ready", item.StatusText);
            Assert.True(item.HasPreview);
            Assert.Equal("The plan looks solid overall.", item.PreviewText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_needs_input_pause_derives_a_reply_not_a_review()
    {
        // #334: the exact bug — a settled chat turn showed "Waiting for your review" and a [Review]
        // button. A NeedsInput pause must read as "your turn to reply" wherever the derivation renders.
        var roomDirectory = await CreateNeedsInputRoomDirectoryAsync("ok", TestContext.Current.CancellationToken);
        try
        {
            var projection = await RoomProjectionLoader.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var (statusText, status) = RoomCardViewModel.DeriveStatus(projection);
            Assert.Equal(RoomCardStatus.NeedsYou, status);
            Assert.Equal("Waiting for your reply", statusText);

            var pausedStep = projection.State.Steps.Single(s => s.Status == StepStatus.Paused);
            var item = HomeViewModel.BuildInboxItem(roomDirectory, projection, pausedStep, _ => Task.CompletedTask);
            Assert.Equal(PausePointKind.NeedsInput, item.Kind);
            Assert.Equal("Waiting for your reply", item.StatusText);
            Assert.Equal("Reply", item.ActionLabel);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_paused_step_item_opens_the_room_it_points_at()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync(
            "Needs another pass at the error handling.", TestContext.Current.CancellationToken);
        try
        {
            var projection = await RoomProjectionLoader.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);
            var pausedStep = projection.State.Steps.Single(s => s.Status == StepStatus.Paused);

            string? opened = null;
            var item = HomeViewModel.BuildInboxItem(
                roomDirectory, projection, pausedStep, path => { opened = path; return Task.CompletedTask; });
            await item.ReviewCommand.ExecuteAsync(null);

            // Review opens the room the item points at — on the switcher that selects its row, whose
            // existing open path renders the gate inline (#1072/#336).
            Assert.Equal(roomDirectory, opened);
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
        Assert.False(window.ViewModel.IsSettingsVisible);
    }

    /// <summary>
    /// #1068: Settings is the former Remote destination. Navigating to it shows the Settings section
    /// (hiding everything else), and the pairing UI that used to be its own destination is folded in —
    /// reachable through the RemoteView embedded in SettingsView, so the fold didn't drop it.
    /// </summary>
    [AvaloniaFact]
    public async Task NavigatingToSettings_showsSettingsAndFoldsInTheRemotePairingSurface()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        await window.InitializeAsync(TestContext.Current.CancellationToken);

        window.ViewModel.CurrentSection = ShellSection.Settings;

        Assert.True(window.ViewModel.IsSettingsVisible);
        Assert.False(window.ViewModel.IsHomeVisible);
        Assert.False(window.ViewModel.IsRoomVisible);
        Assert.False(window.ViewModel.IsChatVisible);
        Assert.False(window.ViewModel.IsRoomsVisible);

        // The pairing controls survive the fold: they now resolve through SettingsView, not a
        // standalone Remote view. A null here means the fold dropped the surface.
        Assert.NotNull(window.RemoteToggleButton);
        Assert.NotNull(window.ThemeSystemButton);
    }

    /// <summary>
    /// #1068: choosing a theme in Settings → Appearance applies it to the running app, marks that
    /// choice selected on the toggle, and persists it so the next launch opens in it. Starts from the
    /// System default so the assertions discriminate a real change from a no-op.
    /// </summary>
    [AvaloniaFact]
    public async Task Choosing_a_theme_applies_it_marks_it_selected_and_persists_it()
    {
        var configFilePath = NewConfigFilePath();
        var window = new MainWindow(new LocalUiConfigurationStore(configFilePath));
        await window.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(window.ViewModel.IsThemeSystem);

        var original = Avalonia.Application.Current!.RequestedThemeVariant;
        try
        {
            await window.ChooseThemeAsync(ThemeNames.Dark);

            Assert.Equal(ThemeNames.Dark, window.ViewModel.ThemePreference);
            Assert.True(window.ViewModel.IsThemeDark);
            Assert.False(window.ViewModel.IsThemeSystem);
            Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, Avalonia.Application.Current!.RequestedThemeVariant);
            Assert.Equal(
                ThemeNames.Dark,
                await new LocalUiConfigurationStore(configFilePath).LoadThemeAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            // Theme is app-global; don't leak the change into other tests.
            Avalonia.Application.Current!.RequestedThemeVariant = original;
        }
    }

    // #1071 retired the Home recents cards (incl. the greyed "unavailable" rendering for a stale
    // recent); a room that no longer loads is the switcher fleet loader's concern now (daemon-driven),
    // covered by the fleet tests. Nothing Home-side left to assert here.
    [AvaloniaFact(Skip = "Retired with the Home recents cards (#1071); unavailable rendering moved to the switcher fleet loader.")]
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

            Assert.False(window.ViewModel.Home.HasNoRooms);
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
