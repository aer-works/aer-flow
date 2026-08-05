using System.Collections.ObjectModel;
using System.Linq;
using Aer.Adapters;
using Aer.Flow.Domain;
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

    /// <summary>
    /// The switcher's current row (#336) — which record the permanently-visible list has highlighted,
    /// and therefore what the detail pane is showing. Distinct from
    /// <see cref="TaskFleetItemViewModel.IsSelected"/>, which is bulk-select's checkbox: you can tick
    /// five rows for a bulk archive while looking at a sixth, so "checked" and "open" are genuinely
    /// two different things and share no state.
    /// </summary>
    [ObservableProperty]
    private TaskFleetItemViewModel? currentItem;

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

            // A rebuild replaces every row object, so the open record has to be re-found by identity
            // afterwards (#336) — otherwise any refresh would silently deselect whatever the user is
            // looking at, which on a permanently-visible switcher is far more disruptive than it was
            // on a view you had to navigate to. Null if it was archived away or deleted meanwhile.
            var openDirectoryPath = CurrentItem?.TaskDirectoryPath;

            Items.Clear();
            foreach (var item in InFleetOrder(items))
            {
                Items.Add(new TaskFleetItemViewModel(
                    item,
                    i => ArchiveAsync(session, i, cancellationToken),
                    i => UnarchiveAsync(session, i, cancellationToken),
                    i => DeleteAsync(session, i, cancellationToken),
                    OnItemSelectionChanged));
            }

            CurrentItem = openDirectoryPath == null ? null : FindRow(openDirectoryPath);
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
    /// The fleet list's one ordering rule (#336): most recently updated first, ties broken by name so
    /// the order is *stable* rather than merely sorted — two sessions touched in the same second must
    /// not swap places on an unrelated refresh, which in a permanently-visible switcher would move a
    /// row out from under the pointer.
    /// </summary>
    /// <remarks>
    /// This previously ordered by <c>FriendlyName</c> descending, which silently discarded the
    /// recency order the daemon had already applied (<c>Aer.Daemon.Program</c>'s
    /// <c>OrderByDescending(i =&gt; i.Updated)</c>) and contradicted <see cref="TaskFleetItem.Updated"/>'s
    /// own contract ("the key the fleet list orders by"). The phone showed recency and the desktop
    /// showed reverse-alphabetical for the same fleet. Sorting here rather than trusting the
    /// transport keeps local (non-daemon) loads and push-updated rows in the same order as remote ones.
    /// </remarks>
    private static IEnumerable<TaskFleetItem> InFleetOrder(IEnumerable<TaskFleetItem> items) =>
        items.OrderByDescending(i => i.LastActivityAt ?? i.Updated).ThenBy(i => i.FriendlyName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Test seam for <see cref="InFleetOrder"/> — same reasoning as <see cref="AddTestItem"/>: the
    /// rule is worth asserting directly, and reaching it through <see cref="RefreshAsync"/> would
    /// need the sealed <see cref="TaskSession"/> and a live fleet fetch to test a pure sort.
    /// </summary>
    internal static IEnumerable<TaskFleetItem> InFleetOrderForTests(IEnumerable<TaskFleetItem> items) =>
        InFleetOrder(items);

    /// <summary>
    /// Applies one live projection push to the row it belongs to (#336). The switcher's list is
    /// permanently visible, so it can no longer rely on rebuilding itself when its section is
    /// activated — before this, <see cref="RefreshAsync"/> on activation was the *only* thing keeping
    /// it current, and making the list permanent removes that trigger.
    /// </summary>
    /// <remarks>
    /// Updates the existing row in place rather than going through <see cref="RefreshAsync"/>'s
    /// clear-and-rebuild: a rebuild on every frame would discard selection and scroll position, and
    /// would re-fetch the whole fleet to apply news the push already carried. A push for a directory
    /// with no row (a session created by another client since the last refresh) is ignored rather than
    /// synthesising a row — the push carries a projection, not the archived/created/updated fleet
    /// metadata a row needs, so a synthesised row would be wrong in exactly the fields the list sorts
    /// and filters on. Rows keyed by <see cref="AerPaths.RecordKey"/>, the shared normaliser from #335
    /// — two spellings of one directory must resolve to one row here for the same reason they must
    /// resolve to one lock there.
    /// </remarks>
    public void ApplyProjectionPush(string directoryPath, TaskProjection projection) =>
        FindRow(directoryPath)?.ApplyProjection(projection);

    /// <summary>
    /// The list's one row-identity rule (#336): a directory path resolves to at most one row, under
    /// #335's shared <see cref="AerPaths.RecordKey"/> normaliser. Two spellings of one directory must
    /// resolve to one row here for the same reason they must resolve to one lock there — #335's
    /// durable lesson was that the *second* primitive keyed on a record path is where normalisers
    /// drift apart, and this is that second primitive.
    /// </summary>
    private TaskFleetItemViewModel? FindRow(string directoryPath)
    {
        var key = AerPaths.RecordKey(directoryPath);
        return Items.FirstOrDefault(
            i => AerPaths.RecordKeyComparer.Equals(AerPaths.RecordKey(i.TaskDirectoryPath), key));
    }

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
        IsSession = item.IsSession;
        statusText = item.StatusText;
        pausedStepCount = item.PausedStepCount;
        IsArchived = item.IsArchived;
        LastActivityAt = item.LastActivityAt;
        _archiveAsync = archiveAsync;
        _unarchiveAsync = unarchiveAsync;
        _deleteAsync = deleteAsync;
        _selectionChanged = selectionChanged;
    }

    public string TaskDirectoryPath { get; }
    public string FriendlyName { get; }
    public string TypeLabel { get; }
    public DateTimeOffset? LastActivityAt { get; }

    /// <summary>
    /// Whether this row is an interactive session (chat-shaped) rather than a workflow (DAG-shaped)
    /// — what the switcher routes the detail pane on (#336). See <see cref="TaskFleetItem.IsSession"/>
    /// for why this is carried structurally instead of read back off <see cref="TypeLabel"/>.
    /// </summary>
    public bool IsSession { get; }

    public bool IsArchived { get; }

    /// <summary>
    /// Live under projection pushes (#336) — see <see cref="TasksViewModel.ApplyProjectionPush"/>.
    /// Observable rather than get-only because the switcher's list is permanently visible: it can no
    /// longer wait for a section activation to rebuild itself with a fresh value.
    /// </summary>
    [ObservableProperty]
    private string statusText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPausedSteps))]
    private int pausedStepCount;

    public bool HasPausedSteps => PausedStepCount > 0;

    /// <summary>
    /// Folds one live projection push into this row (#336), touching only what a projection actually
    /// knows: the workflow status and the paused-step count. Name, type, archived state and the
    /// timestamps are fleet metadata the push does not carry, so they are deliberately left alone
    /// rather than being guessed at from the projection.
    /// </summary>
    internal void ApplyProjection(TaskProjection projection)
    {
        var (statusText, status) = TaskCardViewModel.DeriveStatus(projection);
        StatusText = statusText;
        Status = status;
        PausedStepCount = projection.State.Steps.Count(s => s.Status == StepStatus.Paused);
    }

    /// <summary>
    /// This row's status as a mark-bearing state rather than a string (#461's vocabulary), so the
    /// switcher draws the same silhouette for the same state as Home's cards do — decision 0006's
    /// rule 2 is only worth anything if every surface honours it. Null until a projection has been
    /// seen for this row: the fleet list's own <see cref="TaskFleetItem.StatusText"/> is a bare
    /// <c>WorkflowStatus</c> name, which is deliberately *not* mapped to a mark here — guessing a
    /// state from a string is how the vocabulary drifts, and an unknown state must read as unknown.
    /// </summary>
    [ObservableProperty]
    private TaskCardStatus? status;

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
