using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui.Core;

/// <summary>
/// The Tasks view's state (M24 Phase 5, #278) — every known task/session directory, not just
/// Home's capped 10-item recents cards, with archive/unarchive/delete. Deliberately its own child
/// ViewModel rather than fields on <see cref="MainWindowViewModel"/> (the pattern <see cref="RemoteViewModel"/>/<see cref="ChatViewModel"/>
/// already establish) — a real fleet management surface is a distinct concern from the mutation/decision
/// surface <see cref="MainWindowViewModel"/> was introduced for.
/// </summary>
public sealed partial class TasksViewModel : ObservableObject
{
    [ObservableProperty]
    private bool includeArchived;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorText))]
    private string? errorText;

    /// <summary>
    /// How many of <see cref="Items"/> currently have <see cref="TaskFleetItemViewModel.IsSelected"/>
    /// set (bulk select, issue #288) — recomputed by <see cref="OnItemSelectionChanged"/> rather than
    /// tracked independently, since the source of truth is each row's own checkbox state.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(BulkDeleteConfirmText))]
    [NotifyCanExecuteChangedFor(nameof(RequestBulkDeleteCommand))]
    private int selectedCount;

    /// <summary>
    /// Bulk delete's own two-step confirm (issue #288) — the same in-place idiom
    /// <see cref="TaskFleetItemViewModel.IsConfirmingDelete"/> already uses for a single row, scaled
    /// to "Delete N tasks?" instead of one confirm per item.
    /// </summary>
    [ObservableProperty]
    private bool isConfirmingBulkDelete;

    public ObservableCollection<TaskFleetItemViewModel> Items { get; } = [];

    public bool HasNoItems => !IsBusy && Items.Count == 0;
    public bool HasErrorText => !string.IsNullOrEmpty(ErrorText);
    public bool HasSelection => SelectedCount > 0;

    public string BulkDeleteConfirmText =>
        $"Really delete {SelectedCount} selected task{(SelectedCount == 1 ? "" : "s")}? This can't be undone.";

    /// <summary>Re-fetches the fleet list (activation, after archive/unarchive/delete, and the "Show archived" toggle).</summary>
    public async Task RefreshAsync(TaskSession session, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorText = null;
        IsConfirmingBulkDelete = false;

        try
        {
            var (items, error) = await session.GetFleetAsync(IncludeArchived, cancellationToken).ConfigureAwait(true);
            if (items == null)
            {
                ErrorText = error ?? "Could not load tasks.";
                return;
            }

            Items.Clear();
            foreach (var item in items.OrderByDescending(i => i.FriendlyName))
            {
                Items.Add(new TaskFleetItemViewModel(
                    item,
                    i => ArchiveAsync(session, i, cancellationToken),
                    i => UnarchiveAsync(session, i, cancellationToken),
                    i => DeleteAsync(session, i, cancellationToken),
                    OnItemSelectionChanged));
            }
        }
        finally
        {
            IsBusy = false;
            OnItemSelectionChanged();
            OnPropertyChanged(nameof(HasNoItems));
        }
    }

    /// <summary>Every row's selection checkbox reports back through this (rather than <see cref="Items"/> itself being observed) — see <see cref="TaskFleetItemViewModel"/>'s own <c>selectionChanged</c> callback.</summary>
    private void OnItemSelectionChanged() => SelectedCount = Items.Count(i => i.IsSelected);

    /// <summary>
    /// Test seam (issue #288): adds a row to <see cref="Items"/> wired with the real
    /// selection-changed callback <see cref="RefreshAsync"/> itself uses, but no-op
    /// archive/unarchive/delete delegates — lets <c>TasksViewModelTests</c> exercise the actual
    /// selection bookkeeping (<see cref="SelectedCount"/>, <see cref="HasSelection"/>, the bulk-delete
    /// confirm gating) without constructing the sealed <see cref="TaskSession"/> that
    /// <see cref="RefreshAsync"/>'s real row construction needs. Same reasoning as
    /// <see cref="TaskSession.ShouldApplyProjectionPush"/>'s own internal test seam.
    /// </summary>
    internal TaskFleetItemViewModel AddTestItem(TaskFleetItem item)
    {
        var row = new TaskFleetItemViewModel(
            item, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, OnItemSelectionChanged);
        Items.Add(row);
        OnItemSelectionChanged();
        return row;
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items)
        {
            item.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RequestBulkDelete() => IsConfirmingBulkDelete = true;

    [RelayCommand]
    private void CancelBulkDelete() => IsConfirmingBulkDelete = false;

    /// <summary>
    /// Archives every selected, not-yet-archived row (issue #288) — the bulk counterpart of
    /// <see cref="ArchiveAsync"/>. Fans out sequentially against the same per-directory
    /// <c>/api/tasks/archive</c> endpoint (delete mutates the shared recents list and archive mutates
    /// the shared fleet index, so concurrent calls could race) rather than a new bulk daemon endpoint,
    /// per the issue's stated default. Calls <see cref="TaskSession.ArchiveTaskAsync"/> directly in the
    /// loop and refreshes exactly once at the end -- routing through the existing single-item
    /// <see cref="ArchiveAsync"/> would call <see cref="RefreshAsync"/> after every item, rebuilding
    /// <see cref="Items"/> (and clearing selection) mid-loop.
    /// </summary>
    public async Task BulkArchiveAsync(TaskSession session, CancellationToken cancellationToken = default)
    {
        var targets = Items.Where(i => i.IsSelected && !i.IsArchived).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        IsBusy = true;
        var failures = new List<string>();
        try
        {
            foreach (var item in targets)
            {
                var outcome = await session.ArchiveTaskAsync(item.TaskDirectoryPath, cancellationToken).ConfigureAwait(true);
                if (outcome.ErrorMessage != null)
                {
                    failures.Add($"{item.FriendlyName}: {outcome.ErrorMessage}");
                }
            }

            await RefreshAsync(session, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }

        // Set after RefreshAsync, which resets ErrorText to null on entry -- setting it before the
        // refresh would just be clobbered.
        if (failures.Count > 0)
        {
            ErrorText = $"{failures.Count} of {targets.Count} task(s) couldn't be archived: {string.Join("; ", failures)}";
        }
    }

    /// <summary>
    /// Deletes every selected row (issue #288) once <see cref="IsConfirmingBulkDelete"/>'s confirm has
    /// been accepted -- the bulk counterpart of <see cref="DeleteAsync"/>, with the same
    /// sequential-fan-out-then-single-refresh reasoning as <see cref="BulkArchiveAsync"/>.
    /// </summary>
    public async Task ConfirmBulkDeleteAsync(TaskSession session, CancellationToken cancellationToken = default)
    {
        var targets = Items.Where(i => i.IsSelected).ToList();
        if (targets.Count == 0)
        {
            IsConfirmingBulkDelete = false;
            return;
        }

        IsBusy = true;
        var failures = new List<string>();
        try
        {
            foreach (var item in targets)
            {
                var outcome = await session.DeleteTaskAsync(item.TaskDirectoryPath, cancellationToken).ConfigureAwait(true);
                if (outcome.ErrorMessage != null)
                {
                    failures.Add($"{item.FriendlyName}: {outcome.ErrorMessage}");
                }
            }

            await RefreshAsync(session, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }

        if (failures.Count > 0)
        {
            ErrorText = $"{failures.Count} of {targets.Count} task(s) couldn't be deleted: {string.Join("; ", failures)}";
        }
    }

    private async Task ArchiveAsync(TaskSession session, TaskFleetItemViewModel item, CancellationToken cancellationToken)
    {
        var outcome = await session.ArchiveTaskAsync(item.TaskDirectoryPath, cancellationToken).ConfigureAwait(true);
        if (outcome.ErrorMessage != null)
        {
            item.RowErrorText = outcome.ErrorMessage;
            return;
        }

        await RefreshAsync(session, cancellationToken).ConfigureAwait(true);
    }

    private async Task UnarchiveAsync(TaskSession session, TaskFleetItemViewModel item, CancellationToken cancellationToken)
    {
        var outcome = await session.UnarchiveTaskAsync(item.TaskDirectoryPath, cancellationToken).ConfigureAwait(true);
        if (outcome.ErrorMessage != null)
        {
            item.RowErrorText = outcome.ErrorMessage;
            return;
        }

        await RefreshAsync(session, cancellationToken).ConfigureAwait(true);
    }

    private async Task DeleteAsync(TaskSession session, TaskFleetItemViewModel item, CancellationToken cancellationToken)
    {
        var outcome = await session.DeleteTaskAsync(item.TaskDirectoryPath, cancellationToken).ConfigureAwait(true);
        if (outcome.ErrorMessage != null)
        {
            item.IsConfirmingDelete = false;
            item.RowErrorText = outcome.ErrorMessage;
            return;
        }

        await RefreshAsync(session, cancellationToken).ConfigureAwait(true);
    }
}

/// <summary>
/// One row in the Tasks view (M24 Phase 5, #278) — same closure-over-parent-actions shape as
/// <see cref="PairedClientItemViewModel"/>: the parent <see cref="TasksViewModel"/> already has the
/// <see cref="TaskSession"/> this row's actions need, so each action closes over it at construction
/// rather than the row needing its own reference. Delete uses an inline two-step confirm
/// (<see cref="IsConfirmingDelete"/>) rather than a modal dialog — no modal-dialog precedent exists
/// anywhere in this codebase's Avalonia views (<see cref="TemplatePickerWindow"/>'s in-window
/// <c>ErrorText</c> is the closest thing, and this follows the same in-place idiom).
/// </summary>
public sealed partial class TaskFleetItemViewModel : ObservableObject
{
    private readonly Func<TaskFleetItemViewModel, Task> _archiveAsync;
    private readonly Func<TaskFleetItemViewModel, Task> _unarchiveAsync;
    private readonly Func<TaskFleetItemViewModel, Task> _deleteAsync;
    private readonly Action? _selectionChanged;

    public TaskFleetItemViewModel(
        TaskFleetItem item,
        Func<TaskFleetItemViewModel, Task> archiveAsync,
        Func<TaskFleetItemViewModel, Task> unarchiveAsync,
        Func<TaskFleetItemViewModel, Task> deleteAsync,
        Action? selectionChanged = null)
    {
        TaskDirectoryPath = item.TaskDirectoryPath;
        FriendlyName = item.FriendlyName;
        TypeLabel = item.TypeLabel;
        StatusText = item.StatusText;
        PausedStepCount = item.PausedStepCount;
        IsArchived = item.IsArchived;
        _archiveAsync = archiveAsync;
        _unarchiveAsync = unarchiveAsync;
        _deleteAsync = deleteAsync;
        _selectionChanged = selectionChanged;
    }

    public string TaskDirectoryPath { get; }
    public string FriendlyName { get; }
    public string TypeLabel { get; }
    public string StatusText { get; }
    public int PausedStepCount { get; }
    public bool IsArchived { get; }
    public bool HasPausedSteps => PausedStepCount > 0;

    /// <summary>Bulk select (issue #288) — this row's own checkbox state; <see cref="TasksViewModel.SelectedCount"/> is recomputed from every row's value whenever any one of them changes.</summary>
    [ObservableProperty]
    private bool isSelected;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged?.Invoke();

    [ObservableProperty]
    private bool isConfirmingDelete;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRowErrorText))]
    private string? rowErrorText;

    public bool HasRowErrorText => !string.IsNullOrEmpty(RowErrorText);

    [RelayCommand]
    private Task Archive() => _archiveAsync(this);

    [RelayCommand]
    private Task Unarchive() => _unarchiveAsync(this);

    [RelayCommand]
    private void RequestDelete() => IsConfirmingDelete = true;

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private Task ConfirmDelete() => _deleteAsync(this);
}
