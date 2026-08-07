using Aer.Flow.Domain;
using Aer.Flow.Templates;
using Aer.Ui.Core;

namespace Aer.Ui.Tests;

/// <summary>
/// Bulk select (issue #288) — the ViewModel-layer unit-test level for <see cref="RoomsViewModel"/>
/// and <see cref="RoomFleetItemViewModel"/>'s selection bookkeeping, mirroring
/// <see cref="PausedStepViewModelTests"/>'s "plain unit test, no headless Avalonia session, no live
/// daemon" approach. There was no pre-existing <c>RoomsViewModelTests</c> file (the issue's
/// description of one is stale) — this is the first ViewModel-level coverage for
/// <see cref="RoomsViewModel"/>; the fan-out/refresh mutation surface itself is covered at the
/// endpoint level by <c>DaemonIntegrationTests</c>' single-item archive/unarchive/delete round trip,
/// the same level the pre-existing single-item actions were already tested at.
/// </summary>
public class RoomsViewModelTests
{
    private static RoomFleetItem NewItem(string path, bool isArchived = false) =>
        new(path, FriendlyName: path, TypeLabel: "solo-run-template", StatusText: "Idle", PausedStepCount: 0,
            IsArchived: isArchived, Created: DateTimeOffset.UnixEpoch, Updated: DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_freshly_constructed_RoomsViewModel_has_no_selection()
    {
        var viewModel = new RoomsViewModel();

        Assert.Equal(0, viewModel.SelectedCount);
        Assert.False(viewModel.HasSelection);
    }

    [Fact]
    public void Selecting_a_row_updates_the_parents_SelectedCount_and_HasSelection()
    {
        var viewModel = new RoomsViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));

        row.IsSelected = true;

