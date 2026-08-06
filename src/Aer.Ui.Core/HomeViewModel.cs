using System.Collections.ObjectModel;
using Aer.Adapters;
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

    public ObservableCollection<RoomCardViewModel> RoomCards { get; } = [];
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
        RoomClient session, Func<string, Task> openTaskAsync, CancellationToken cancellationToken = default)
    {
        var recents = await session.LoadRecentTaskDirectoriesAsync(cancellationToken).ConfigureAwait(true);

        RoomCards.Clear();
        InboxItems.Clear();

        foreach (var roomDirectoryPath in recents)
        {
            RoomProjection projection;
            try
            {
                projection = await RoomProjectionLoader.LoadAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(true);
            }
            catch (AerFlowException)
            {
                // §3's stale-list rule: reflected as a greyed card, never an error — the entry
                // stays visible (the user recorded it; hiding it silently would misreport their
                // own history) but carries no inbox items and no live status.
                RoomCards.Add(new RoomCardViewModel(
                    roomDirectoryPath,
                    RoomCardViewModel.TitleFor(roomDirectoryPath),
                    "Not available — moved, deleted, or not a task",
                    RoomCardStatus.Unavailable,
                    openTaskAsync));
                continue;
            }

            var card = RoomCardViewModel.FromProjection(roomDirectoryPath, projection, openTaskAsync);
            RoomCards.Add(card);

            if (card.Status == RoomCardStatus.NeedsYou)
            {
                foreach (var stepState in projection.State.Steps)
                {
                    if (stepState.Status == StepStatus.Paused)
                    {
                        InboxItems.Add(BuildInboxItem(roomDirectoryPath, projection, stepState, openTaskAsync));
                    }
                }
            }
        }

        HasNoTasks = RoomCards.Count == 0;
        UpdateInboxSummary();
    }

    /// <summary>
    /// The inbox summary's one derivation, shared by <see cref="RefreshAsync"/> and
    /// <see cref="RetireInboxItem"/> — #618's retire path first restated this switch inline, which
    /// is exactly the two-copies drift the same issue exists to end on the gate surfaces.
    /// Counts come from the card's one status derivation, not re-derived from the raw
    /// WorkflowStatus (#616: the raw switch counted every Terminal run as "finished", so a failed
    /// or cancelled task inflated the finished count). Failed, Cancelled and Unavailable are
    /// deliberately in neither count because the summary sentence doesn't speak of them.
    /// </summary>
    private void UpdateInboxSummary()
    {
        var runningCount = RoomCards.Count(card => card.Status == RoomCardStatus.Running);
        var finishedCount = RoomCards.Count(card => card.Status == RoomCardStatus.Finished);
        var needsInputCount = InboxItems.Count(item => item.Kind == PausePointKind.NeedsInput);
        InboxSummaryText = InboxItems.Count switch
        {
            0 when RoomCards.Count == 0 => "Nothing is waiting on you.",
            0 => $"Nothing is waiting on you — {runningCount} working, {finishedCount} finished.",
            _ => SummaryForPending(needsInputCount, InboxItems.Count - needsInputCount),
        };
    }

    /// <summary>
    /// #618 (0020 clause 3): answering a gate once retires it everywhere. Removes the matching inbox
    /// item immediately by gate identity (roomDirectoryPath, StepId, ExecutionId) without a full Home refresh.
    /// </summary>
    public void RetireInboxItem(string roomDirectoryPath, StepId stepId, ExecutionId executionId)
    {
        var key = AerPaths.RecordKey(roomDirectoryPath);
        var matching = InboxItems.FirstOrDefault(item =>
            AerPaths.RecordKeyComparer.Equals(AerPaths.RecordKey(item.RoomDirectoryPath), key) &&
            item.StepName == stepId.Value &&
            item.ExecutionId == executionId.Value);

        if (matching != null)
        {
            InboxItems.Remove(matching);
            UpdateInboxSummary();
        }
    }

    private static InboxItemViewModel BuildInboxItem(
        string roomDirectoryPath, RoomProjection projection, StepState stepState, Func<string, Task> openTaskAsync)
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
                    Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName), executionId);
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
            roomDirectoryPath,
            RoomCardViewModel.TitleFor(roomDirectoryPath),
            stepState.StepId.Value,
            statusText,
            previewText,
            kind,
            openTaskAsync,
            stepState.LatestExecutionId?.Value ?? string.Empty);
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
public sealed partial class RoomCardViewModel(
    string roomDirectoryPath, string title, string statusText, RoomCardStatus status, Func<string, Task> openTaskAsync)
{
    public string RoomDirectoryPath { get; } = roomDirectoryPath;
    public string Title { get; } = title;
    public string StatusText { get; } = statusText;
    public RoomCardStatus Status { get; } = status;

    /// <summary>Style hooks for the one status system (design-language.md): exactly one of these is true, consumed by the card's classes.</summary>
    public bool IsNeedsYou => Status == RoomCardStatus.NeedsYou;

    [RelayCommand]
    private Task Open() => openTaskAsync(RoomDirectoryPath);

    /// <summary>The card title is the task directory's leaf name — the human's handle for the task, with the full path detail-on-demand (ux-principles §3).</summary>
    public static string TitleFor(string roomDirectoryPath)
        => Path.GetFileName(Path.TrimEndingDirectorySeparator(roomDirectoryPath));

    public static RoomCardViewModel FromProjection(
        string roomDirectoryPath, RoomProjection projection, Func<string, Task> openTaskAsync)
    {
        var (statusText, status) = DeriveStatus(projection);
        return new RoomCardViewModel(roomDirectoryPath, TitleFor(roomDirectoryPath), statusText, status, openTaskAsync);
    }

    /// <summary>
    /// The one place a <see cref="RoomProjection"/> becomes a human status line and a
    /// <see cref="RoomCardStatus"/>. Shared with the #336 switcher's rows rather than duplicated:
    /// the same surfaces that made #458's marks disagree across toolkits would make two copies of
    /// this disagree across views — Home would say "Cancelled" while the switcher said "Finished",
    /// which is the exact defect #461 had just fixed in one place.
    /// </summary>
    public static (string StatusText, RoomCardStatus Status) DeriveStatus(RoomProjection projection)
    {
        return projection.State.Status switch
        {
            WorkflowStatus.Paused => (PausedCardStatusText(projection), RoomCardStatus.NeedsYou),
            WorkflowStatus.Running when projection.State.Steps.FirstOrDefault(s => s.Status == StepStatus.Running) is { } runningStep
                => ($"Working — {runningStep.StepId.Value}", RoomCardStatus.Running),
            WorkflowStatus.Running => ("Working", RoomCardStatus.Running),
            _ when projection.State.Steps.Any(s => s.Status is StepStatus.Failed or StepStatus.Rejected)
                => ("Failed", RoomCardStatus.Failed),
            // #461: a cancelled run has no WorkflowStatus of its own — it reaches Terminal like any
            // other, which is exactly why it used to fall through to "Finished" and tell you a task
            // you had just stopped had completed. Cancellation is only visible in the steps. Ordered
            // after Failed on purpose: if something failed *and* something was cancelled, the
            // failure is the more important truth about the run.
            _ when projection.State.Steps.Any(s => s.Status == StepStatus.Cancelled)
                => ("Cancelled", RoomCardStatus.Cancelled),
            _ => ("Finished", RoomCardStatus.Finished),
        };
    }

    // #334: a paused chat turn is "your turn to reply", not an approval gate. A card whose only
    // paused steps are NeedsInput says so; any genuine ReadyForReview gate among them keeps the
    // established approval wording (and its exact string, which NavigationShellTests pins).
    private static string PausedCardStatusText(RoomProjection projection)
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
    public static PausePointKind ForStep(RoomProjection projection, StepId stepId)
        => projection.Snapshot.Steps.FirstOrDefault(step => step.StepId == stepId)?.PausePoint?.Kind
           ?? PausePointKind.ReadyForReview;
}

/// <summary>The one status system's card-level states — carried as data so the skin styles them consistently (color + icon + word, never color alone).</summary>
public enum RoomCardStatus
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
    string roomDirectoryPath, string roomTitle, string stepName, string statusText, string previewText,
    PausePointKind kind, Func<string, Task> openTaskAsync, string executionId = "")
{
    public string RoomDirectoryPath { get; } = roomDirectoryPath;
    public string RoomTitle { get; } = roomTitle;
    public string StepName { get; } = stepName;
    public string ExecutionId { get; } = executionId;
    public string StatusText { get; } = statusText;
    public string PreviewText { get; } = previewText;
    public bool HasPreview => PreviewText.Length > 0;

    /// <summary>Which human act this pause demands (#334) — carried so #319 can filter the inbox into "Needs input" / "Ready for review" states without re-deriving it.</summary>
    public PausePointKind Kind { get; } = kind;

    /// <summary>#334: a needs-input turn wants your next message, so the action reads "Reply"; a review gate reads "Review". Both open the task — the label names the act, not a second authority.</summary>
    public string ActionLabel => Kind == PausePointKind.NeedsInput ? "Reply" : "Review";

    [RelayCommand]
    private Task Review() => openTaskAsync(RoomDirectoryPath);
}
