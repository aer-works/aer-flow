using System.Collections.ObjectModel;
using Aer.Flow;
using Aer.Flow.Artifacts;
using Aer.Flow.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui.Core;

/// <summary>
/// Home's read model (M19 Phase 2, issue #187): the recent task directories as live status cards,
/// and the decision inbox — everything across those tasks currently waiting on the human, one item
/// per paused step, each leading with the artifact to review (information-architecture.md).
/// Rebuilt from durable contents on every refresh (§3.1, §11) with the same rebuild-from-scratch
/// discipline as every other projection surface — never reconciled.
/// <para>
/// <b>Inbox scan-scope decision of record (the phase's named open question):</b> the inbox scans
/// <em>all</em> recent task directories, not just the open task — Home exists precisely for the
/// moment no task is open yet, and an inbox that only knew about the open task would be empty
/// exactly when it matters most. The scan is bounded by the recents list the store already caps,
/// and it refreshes on Home activation plus the poller's tick while an open task is being
/// observed — not on its own timer.
/// </para>
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private const int InboxPreviewMaxLength = 400;

    public ObservableCollection<TaskCardViewModel> TaskCards { get; } = [];
    public ObservableCollection<InboxItemViewModel> InboxItems { get; } = [];

    /// <summary>The inbox's one-line summary — the honest empty state ("empty" must not read as "broken": running/finished counts say why nothing is waiting).</summary>
    [ObservableProperty]
    private string inboxSummaryText = "Nothing is waiting on you.";

    /// <summary>True when there is no task history at all — Home's empty state says what to do next (M19 Phase 5, #190) instead of showing a blank page.</summary>
    [ObservableProperty]
    private bool hasNoTasks = true;

    /// <summary>
    /// Rebuilds cards and inbox from the recents list. A listed directory that no longer loads is
    /// stale list state (§3) — skipped, never surfaced as an error; it simply has no card this
    /// refresh.
    /// </summary>
    public async Task RefreshAsync(
        TaskSession session, Func<string, Task> openTaskAsync, CancellationToken cancellationToken = default)
    {
        var recents = await session.LoadRecentTaskDirectoriesAsync(cancellationToken).ConfigureAwait(true);

        TaskCards.Clear();
        InboxItems.Clear();

        var runningCount = 0;
        var finishedCount = 0;

        foreach (var taskDirectoryPath in recents)
        {
            TaskProjection projection;
            try
            {
                projection = await TaskProjectionLoader.LoadAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);
            }
            catch (AerFlowException)
            {
                // §3's stale-list rule: reflected as a greyed card, never an error — the entry
                // stays visible (the user recorded it; hiding it silently would misreport their
                // own history) but carries no inbox items and no live status.
                TaskCards.Add(new TaskCardViewModel(
                    taskDirectoryPath,
                    TaskCardViewModel.TitleFor(taskDirectoryPath),
                    "Not available — moved, deleted, or not a task",
                    TaskCardStatus.Unavailable,
                    openTaskAsync));
                continue;
            }

            var card = TaskCardViewModel.FromProjection(taskDirectoryPath, projection, openTaskAsync);
            TaskCards.Add(card);

            switch (projection.State.Status)
            {
                case WorkflowStatus.Running:
                    runningCount++;
                    break;
                case WorkflowStatus.Terminal:
                    finishedCount++;
                    break;
                case WorkflowStatus.Paused:
                    foreach (var stepState in projection.State.Steps)
                    {
                        if (stepState.Status == StepStatus.Paused)
                        {
                            InboxItems.Add(BuildInboxItem(taskDirectoryPath, projection, stepState, openTaskAsync));
                        }
                    }

                    break;
            }
        }

        HasNoTasks = TaskCards.Count == 0;
        InboxSummaryText = InboxItems.Count switch
        {
            0 when TaskCards.Count == 0 => "Nothing is waiting on you.",
            0 => $"Nothing is waiting on you — {runningCount} working, {finishedCount} finished.",
            1 => "1 step is waiting for your review.",
            var count => $"{count} steps are waiting for your review.",
        };
    }

    private static InboxItemViewModel BuildInboxItem(
        string taskDirectoryPath, TaskProjection projection, StepState stepState, Func<string, Task> openTaskAsync)
    {
        // Lead with the thing to review (ux-principles §4): the paused execution's first durable
        // output, previewed inline. Best-effort by design — a pause with no readable output still
        // renders an honest item, just without a preview.
        var previewText = string.Empty;
        var previewFileName = string.Empty;

        if (stepState.LatestExecutionId is { } executionId)
        {
            var execution = projection.Lineage.Executions.FirstOrDefault(e => e.ExecutionId == executionId);
            if (execution is { OutputFiles.Count: > 0 })
            {
                previewFileName = execution.OutputFiles[0];
                var outputDirectory = ArtifactManager.ResolveOutputDirectory(
                    Path.Combine(taskDirectoryPath, "artifacts"), executionId);
                try
                {
                    var content = File.ReadAllText(Path.Combine(outputDirectory, previewFileName));
                    previewText = content.Length > InboxPreviewMaxLength
                        ? content[..InboxPreviewMaxLength] + "…"
                        : content;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    previewText = string.Empty;
                }
            }
        }

        return new InboxItemViewModel(
            taskDirectoryPath,
            TaskCardViewModel.TitleFor(taskDirectoryPath),
            stepState.StepId.Value,
            previewFileName.Length > 0
                ? $"Waiting for your review — {previewFileName} ready"
                : "Waiting for your review",
            previewText,
            openTaskAsync);
    }
}

