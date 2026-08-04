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

            // #616: counted from the card's one derivation, not re-derived from the raw
            // WorkflowStatus — the raw switch counted every Terminal run as "finished", so a
            // failed or cancelled task inflated the summary's finished count (the counting form
            // of the Finished-for-Cancelled defect). Every member is named; Failed, Cancelled and
            // Unavailable are deliberately in neither count because the summary sentence doesn't
            // speak of them.
            switch (card.Status)
            {
                case TaskCardStatus.Running:
                    runningCount++;
                    break;
                case TaskCardStatus.Finished:
                    finishedCount++;
                    break;
                case TaskCardStatus.NeedsYou:
                    foreach (var stepState in projection.State.Steps)
                    {
                        if (stepState.Status == StepStatus.Paused)
                        {
                            InboxItems.Add(BuildInboxItem(taskDirectoryPath, projection, stepState, openTaskAsync));
                        }
                    }

                    break;
                case TaskCardStatus.Failed:
                case TaskCardStatus.Cancelled:
                case TaskCardStatus.Unavailable:
                    break;
            }
        }

        HasNoTasks = TaskCards.Count == 0;
        var needsInputCount = InboxItems.Count(item => item.Kind == PausePointKind.NeedsInput);
        InboxSummaryText = InboxItems.Count switch
        {
            0 when TaskCards.Count == 0 => "Nothing is waiting on you.",
            0 => $"Nothing is waiting on you — {runningCount} working, {finishedCount} finished.",
            _ => SummaryForPending(needsInputCount, InboxItems.Count - needsInputCount),
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
                    Path.Combine(taskDirectoryPath, ArtifactManager.ArtifactsDirectoryName), executionId);
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

        // #334: needs-input (a chat turn) asks for your reply, not your approval — the "{file} ready"
        // approval framing is wrong for it. Ready-for-review keeps its exact wording (test-pinned).
        var kind = PauseKind.ForStep(projection, stepState.StepId);
        var statusText = kind == PausePointKind.NeedsInput
            ? "Waiting for your reply"
            : previewFileName.Length > 0
                ? $"Waiting for your review — {previewFileName} ready"
                : "Waiting for your review";

        return new InboxItemViewModel(
            taskDirectoryPath,
            TaskCardViewModel.TitleFor(taskDirectoryPath),
            stepState.StepId.Value,
            statusText,
            previewText,
            kind,
            openTaskAsync);
    }

    // #334: needs-input and ready-for-review are different human acts (#319 filters them apart), so
    // the one-line summary counts them separately rather than calling every pause a "review". The
    // review-only phrasing is kept verbatim — NavigationShellTests pins it.
    private static string SummaryForPending(int needsInputCount, int reviewCount)
    {
        var reviewOnly = reviewCount == 1
            ? "1 step is waiting for your review."
            : $"{reviewCount} steps are waiting for your review.";
        if (needsInputCount == 0)
        {
            return reviewOnly;
        }

        var replyOnly = needsInputCount == 1
            ? "1 room is waiting for your reply."
            : $"{needsInputCount} rooms are waiting for your reply.";
        if (reviewCount == 0)
        {
            return replyOnly;
        }

        var replyPart = needsInputCount == 1 ? "1 waiting for your reply" : $"{needsInputCount} waiting for your reply";
        var reviewPart = reviewCount == 1 ? "1 for your review" : $"{reviewCount} for your review";
        return $"{replyPart}, {reviewPart}.";
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
        var (statusText, status) = DeriveStatus(projection);
        return new TaskCardViewModel(taskDirectoryPath, TitleFor(taskDirectoryPath), statusText, status, openTaskAsync);
    }

    /// <summary>
    /// The one place a <see cref="TaskProjection"/> becomes a human status line and a
    /// <see cref="TaskCardStatus"/>. Shared with the #336 switcher's rows rather than duplicated:
    /// the same surfaces that made #458's marks disagree across toolkits would make two copies of
    /// this disagree across views — Home would say "Cancelled" while the switcher said "Finished",
    /// which is the exact defect #461 had just fixed in one place.
    /// </summary>
    public static (string StatusText, TaskCardStatus Status) DeriveStatus(TaskProjection projection)
    {
        return projection.State.Status switch
        {
            WorkflowStatus.Paused => (PausedCardStatusText(projection), TaskCardStatus.NeedsYou),
            WorkflowStatus.Running when projection.State.Steps.FirstOrDefault(s => s.Status == StepStatus.Running) is { } runningStep
                => ($"Working — {runningStep.StepId.Value}", TaskCardStatus.Running),
            WorkflowStatus.Running => ("Working", TaskCardStatus.Running),
            _ when projection.State.Steps.Any(s => s.Status is StepStatus.Failed or StepStatus.Rejected)
                => ("Failed", TaskCardStatus.Failed),
            // #461: a cancelled run has no WorkflowStatus of its own — it reaches Terminal like any
            // other, which is exactly why it used to fall through to "Finished" and tell you a task
            // you had just stopped had completed. Cancellation is only visible in the steps. Ordered
            // after Failed on purpose: if something failed *and* something was cancelled, the
            // failure is the more important truth about the run.
            _ when projection.State.Steps.Any(s => s.Status == StepStatus.Cancelled)
                => ("Cancelled", TaskCardStatus.Cancelled),
            _ => ("Finished", TaskCardStatus.Finished),
        };
    }

    // #334: a paused chat turn is "your turn to reply", not an approval gate. A card whose only
    // paused steps are NeedsInput says so; any genuine ReadyForReview gate among them keeps the
    // established approval wording (and its exact string, which NavigationShellTests pins).
    private static string PausedCardStatusText(TaskProjection projection)
        => projection.State.Steps.Any(step =>
               step.Status == StepStatus.Paused &&
               PauseKind.ForStep(projection, step.StepId) == PausePointKind.ReadyForReview)
            ? "Waiting for your review"
            : "Waiting for your reply";
}

