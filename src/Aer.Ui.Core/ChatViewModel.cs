using System.Collections.ObjectModel;
using System.Linq;
using Aer.Adapters;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aer.Ui.Core;

/// <summary>One rendered row in <see cref="ChatViewModel.Messages"/> — a human turn or an assistant response, never both (M24 Phase 1 desktop chat UI, issue #262).</summary>
public sealed record ChatMessageViewModel(string SenderLabel, string Text, DateTimeOffset Timestamp, bool IsFromUser);

/// <summary>
/// One row in the chat capability picker (M24 Phase 2 follow-up). Only <c>"command"</c>/<c>"skill"</c>/<c>"agent"</c>
/// kinds are <see cref="IsInvokable"/> — things a user can actually pick and send/insert. Gemini's
/// <c>"mode"</c>/<c>"plugin"</c> kinds are informational only (a mode is a permission-scope label,
/// not a chat message; a plugin is something already imported into the vendor CLI, not an action) —
/// the picker renders them in a separate, non-selectable section rather than presenting all kinds as
/// equally actionable.
/// </summary>
public sealed record ChatCapabilityItemViewModel(string Name, string Kind, string Description, bool IsRecentlyUsed)
{
    public bool IsInvokable => Kind is "command" or "skill" or "agent";
}

/// <summary>
/// The dedicated Chat view's state (M24 Phase 1 desktop wiring, issue #262) — a chat/codebase
/// session renders here instead of <see cref="MainWindowViewModel.TaskSteps"/>'s generic DAG
/// drill-in, since a single repeatedly-superseded "chat" step has no dependency graph worth
/// showing and the real content (<see cref="SessionMetadata.Turns"/>) lives outside
/// <c>TaskProjection</c> entirely.
/// <para>
/// <c>POST /api/sessions/send</c> only confirms a turn was dispatched — the daemon runs it on a
/// fire-and-forget background task and the response carries no updated metadata at all
/// (<c>Aer.Daemon.Program</c>'s handler). Completion is observed the same way every other live
/// task state already is in this app: <c>MainWindow</c>'s existing 2-second poll
/// (<c>_liveRefreshTimer</c>) reloads <see cref="SessionMetadata"/> and calls
/// <see cref="LoadFromMetadata"/> again, whose <see cref="Aer.Adapters.SessionTurn"/> count moving
/// past <see cref="_turnsCountAtSendTime"/> is what flips <see cref="IsSending"/> back off — no
/// second polling loop or completion push needed.
/// </para>
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

    /// <summary>Invokable skills/commands/agents (M24 Phase 2 follow-up chat capability picker) — recently-used first, per <see cref="LoadCommands"/>.</summary>
    public ObservableCollection<ChatCapabilityItemViewModel> InvokableCommands { get; } = [];

    /// <summary>Informational-only entries (Gemini's modes/plugins) — shown, never selectable.</summary>
    public ObservableCollection<ChatCapabilityItemViewModel> InfoCommands { get; } = [];

    [ObservableProperty]
    private bool isCommandMenuOpen;

    [ObservableProperty]
    private string inputText = string.Empty;

    [ObservableProperty]
    private bool isSending;

    /// <summary>The raw in-turn stream (<see cref="WorkerProgressEvent.Text"/> concatenated as it arrives) — reset at the start of every send.</summary>
    [ObservableProperty]
    private string liveProgressText = string.Empty;

    [ObservableProperty]
    private string headlineText = "No session open.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private string statusText = string.Empty;

    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    /// <summary>The active session mode ("auto"/"default"/"plan"/"custom"), or null until <see cref="TaskSession.GetSessionModeAsync"/> has resolved it (#286) — persistently shown in the chat header, not just reflected transiently after a click.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentMode))]
    private string? currentMode;

    public bool HasCurrentMode => !string.IsNullOrEmpty(CurrentMode);

    public string? SessionId { get; private set; }
    public string? TaskDirectoryPath { get; private set; }
    public string? CurrentAdapter { get; private set; }

    private int _turnsCountAtSendTime;
    private string? _pendingUserMessage;

    /// <summary>Rebuilds <see cref="Messages"/> from a freshly loaded/polled <see cref="SessionMetadata"/> — the chat view's counterpart of <see cref="MainWindowViewModel.RebuildTaskSteps"/>.</summary>
    public void LoadFromMetadata(SessionMetadata metadata, string taskDirectoryPath)
    {
        SessionId = metadata.SessionId;
        TaskDirectoryPath = taskDirectoryPath;
        CurrentAdapter = metadata.CurrentAdapter;
        HeadlineText = $"{metadata.CurrentAdapter} — turn {metadata.Turns.Count}";

        Messages.Clear();
        foreach (var turn in metadata.Turns)
        {
            Messages.Add(new ChatMessageViewModel("You", turn.HumanMessage, turn.ExecutedAt, IsFromUser: true));
            if (turn.AssistantResponse is { } response)
            {
                Messages.Add(new ChatMessageViewModel(turn.Vendor, response, turn.ExecutedAt, IsFromUser: false));
            }
        }

        if (IsSending && metadata.Turns.Count > _turnsCountAtSendTime)
        {
            IsSending = false;
            LiveProgressText = string.Empty;
            _pendingUserMessage = null;
        }
        else if (IsSending && _pendingUserMessage is { } pending)
        {
            // The turn hasn't landed in Turns yet (still running, or the send hasn't reached the
            // daemon's background task) -- show the user's own message immediately rather than
            // leaving the box looking like Send did nothing until the response completes.
            Messages.Add(new ChatMessageViewModel("You", pending, DateTimeOffset.UtcNow, IsFromUser: true));
        }
    }

    /// <summary>Marks a send as in flight and captures enough state for <see cref="LoadFromMetadata"/> to detect completion. Called by <c>MainWindow</c> right before it posts to the daemon.</summary>
    public void BeginSend(string message, int currentTurnsCount)
    {
        _turnsCountAtSendTime = currentTurnsCount;
        _pendingUserMessage = message;
        LiveProgressText = string.Empty;
        IsSending = true;
        InputText = string.Empty;
    }

    /// <summary>Called on a failed dispatch (the daemon rejected or was unreachable) — <see cref="LoadFromMetadata"/> never runs to clear <see cref="IsSending"/> in that case since no turn was ever started.</summary>
    public void FailSend(string errorMessage)
    {
        IsSending = false;
        _pendingUserMessage = null;
        StatusText = errorMessage;
    }

    /// <summary>Appends one live in-turn stream fragment (<c>/api/ws/progress</c>) to <see cref="LiveProgressText"/>.</summary>
    public void AppendProgress(WorkerProgressEvent progressEvent)
    {
        LiveProgressText += progressEvent.Text;
    }

    /// <summary>
    /// Populates <see cref="InvokableCommands"/>/<see cref="InfoCommands"/> from a fresh
    /// <c>GET /api/sessions/{id}/commands</c> result (M24 Phase 2 follow-up) — recently-used items
    /// first within each list, matching this vendor's own item order otherwise.
    /// </summary>
    public void LoadCommands(TaskSession.SessionCommandsResult result)
    {
        InvokableCommands.Clear();
        InfoCommands.Clear();

        var recentRank = result.RecentlyUsed
            .Select((name, index) => (name, index))
            .ToDictionary(t => t.name, t => t.index, StringComparer.Ordinal);

        var ordered = result.Items
            .Select(item => new ChatCapabilityItemViewModel(item.Name, item.Kind, item.Description, recentRank.ContainsKey(item.Name)))
            .OrderBy(item => recentRank.TryGetValue(item.Name, out var rank) ? rank : int.MaxValue);

        foreach (var item in ordered)
        {
            (item.IsInvokable ? InvokableCommands : InfoCommands).Add(item);
        }
    }

    public void Clear()
    {
        SessionId = null;
        TaskDirectoryPath = null;
        CurrentAdapter = null;
        HeadlineText = "No session open.";
        StatusText = string.Empty;
        LiveProgressText = string.Empty;
        IsSending = false;
        _pendingUserMessage = null;
        CurrentMode = null;
        Messages.Clear();
        InvokableCommands.Clear();
        InfoCommands.Clear();
        IsCommandMenuOpen = false;
    }
}
