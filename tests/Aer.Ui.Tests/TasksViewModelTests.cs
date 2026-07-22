using Aer.Ui.Core;

namespace Aer.Ui.Tests;

/// <summary>
/// Bulk select (issue #288) — the ViewModel-layer unit-test level for <see cref="TasksViewModel"/>
/// and <see cref="TaskFleetItemViewModel"/>'s selection bookkeeping, mirroring
/// <see cref="PausedStepViewModelTests"/>'s "plain unit test, no headless Avalonia session, no live
/// daemon" approach. There was no pre-existing <c>TasksViewModelTests</c> file (the issue's
/// description of one is stale) — this is the first ViewModel-level coverage for
/// <see cref="TasksViewModel"/>; the fan-out/refresh mutation surface itself is covered at the
/// endpoint level by <c>DaemonIntegrationTests</c>' single-item archive/unarchive/delete round trip,
/// the same level the pre-existing single-item actions were already tested at.
/// </summary>
public class TasksViewModelTests
{
    private static TaskFleetItem NewItem(string path, bool isArchived = false) =>
        new(path, FriendlyName: path, TypeLabel: "solo-run-template", StatusText: "Idle", PausedStepCount: 0, IsArchived: isArchived);

    [Fact]
    public void A_freshly_constructed_TasksViewModel_has_no_selection()
    {
        var viewModel = new TasksViewModel();

        Assert.Equal(0, viewModel.SelectedCount);
        Assert.False(viewModel.HasSelection);
    }

    [Fact]
    public void Selecting_a_row_updates_the_parents_SelectedCount_and_HasSelection()
    {
        var viewModel = new TasksViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));

        row.IsSelected = true;

        Assert.Equal(1, viewModel.SelectedCount);
        Assert.True(viewModel.HasSelection);
    }

    [Fact]
    public void Deselecting_a_row_decrements_SelectedCount_back_to_zero()
    {
        var viewModel = new TasksViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));
        row.IsSelected = true;

        row.IsSelected = false;

        Assert.Equal(0, viewModel.SelectedCount);
        Assert.False(viewModel.HasSelection);
    }

    [Fact]
    public void SelectedCount_reflects_however_many_of_several_rows_are_selected()
    {
        var viewModel = new TasksViewModel();
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
        var viewModel = new TasksViewModel();
        viewModel.AddTestItem(NewItem("/tasks/a"));
        viewModel.AddTestItem(NewItem("/tasks/b"));

        viewModel.SelectAllCommand.Execute(null);

        Assert.Equal(2, viewModel.SelectedCount);
        Assert.All(viewModel.Items, item => Assert.True(item.IsSelected));
    }

    [Fact]
    public void ClearSelectionCommand_deselects_every_row()
    {
        var viewModel = new TasksViewModel();
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
        var viewModel = new TasksViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));

        Assert.False(viewModel.RequestBulkDeleteCommand.CanExecute(null));

        row.IsSelected = true;

        Assert.True(viewModel.RequestBulkDeleteCommand.CanExecute(null));
    }

    [Fact]
    public void RequestBulkDeleteCommand_sets_IsConfirmingBulkDelete()
    {
        var viewModel = new TasksViewModel();
        var row = viewModel.AddTestItem(NewItem("/tasks/a"));
        row.IsSelected = true;

        viewModel.RequestBulkDeleteCommand.Execute(null);

        Assert.True(viewModel.IsConfirmingBulkDelete);
    }

    [Fact]
    public void CancelBulkDeleteCommand_clears_the_confirm_without_touching_the_selection()
    {
        var viewModel = new TasksViewModel();
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
        var viewModel = new TasksViewModel();
        var a = viewModel.AddTestItem(NewItem("/tasks/a"));
        var b = viewModel.AddTestItem(NewItem("/tasks/b"));

        a.IsSelected = true;
        Assert.Equal("Really delete 1 selected task? This can't be undone.", viewModel.BulkDeleteConfirmText);

        b.IsSelected = true;
        Assert.Equal("Really delete 2 selected tasks? This can't be undone.", viewModel.BulkDeleteConfirmText);
    }
}