/// <summary>
/// Resolves a paused step's declared <see cref="PausePointKind"/> from the bound snapshot (#334) —
/// the single lookup every Home surface shares. Defaults to <see cref="PausePointKind.ReadyForReview"/>
/// for any step lacking a pause point, so a pause persisted before the kind existed keeps the
/// approval-gate meaning every pause historically carried.
/// </summary>
internal static class PauseKind
{
    public static PausePointKind ForStep(TaskProjection projection, StepId stepId)
        => projection.Snapshot.Steps.FirstOrDefault(step => step.StepId == stepId)?.PausePoint?.Kind
           ?? PausePointKind.ReadyForReview;
}

/// <summary>The one status system's card-level states — carried as data so the skin styles them consistently (color + icon + word, never color alone).</summary>
public enum TaskCardStatus
{
    Running,
    NeedsYou,
    Finished,
    Failed,

    /// <summary>
    /// The run was stopped on purpose (#461). Previously absent, which meant a cancelled task fell
    /// through to <see cref="Finished"/> — the UI told you a task you had just stopped had finished.
    /// Deliberately distinct from <see cref="Failed"/>: "you stopped it" is not "it broke", and a
    /// list that renders them alike reads far more alarming than reality.
    /// </summary>
    Cancelled,

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
    string taskDirectoryPath, string taskTitle, string stepName, string statusText, string previewText,
    PausePointKind kind, Func<string, Task> openTaskAsync)
{
    public string TaskDirectoryPath { get; } = taskDirectoryPath;
    public string TaskTitle { get; } = taskTitle;
    public string StepName { get; } = stepName;
    public string StatusText { get; } = statusText;
    public string PreviewText { get; } = previewText;
    public bool HasPreview => PreviewText.Length > 0;

    /// <summary>Which human act this pause demands (#334) — carried so #319 can filter the inbox into "Needs input" / "Ready for review" states without re-deriving it.</summary>
    public PausePointKind Kind { get; } = kind;

    /// <summary>#334: a needs-input turn wants your next message, so the action reads "Reply"; a review gate reads "Review". Both open the task — the label names the act, not a second authority.</summary>
    public string ActionLabel => Kind == PausePointKind.NeedsInput ? "Reply" : "Review";

    [RelayCommand]
    private Task Review() => openTaskAsync(TaskDirectoryPath);
}
