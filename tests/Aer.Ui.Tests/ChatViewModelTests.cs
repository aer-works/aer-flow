using Aer.Adapters;

namespace Aer.Ui.Tests;

/// <summary>
/// M24 Phase 1 desktop chat UI (issue #262): <see cref="ChatViewModel"/> is pure Aer.Ui.Core logic
/// (no Avalonia, no daemon) — these tests exercise it directly rather than through a headless
/// window, the same split <see cref="PausedStepViewModelTests"/> already draws for its own ViewModel.
/// </summary>
public class ChatViewModelTests
{
    private static SessionMetadata MetadataWithTurns(params SessionTurn[] turns) => new(
        SessionId: "sess-1",
        TaskDirectoryPath: "/tmp/sess-1",
        CurrentAdapter: "claude",
        CurrentVendorSessionId: "vendor-1",
        Model: null,
        WorkingDirectory: null,
        TurnCount: turns.Length,
        SafetyCeiling: 100,
        MinimalOverhead: true,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        Turns: [.. turns]);

    [Fact]
    public void LoadFromMetadata_RendersOneRowPerHumanMessageAndOnePerCompletedResponse()
    {
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false),
            new SessionTurn(2, "claude", "Still thinking?", null, DateTimeOffset.UtcNow, true, false));

        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        Assert.Equal("sess-1", viewModel.SessionId);
        Assert.Equal(3, viewModel.Messages.Count);
        Assert.True(viewModel.Messages[0].IsFromUser);
        Assert.Equal("Hello", viewModel.Messages[0].Text);
        Assert.False(viewModel.Messages[1].IsFromUser);
        Assert.Equal("Hi there", viewModel.Messages[1].Text);
        Assert.True(viewModel.Messages[2].IsFromUser);
        Assert.Equal("Still thinking?", viewModel.Messages[2].Text);
    }

    [Fact]
    public void BeginSend_ShowsThePendingMessageUntilLoadFromMetadataObservesTheCompletedTurn()
    {
        var viewModel = new ChatViewModel();
        var initialMetadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false));
        viewModel.LoadFromMetadata(initialMetadata, "/tmp/sess-1");

        viewModel.BeginSend("What's next?", currentTurnsCount: initialMetadata.Turns.Count);
        Assert.True(viewModel.IsSending);
        Assert.Equal(string.Empty, viewModel.InputText);

        // Poll #1: the daemon hasn't finished the turn yet -- Turns is unchanged, but the pending
        // message should still render so Send doesn't look like it silently did nothing.
        viewModel.LoadFromMetadata(initialMetadata, "/tmp/sess-1");
        Assert.True(viewModel.IsSending);
        Assert.Equal(3, viewModel.Messages.Count);
        Assert.True(viewModel.Messages[^1].IsFromUser);
        Assert.Equal("What's next?", viewModel.Messages[^1].Text);

        // Poll #2: the turn landed.
        var completedMetadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false),
            new SessionTurn(2, "claude", "What's next?", "Let's continue", DateTimeOffset.UtcNow, true, false));
        viewModel.LoadFromMetadata(completedMetadata, "/tmp/sess-1");

        Assert.False(viewModel.IsSending);
        Assert.Equal(4, viewModel.Messages.Count);
        Assert.Equal("Let's continue", viewModel.Messages[^1].Text);
    }

    [Fact]
    public void FailSend_ClearsIsSendingAndSurfacesTheError()
    {
        var viewModel = new ChatViewModel();
        viewModel.BeginSend("Hello", currentTurnsCount: 0);

        viewModel.FailSend("The daemon rejected the request.");

        Assert.False(viewModel.IsSending);
        Assert.Equal("The daemon rejected the request.", viewModel.StatusText);
        Assert.True(viewModel.HasStatusText);
    }

    [Fact]
    public void AppendProgress_AccumulatesEachFragmentIntoLiveProgressText()
    {
        var viewModel = new ChatViewModel();

        viewModel.AppendProgress(new WorkerProgressEvent("text", "Thinking", IsPartial: true));
        viewModel.AppendProgress(new WorkerProgressEvent("text", " some more...", IsPartial: true));

        Assert.Equal("Thinking some more...", viewModel.LiveProgressText);
    }

    [Fact]
    public void Clear_ResetsEveryFieldToItsEmptyState()
    {
        var viewModel = new ChatViewModel();
        viewModel.LoadFromMetadata(MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi", DateTimeOffset.UtcNow, false, false)), "/tmp/sess-1");

        viewModel.Clear();

        Assert.Null(viewModel.SessionId);
        Assert.Null(viewModel.TaskDirectoryPath);
        Assert.Empty(viewModel.Messages);
        Assert.Equal("No session open.", viewModel.HeadlineText);
        Assert.False(viewModel.IsSending);
    }

    /// <summary>#290: a fresh ChatViewModel (no session ever loaded) must read as "no session open" so the Chat page's new-chat entry point renders instead of the inert message box.</summary>
    [Fact]
    public void IsSessionOpen_IsFalseUntilASessionLoadsAndFalseAgainAfterClear()
    {
        var viewModel = new ChatViewModel();
        Assert.False(viewModel.IsSessionOpen);

        viewModel.LoadFromMetadata(MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi", DateTimeOffset.UtcNow, false, false)), "/tmp/sess-1");
        Assert.True(viewModel.IsSessionOpen);

        viewModel.Clear();
        Assert.False(viewModel.IsSessionOpen);
    }

    /// <summary>#290: IsSessionOpen derives from TaskDirectoryPath, which is a plain-setter property, not an [ObservableProperty] -- this guards against a regression where LoadFromMetadata/Clear forget to raise the change notification a XAML IsVisible binding depends on.</summary>
    [Fact]
    public void IsSessionOpen_RaisesPropertyChangedOnLoadAndClear()
    {
        var viewModel = new ChatViewModel();
        var raisedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        viewModel.LoadFromMetadata(MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi", DateTimeOffset.UtcNow, false, false)), "/tmp/sess-1");
        Assert.Contains(nameof(ChatViewModel.IsSessionOpen), raisedProperties);

        raisedProperties.Clear();
        viewModel.Clear();
        Assert.Contains(nameof(ChatViewModel.IsSessionOpen), raisedProperties);
    }

    [Fact]
    public void PopulateAvailableAdapters_UsesTheProbeResultWhenAtLeastOneVendorIsAvailable()
    {
        var viewModel = new ChatViewModel();

        viewModel.PopulateAvailableAdapters([
            new VendorCliStatus("claude", "claude", IsAvailable: false),
            new VendorCliStatus("gemini", "agy", IsAvailable: true),
        ]);

        Assert.Equal(["gemini"], viewModel.AvailableAdapters);
        Assert.Equal("gemini", viewModel.NewChatAdapter);
    }

    /// <summary>Mirrors the desktop template picker's own fallback (TemplatePickerWindow.PopulateVendors) so the two "start a session" entry points never disagree about what's offered when neither vendor CLI is detected on PATH.</summary>
    [Fact]
    public void PopulateAvailableAdapters_FallsBackToClaudeAndGeminiWhenNoneAreAvailable()
    {
        var viewModel = new ChatViewModel();

        viewModel.PopulateAvailableAdapters([
            new VendorCliStatus("claude", "claude", IsAvailable: false),
            new VendorCliStatus("gemini", "agy", IsAvailable: false),
        ]);

        Assert.Equal(["claude", "gemini"], viewModel.AvailableAdapters);
    }
}