        Assert.Equal(1, viewModel.SelectedCount);
        Assert.True(viewModel.HasSelection);
    }

    [Fact]
    public void Deselecting_a_row_decrements_SelectedCount_back_to_zero()
    {
        var viewModel = new RoomsViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));
        row.IsSelected = true;

        row.IsSelected = false;

        Assert.Equal(0, viewModel.SelectedCount);
        Assert.False(viewModel.HasSelection);
    }

    [Fact]
    public void SelectedCount_reflects_however_many_of_several_rows_are_selected()
    {
        var viewModel = new RoomsViewModel();
        var a = viewModel.AddTestItem(NewItem("/tasks/a"));
        var b = viewModel.AddTestItem(NewItem("/tasks/b"));
        viewModel.AddTestItem(NewItem("/tasks/c"));

        a.IsSelected = true;
        b.IsSelected = true;

        Assert.Equal(2, viewModel.SelectedCount);
    }

    [Fact]
    public void SelectAllCommand_selects_every_row()
    {
        var viewModel = new RoomsViewModel();
        viewModel.AddTestItem(NewItem("/tasks/a"));
        viewModel.AddTestItem(NewItem("/tasks/b"));

        viewModel.SelectAllCommand.Execute(null);

        Assert.Equal(2, viewModel.SelectedCount);
        Assert.All(viewModel.Items, item => Assert.True(item.IsSelected));
    }

    [Fact]
    public void ClearSelectionCommand_deselects_every_row()
    {
        var viewModel = new RoomsViewModel();
        viewModel.AddTestItem(NewItem("/tasks/a"));
        viewModel.AddTestItem(NewItem("/tasks/b"));
        viewModel.SelectAllCommand.Execute(null);

        viewModel.ClearSelectionCommand.Execute(null);

        Assert.Equal(0, viewModel.SelectedCount);
        Assert.All(viewModel.Items, item => Assert.False(item.IsSelected));
    }

    [Fact]
    public void RequestBulkDeleteCommand_is_disabled_with_no_selection_and_enabled_once_something_is_selected()
    {
        var viewModel = new RoomsViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));

        Assert.False(viewModel.RequestBulkDeleteCommand.CanExecute(null));

        row.IsSelected = true;

        Assert.True(viewModel.RequestBulkDeleteCommand.CanExecute(null));
    }

    [Fact]
    public void RequestBulkDeleteCommand_sets_IsConfirmingBulkDelete()
    {
        var viewModel = new RoomsViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));
        row.IsSelected = true;

        viewModel.RequestBulkDeleteCommand.Execute(null);

        Assert.True(viewModel.IsConfirmingBulkDelete);
    }

    [Fact]
    public void CancelBulkDeleteCommand_clears_the_confirm_without_touching_the_selection()
    {
        var viewModel = new RoomsViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));
        row.IsSelected = true;
        viewModel.RequestBulkDeleteCommand.Execute(null);

        viewModel.CancelBulkDeleteCommand.Execute(null);

        Assert.False(viewModel.IsConfirmingBulkDelete);
        Assert.Equal(1, viewModel.SelectedCount);
        Assert.True(row.IsSelected);
    }

    [Fact]
    public void BulkDeleteConfirmText_pluralizes_the_count()
    {
        var viewModel = new RoomsViewModel();
        var a = viewModel.AddTestItem(NewItem("/tasks/a"));
        var b = viewModel.AddTestItem(NewItem("/tasks/b"));

        a.IsSelected = true;
        Assert.Equal("Really delete 1 selected room? This can't be undone.", viewModel.BulkDeleteConfirmText);

        b.IsSelected = true;
        Assert.Equal("Really delete 2 selected rooms? This can't be undone.", viewModel.BulkDeleteConfirmText);
    }

    // ---- #336: the switcher's push-driven liveness ----
    //
    // The switcher list is permanently visible, so it no longer gets a section activation to rebuild
    // on — before this, RoomsViewModel.RefreshAsync on activation was the *only* thing keeping it
    // current. These cover the replacement: a live projection push folded into the right row.

    private static RoomProjection ProjectionWith(WorkflowStatus status, params StepStatus[] stepStatuses)
    {
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("switcher-fixture"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(new StepId("only"), "worker", ["in"], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]));

        var steps = stepStatuses
            .Select((s, i) => new StepState(new StepId($"step-{i}"), s, LatestExecutionId: null, UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>()))
            .ToList();

        return new RoomProjection(
            snapshot,
            new FlowState(snapshot.WorkflowDefinitionSnapshotId, steps, status),
            new ExecutionHistory(new Dictionary<StepId, IReadOnlyList<ExecutionAttempt>>(), [], []),
            new ArtifactLineage([]));
    }

    [Fact]
    public void A_projection_push_updates_the_row_it_is_for()
    {
        var viewModel = new RoomsViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));
        Assert.Equal("Idle", row.StatusText);

        viewModel.ApplyProjectionPush("/tasks/a", ProjectionWith(WorkflowStatus.Terminal));

        Assert.Equal("Finished", row.StatusText);
        Assert.Equal(RoomCardStatus.Finished, row.Status);
    }

    [Fact]
    public void A_push_for_one_session_leaves_every_other_rows_status_alone()
    {
        var viewModel = new RoomsViewModel();
        var a = viewModel.AddTestItem(NewItem("/tasks/a"));
        var b = viewModel.AddTestItem(NewItem("/tasks/b"));

        viewModel.ApplyProjectionPush("/tasks/a", ProjectionWith(WorkflowStatus.Terminal));

        Assert.Equal("Finished", a.StatusText);
        Assert.Equal("Idle", b.StatusText);
        Assert.Null(b.Status);
    }

    [Fact]
    public void A_cancelled_session_reads_as_cancelled_on_the_switcher_too()
    {
        // #461 fixed "a cancelled task reports itself as Finished" on Home's cards. The switcher is a
        // second surface showing the same fact, so it shares Home's one derivation rather than
        // growing a copy that could drift back into the same defect.
        var viewModel = new RoomsViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));

        viewModel.ApplyProjectionPush(
            "/tasks/a", ProjectionWith(WorkflowStatus.Terminal, StepStatus.Cancelled));

        Assert.Equal("Cancelled", row.StatusText);
        Assert.Equal(RoomCardStatus.Cancelled, row.Status);
    }

    [Fact]
    public void A_push_carries_the_paused_step_count_the_row_shows()
    {
        var viewModel = new RoomsViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));
        Assert.False(row.HasPausedSteps);

        viewModel.ApplyProjectionPush(
            "/tasks/a", ProjectionWith(WorkflowStatus.Running, StepStatus.Paused, StepStatus.Paused, StepStatus.Succeeded));

        Assert.Equal(2, row.PausedStepCount);
        Assert.True(row.HasPausedSteps);
    }

    [Fact]
    public void A_push_for_a_directory_with_no_row_is_ignored_rather_than_synthesising_one()
    {
        var viewModel = new RoomsViewModel();
        viewModel.AddTestItem(NewItem("/tasks/a"));

        viewModel.ApplyProjectionPush("/tasks/never-seen", ProjectionWith(WorkflowStatus.Terminal));

        // A push carries a projection, not the archived/created/updated fleet metadata a row needs —
        // a synthesised row would be wrong in exactly the fields the list sorts and filters on.
        Assert.Single(viewModel.Items);
        Assert.Equal("Idle", viewModel.Items[0].StatusText);
    }

    [Fact]
    public void Two_spellings_of_one_directory_resolve_to_the_same_row()
    {
        // Built from Path rather than written as a literal: AerPaths.RecordKey runs
        // Path.GetFullPath, so a Windows-shaped literal ("C:\tasks\Alpha") is an absolute path on
        // Windows and a *relative* one on Linux — and '\' is not a separator there, so a trailing
        // one never gets trimmed. A hardcoded path would make this assert a different thing per OS.
        var viewModel = new RoomsViewModel();
        var directoryPath = Path.Combine(Path.GetTempPath(), "aer-switcher-key", "Alpha");
        var row = viewModel.AddTestItem(NewItem(directoryPath));

        // The two spellings that must collapse to one row: different casing, and a trailing
        // separator. #335's durable lesson is that the *second* primitive keyed on a record path is
        // where normalisers drift apart — this is that second primitive, so it shares RecordKey.
        var sameRecordSpeltDifferently = directoryPath.ToUpperInvariant() + Path.DirectorySeparatorChar;

        viewModel.ApplyProjectionPush(sameRecordSpeltDifferently, ProjectionWith(WorkflowStatus.Terminal));

        Assert.Equal("Finished", row.StatusText);
    }

    // ---- #336: ordering ----

    [Fact]
    public void The_list_orders_by_most_recent_last_activity_not_by_name()
    {
        // #640: recency means LAST ACTIVITY — when the room last did something (derived from journal events).
        var viewModel = new RoomsViewModel();
        var olderActivity = NewItem("/tasks/zulu") with
        {
            Updated = DateTimeOffset.UnixEpoch.AddHours(10),
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(1)
        };
        var newerActivity = NewItem("/tasks/alpha") with
        {
            Updated = DateTimeOffset.UnixEpoch.AddHours(2),
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(9)
        };

        var ordered = RoomsViewModel.InFleetOrderForTests([olderActivity, newerActivity]).ToList();

        Assert.Equal("/tasks/alpha", ordered[0].RoomDirectoryPath);
        Assert.Equal("/tasks/zulu", ordered[1].RoomDirectoryPath);
    }

    [Fact]
    public void Rows_with_same_last_activity_instant_order_by_name_so_the_list_is_stable()
    {
        // Ties must not resolve arbitrarily: on a permanently-visible switcher, a row that swaps
        // places on an unrelated refresh moves out from under the pointer.
        var viewModel = new RoomsViewModel();
        var sameInstant = DateTimeOffset.UnixEpoch.AddHours(3);
        var b = NewItem("/tasks/bravo") with { LastActivityAt = sameInstant, FriendlyName = "bravo" };
        var a = NewItem("/tasks/alpha") with { LastActivityAt = sameInstant, FriendlyName = "alpha" };

        var ordered = RoomsViewModel.InFleetOrderForTests([b, a]).ToList();

        Assert.Equal("alpha", ordered[0].FriendlyName);
        Assert.Equal("bravo", ordered[1].FriendlyName);
    }

    // ---- #1051: waiting-on-you first (J3), matching the phone ----

    [Fact]
    public void Rooms_that_need_you_sort_before_others_even_when_less_recently_active()
    {
        // Waiting-on-you is the PRIMARY key: a needs-you room outranks a more recently active room
        // that does not need you. Discriminates the needs-you key from the recency key beneath it.
        var needsYouButOlder = NewItem("/tasks/needs") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(1),
            Status = RoomCardStatus.NeedsYou,
        };
        var finishedButNewer = NewItem("/tasks/finished") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(9),
            Status = RoomCardStatus.Finished,
        };

        var ordered = RoomsViewModel.InFleetOrderForTests([finishedButNewer, needsYouButOlder]).ToList();

        Assert.Equal("/tasks/needs", ordered[0].RoomDirectoryPath);
        Assert.Equal("/tasks/finished", ordered[1].RoomDirectoryPath);
    }

    [Fact]
    public void Among_rooms_that_need_you_the_more_recently_active_still_comes_first()
    {
        // The needs-you key partitions; it does not flatten recency inside a partition.
        var olderNeedsYou = NewItem("/tasks/older") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(2),
            Status = RoomCardStatus.NeedsYou,
        };
        var newerNeedsYou = NewItem("/tasks/newer") with
        {
            LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(8),
            Status = RoomCardStatus.NeedsYou,
        };

        var ordered = RoomsViewModel.InFleetOrderForTests([olderNeedsYou, newerNeedsYou]).ToList();

        Assert.Equal("/tasks/newer", ordered[0].RoomDirectoryPath);
        Assert.Equal("/tasks/older", ordered[1].RoomDirectoryPath);
    }

    [Fact]
    public void A_row_seeds_its_mark_from_the_fleets_status_on_load_not_only_after_a_push()
    {
        // The switcher must draw the correct silhouette immediately; before #1051 the row's Status
        // was null until ApplyProjection fired on the first projection push.
        var viewModel = new RoomsViewModel();

        var row = viewModel.AddTestItem(NewItem("/tasks/needs") with { Status = RoomCardStatus.NeedsYou });

        Assert.Equal(RoomCardStatus.NeedsYou, row.Status);
    }

    // ---- #336: the detail router's discriminator ----

    [Fact]
    public void A_row_carries_whether_it_is_a_session_structurally_not_as_a_label()
    {
        // The switcher routes the detail pane on this. TypeLabel is a *display* string, so routing on
        // it would mean string-matching a rendered label.
        var viewModel = new RoomsViewModel();

        var session = viewModel.AddTestItem(NewItem("/tasks/chat") with { IsSession = true, TypeLabel = "interactive session" });
        var workflow = viewModel.AddTestItem(NewItem("/tasks/dag"));

        Assert.True(session.IsSession);
        Assert.False(workflow.IsSession);
    }
}