/// <summary>One recent task as a live status card — the recents list re-projected as Home's primary surface. Plain-language status per ux-principles.md's vocabulary map, with the precise engine state one disclosure away (the Task view).</summary>
public sealed partial class TaskCardViewModel(
    string taskDirectoryPath, string title, string statusText, TaskCardStatus status, Func<string, Task> openTaskAsync)
{
    public string TaskDirectoryPath { get; } = taskDirectoryPath;
    public string Title { get; } = title;
    public string StatusText { get; } = statusText;
    public TaskCardStatus Status { get; } = status;

    /// <summary>Style hooks for the one status system (design-language.md): exactly one of these is true, consumed by the card's classes.</summary>
    public bool IsNeedsYou => Status == TaskCardStatus.NeedsYou;

    [RelayCommand]
    private Task Open() => openTaskAsync(TaskDirectoryPath);

    /// <summary>The card title is the task directory's leaf name — the human's handle for the task, with the full path detail-on-demand (ux-principles §3).</summary>
    public static string TitleFor(string taskDirectoryPath)
        => Path.GetFileName(Path.TrimEndingDirectorySeparator(taskDirectoryPath));

    public static TaskCardViewModel FromProjection(
        string taskDirectoryPath, TaskProjection projection, Func<string, Task> openTaskAsync)
    {
        var (statusText, status) = projection.State.Status switch
        {
            WorkflowStatus.Paused => ("Waiting for your review", TaskCardStatus.NeedsYou),
            WorkflowStatus.Running when projection.State.Steps.FirstOrDefault(s => s.Status == StepStatus.Running) is { } runningStep
                => ($"Working — {runningStep.StepId.Value}", TaskCardStatus.Running),
            WorkflowStatus.Running => ("Working", TaskCardStatus.Running),
            _ when projection.State.Steps.Any(s => s.Status is StepStatus.Failed or StepStatus.Rejected)
                => ("Failed", TaskCardStatus.Failed),
            _ => ("Finished", TaskCardStatus.Finished),
        };

        return new TaskCardViewModel(taskDirectoryPath, TitleFor(taskDirectoryPath), statusText, status, openTaskAsync);
    }
}

/// <summary>The one status system's card-level states — carried as data so the skin styles them consistently (color + icon + word, never color alone).</summary>
public enum TaskCardStatus
{
    Running,
    NeedsYou,
    Finished,
    Failed,

    /// <summary>§3's stale list state: recorded in Local UI Configuration but no longer loadable — greyed, never an error.</summary>
    Unavailable,
}

/// <summary>
/// One paused step across the recent tasks, as a decision-inbox item: the plain status, the
/// artifact preview beside it, and Review — which opens the task at its decision surface, the
/// same mutation path as deciding anywhere else (the inbox is a projection, never a second
/// authority).
/// </summary>
public sealed partial class InboxItemViewModel(
    string taskDirectoryPath, string taskTitle, string stepName, string statusText, string previewText, Func<string, Task> openTaskAsync)
{
    public string TaskDirectoryPath { get; } = taskDirectoryPath;
    public string TaskTitle { get; } = taskTitle;
    public string StepName { get; } = stepName;
    public string StatusText { get; } = statusText;
    public string PreviewText { get; } = previewText;
    public bool HasPreview => PreviewText.Length > 0;

    [RelayCommand]
    private Task Review() => openTaskAsync(TaskDirectoryPath);
}
