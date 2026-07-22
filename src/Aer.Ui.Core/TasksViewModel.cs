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

    public ObservableCollection<TaskFleetItemViewModel> Items { get; } = [];

    public bool HasNoItems => !IsBusy && Items.Count == 0;
    public bool HasErrorText => !string.IsNullOrEmpty(ErrorText);

    /// <summary>Re-fetches the fleet list (activation, after archive/unarchive/delete, and the "Show archived" toggle).</summary>
    public async Task RefreshAsync(TaskSession session, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorText = null;

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
                    i => DeleteAsync(session, i, cancellationToken)));
            }
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasNoItems));
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

    public TaskFleetItemViewModel(
        TaskFleetItem item,
        Func<TaskFleetItemViewModel, Task> archiveAsync,
        Func<TaskFleetItemViewModel, Task> unarchiveAsync,
        Func<TaskFleetItemViewModel, Task> deleteAsync)
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
    }

    public string TaskDirectoryPath { get; }
    public string FriendlyName { get; }
    public string TypeLabel { get; }
    public string StatusText { get; }
    public int PausedStepCount { get; }
    public bool IsArchived { get; }
    public bool HasPausedSteps => PausedStepCount > 0;

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
