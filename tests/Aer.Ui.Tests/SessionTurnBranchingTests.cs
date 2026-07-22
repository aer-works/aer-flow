using System.Diagnostics;
using System.Net.Http.Json;
using Aer.Adapters;
using Aer.Daemon;
using Aer.Ui.Tests.TestSupport;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// Retroactive M24 Phase 1/2 test-gap-fill (#262/#263): covers <c>ExecuteSessionTurnAsync</c>'s
/// vendor-handoff/safety-ceiling branching logic and the compact endpoint's real handoff behavior --
/// none of this touches a vendor CLI (it's pure C# branching over <see cref="SessionMetadata"/>), so
/// it was always automatable, just never automated. Runs its own daemon instance on a dynamically
/// OS-assigned port (issue #296) with <see cref="SessionTurnStubAdapter"/> substituted for both
/// "claude" and "gemini" -- deliberately
/// NOT sharing <see cref="DaemonIntegrationTests"/>'s fixture (a different class, its own
/// <c>InitializeAsync</c>/adapter registry), since several of its tests (capability discovery,
/// "/compact" item presence) assert on the real adapter registry and would break under a stubbed
/// one. It DOES share the same xUnit *collection name*, deliberately: both classes spin up a real
/// Kestrel daemon per test and both point their (independently-constructed)
/// <c>LocalUiConfigurationStore</c> at the same real per-user config file with no cross-instance
/// locking -- letting the two collections run in parallel (xUnit's default across different
/// collection names) caused intermittent "connection refused" failures and, under full-suite load,
/// an outright hang. Same collection name forces xUnit to run every test in both classes strictly
/// sequentially instead.
/// </summary>
[Collection("DaemonIntegrationTests")]
public class SessionTurnBranchingTests : IAsyncLifetime
{
    private Task? _daemonTask;
    private string _baseUrl = "";
    private readonly HttpClient _client = new();

    public async ValueTask InitializeAsync()
    {
        IReadOnlyDictionary<string, IWorkerAdapter> stubAdapters = new Dictionary<string, IWorkerAdapter>
        {
            ["claude"] = new SessionTurnStubAdapter(),
            ["gemini"] = new SessionTurnStubAdapter(),
            [NoOpWorkerAdapter.AdapterName] = new NoOpWorkerAdapter(),
        };

        // Start Daemon on a dynamically OS-assigned port (issue #296) — a hardcoded port collides
        // whenever two test runs happen to overlap.
        (_daemonTask, _baseUrl) = await DaemonTestHost.StartAsync(stubAdapters);

        for (int i = 0; i < 30; i++)
        {
            try
            {
                var response = await _client.GetAsync($"{_baseUrl}/api/version", TestContext.Current.CancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }

        var aerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aer");
        var tokenFile = Path.Combine(aerDir, "daemon.token");
        if (File.Exists(tokenFile))
        {
            var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (DaemonHost.App != null)
        {
            await DaemonHost.App.StopAsync();
        }

        if (_daemonTask != null)
        {
            await _daemonTask;
        }

        _client.Dispose();
    }

    private async Task<SessionMetadata> StartStubSessionAsync(string initialMessage, int? safetyCeiling = null)
    {
        var request = new StartSessionRequest(
            Adapter: "claude",
            TaskName: "stub-session-" + Guid.NewGuid().ToString("N"),
            InitialMessage: initialMessage,
            SafetyCeiling: safetyCeiling);

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/start", request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        return await PollUntilTurnCountAsync(metadata.SessionId, expectedTurnCount: 1);
    }

    /// <summary>How long <see cref="PollUntilTurnCountAsync"/> waits for a session to reach a turn count.</summary>
    /// <remarks>
    /// A wall-clock deadline, deliberately not a fixed attempt count. This previously polled 100
    /// times at 100ms, capping the wait near 10 seconds however loaded the host was, which made
    /// the 60-turn stress test flaky on CI's Windows leg: it failed having observed 31 of 32 turns,
    /// so the supersede chain under test was working and only the budget had run out. A deadline
    /// scales with the machine rather than assuming a fast one, and matches how the rest of the
    /// suite waits (<c>Task.WhenAny(task, Task.Delay(timeout))</c>, <c>DaemonTestHost</c>'s
    /// <c>PollInterval</c>). A genuinely stuck session still fails, just on elapsed time.
    /// </remarks>
    private static readonly TimeSpan TurnCountTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan TurnCountPollInterval = TimeSpan.FromMilliseconds(100);

    private async Task<SessionMetadata> PollUntilTurnCountAsync(string sessionId, int expectedTurnCount)
    {
        SessionMetadata? metadata = null;
        var polls = 0;
        var started = Stopwatch.StartNew();
        while (started.Elapsed < TurnCountTimeout)
        {
            polls++;
            var response = await _client.GetAsync($"{_baseUrl}/api/sessions/{sessionId}", TestContext.Current.CancellationToken);
            if (response.IsSuccessStatusCode)
            {
                metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
                if (metadata != null && metadata.Turns.Count >= expectedTurnCount)
                {
                    return metadata;
                }
            }

            await Task.Delay(TurnCountPollInterval, TestContext.Current.CancellationToken);
        }

        Assert.Fail(
            $"Session {sessionId} never reached {expectedTurnCount} turn(s) within {TurnCountTimeout.TotalSeconds:0}s " +
            $"({polls} polls); last seen: {metadata?.Turns.Count ?? -1}.");
        return null!;
    }

    [Fact]
    public async Task SendMessage_WithDifferentAdapterThanCurrent_SynthesizesHandoffAndSwitchesCurrentAdapter()
    {
        var started = await StartStubSessionAsync("hello");
        Assert.Equal("claude", started.CurrentAdapter);
        Assert.False(started.Turns[0].VendorHandoffSynthesized);

        var sendRequest = new SendSessionMessageRequest(SessionId: started.SessionId, Message: "switch to gemini", Adapter: "gemini");
        var sendResponse = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/send", sendRequest, TestContext.Current.CancellationToken);
        Assert.True(sendResponse.IsSuccessStatusCode);

        var afterHandoff = await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: 2);

        Assert.Equal("gemini", afterHandoff.CurrentAdapter);
        var handoffTurn = afterHandoff.Turns[1];
        Assert.Equal("gemini", handoffTurn.Vendor);
        Assert.True(handoffTurn.VendorHandoffSynthesized);
        Assert.False(handoffTurn.NativeSessionResumed);
    }

    [Fact]
    public async Task SendMessage_WhenSafetyCeilingReached_ResetsTurnCountAndSynthesizesHandoff()
    {
        var started = await StartStubSessionAsync("hello", safetyCeiling: 2);
        Assert.Equal(1, started.TurnCount);

        var secondRequest = new SendSessionMessageRequest(SessionId: started.SessionId, Message: "second turn");
        var secondResponse = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/send", secondRequest, TestContext.Current.CancellationToken);
        Assert.True(secondResponse.IsSuccessStatusCode);
        var afterSecond = await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: 2);
        Assert.Equal(2, afterSecond.TurnCount);
        Assert.False(afterSecond.Turns[1].VendorHandoffSynthesized);

        // metadata.TurnCount (2) >= SafetyCeiling (2) on this third turn -- crosses the ceiling.
        var thirdVendorSessionId = afterSecond.CurrentVendorSessionId;
        var thirdRequest = new SendSessionMessageRequest(SessionId: started.SessionId, Message: "third turn crosses ceiling");
        var thirdResponse = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/send", thirdRequest, TestContext.Current.CancellationToken);
        Assert.True(thirdResponse.IsSuccessStatusCode);
        var afterThird = await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: 3);

        var ceilingTurn = afterThird.Turns[2];
        Assert.True(ceilingTurn.VendorHandoffSynthesized);
        Assert.Equal(1, afterThird.TurnCount);
        Assert.NotEqual(thirdVendorSessionId, afterThird.CurrentVendorSessionId);
    }

    [Fact]
    public async Task Compact_AlwaysSynthesizesHandoffAndStartsAFreshNativeSession()
    {
        // This proves AER-side plumbing only: the compact turn takes the handoff branch and a new
        // native vendor session id is issued. Whether the *vendor's own* context window is actually
        // smaller afterward is vendor-internal behavior, not something this stub can observe --
        // that claim belongs to a live smoke gate, not this test (see #263's reconciliation notes).
        var started = await StartStubSessionAsync("hello");
        var vendorSessionIdBeforeCompact = started.CurrentVendorSessionId;
        Assert.NotNull(vendorSessionIdBeforeCompact);

        var compactResponse = await _client.PostAsync($"{_baseUrl}/api/sessions/{started.SessionId}/compact", null, TestContext.Current.CancellationToken);
        Assert.True(compactResponse.IsSuccessStatusCode);

        var afterCompact = await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: 2);

        var compactTurn = afterCompact.Turns[1];
        Assert.True(compactTurn.VendorHandoffSynthesized);
        Assert.False(compactTurn.NativeSessionResumed);
        Assert.NotEqual(vendorSessionIdBeforeCompact, afterCompact.CurrentVendorSessionId);
    }

    /// <summary>
    /// Starts a session with no <c>InitialMessage</c> -- the normal chat-page flow, and the exact
    /// shape that reproduced #285 live: <c>/api/sessions/start</c> only runs a turn when
    /// <c>InitialMessage</c> is non-blank, so this session has <c>TurnCount:0</c> and its first real
    /// turn arrives via <c>/api/sessions/send</c> with <c>isInitial:false</c>.
    /// </summary>
    private async Task<SessionMetadata> StartStubSessionWithNoInitialMessageAsync(int? safetyCeiling = null)
    {
        var request = new StartSessionRequest(
            Adapter: "claude",
            TaskName: "stub-session-" + Guid.NewGuid().ToString("N"),
            SafetyCeiling: safetyCeiling);

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/start", request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        Assert.Empty(metadata.Turns);
        return metadata;
    }

    [Fact]
    public async Task FirstMessage_OfASessionStartedWithNoInitialMessage_EstablishesInsteadOfResuming()
    {
        // The actual #285 bug: isInitial was standing in for "has the vendor established this id
        // yet", and was wrong here -- the pre-fix code sent this turn as `--resume <unestablished
        // guid>`, which a real vendor CLI rejects outright, permanently wedging the session.
        var started = await StartStubSessionWithNoInitialMessageAsync();

        var sendRequest = new SendSessionMessageRequest(SessionId: started.SessionId, Message: "hello");
        var sendResponse = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/send", sendRequest, TestContext.Current.CancellationToken);
        Assert.True(sendResponse.IsSuccessStatusCode);

        var afterFirst = await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: 1);
        Assert.False(afterFirst.Turns[0].NativeSessionResumed);
        Assert.True(afterFirst.VendorSessionEstablished);
        Assert.NotNull(afterFirst.Turns[0].AssistantResponse);
    }

    [Fact]
    public async Task SecondMessage_AfterAnEstablishedFirstTurn_ActuallyResumes()
    {
        var started = await StartStubSessionWithNoInitialMessageAsync();
        await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(SessionId: started.SessionId, Message: "hello"), TestContext.Current.CancellationToken);
        await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: 1);

        var secondResponse = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(SessionId: started.SessionId, Message: "second message"), TestContext.Current.CancellationToken);
        Assert.True(secondResponse.IsSuccessStatusCode);

        var afterSecond = await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: 2);
        Assert.True(afterSecond.Turns[1].NativeSessionResumed);
    }

    /// <summary>
    /// #285's design hinges on an assumption every other test in this file leaves unexercised: a
    /// *mid-conversation* Supersede-driven rerun of "chat" can fail (turn N&gt;1, as opposed to the
    /// very first turn). When it does, the anchor step ends up settled at plain <c>Succeeded</c> (not
    /// <c>Paused</c>) -- issuing the Supersede decision itself resumes/unpauses anchor immediately,
    /// and because chat's rerun failed, nothing re-triggers anchor's own pause point again. There is
    /// no paused execution left for a further Decide to target, so ExecuteSessionTurnAsync's "anchor
    /// never succeeded" branch (delete snapshot.json/flow.jsonl/artifacts, re-materialize fresh) is
    /// the only legal way forward, and it runs even though anchor *did* succeed once before.
    ///
    /// This is an acceptable recovery path, not a defect: Flow's own internal bookkeeping
    /// (snapshot/log/artifacts) resets, but the user-visible conversation does not -- continuity is
    /// carried by <c>VendorSessionEstablished</c> (SessionMetadata, untouched by the wipe) and by
    /// SessionMetadata's own Turns list, so the vendor CLI is still invoked with <c>--resume</c>
    /// against the real prior conversation, not a fresh one. This test asserts the thing that
    /// actually matters -- a mid-conversation failure surfaces its error and a subsequent retry
    /// recovers and resumes the real vendor session -- not Flow's internal directory bookkeeping.
    /// </summary>
    [Fact]
    public async Task MidConversationFailure_RecoversOnRetry()
    {
        var started = await StartStubSessionWithNoInitialMessageAsync();
        await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(SessionId: started.SessionId, Message: "turn one"), TestContext.Current.CancellationToken);
        var afterFirst = await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: 1);
        Assert.NotNull(afterFirst.Turns[0].AssistantResponse);

        var failingSend = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(SessionId: started.SessionId, Message: SessionTurnStubAdapter.FailureSentinel),
            TestContext.Current.CancellationToken);
        Assert.True(failingSend.IsSuccessStatusCode);
        var afterSecond = await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: 2);
        Assert.Null(afterSecond.Turns[1].AssistantResponse);
        Assert.NotNull(afterSecond.Turns[1].ErrorMessage);

        var thirdSend = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(SessionId: started.SessionId, Message: "turn three"), TestContext.Current.CancellationToken);
        Assert.True(thirdSend.IsSuccessStatusCode);
        var afterThird = await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: 3);
        Assert.NotNull(afterThird.Turns[2].AssistantResponse);

        // Resumed, not re-established: the real vendor conversation continues across the
        // failure/retry even though Flow's own internal bookkeeping reset underneath it.
        Assert.True(afterThird.Turns[2].NativeSessionResumed);
    }

    [Fact]
    public async Task FirstMessage_WhenTheVendorRejectsIt_DoesNotPermanentlyWedgeTheSession()
    {
        var started = await StartStubSessionWithNoInitialMessageAsync();

        var failingSend = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(SessionId: started.SessionId, Message: SessionTurnStubAdapter.FailureSentinel),
            TestContext.Current.CancellationToken);
        Assert.True(failingSend.IsSuccessStatusCode);

        var afterFailure = await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: 1);
        Assert.Null(afterFailure.Turns[0].AssistantResponse);
        Assert.NotNull(afterFailure.Turns[0].ErrorMessage);
        Assert.False(afterFailure.VendorSessionEstablished);

        // The regression this guards against: pre-fix, this second attempt would still carry
        // NativeSessionResumed=true (isInitial was already false on the very first turn), so it
        // would `--resume` the same never-established id and fail identically forever.
        var retrySend = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(SessionId: started.SessionId, Message: "try again"),
            TestContext.Current.CancellationToken);
        Assert.True(retrySend.IsSuccessStatusCode);

        var afterRetry = await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: 2);
        Assert.False(afterRetry.Turns[1].NativeSessionResumed);
        Assert.NotNull(afterRetry.Turns[1].AssistantResponse);
        Assert.True(afterRetry.VendorSessionEstablished);
    }

    [Fact]
    public async Task RepeatedSupersede_HoldsAtSixtyFastSequentialTurns()
    {
        const int turnCount = 60;
        var started = await StartStubSessionAsync("hello", safetyCeiling: turnCount + 10);

        for (var i = 2; i <= turnCount; i++)
        {
            var request = new SendSessionMessageRequest(SessionId: started.SessionId, Message: $"turn {i}");
            var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/send", request, TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode);
            await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: i);
        }

        var final = await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: turnCount);
        Assert.Equal(turnCount, final.TurnCount);
        Assert.Equal(turnCount, final.Turns.Count);
        for (var i = 0; i < turnCount; i++)
        {
            Assert.Equal(i + 1, final.Turns[i].TurnIndex);
        }

        var artifactsDir = Path.Combine(final.TaskDirectoryPath, "artifacts");
        Assert.True(Directory.Exists(artifactsDir));
        var executionDirs = Directory.GetDirectories(artifactsDir);
        Assert.Equal(executionDirs.Length, executionDirs.Distinct().Count());
    }
}
