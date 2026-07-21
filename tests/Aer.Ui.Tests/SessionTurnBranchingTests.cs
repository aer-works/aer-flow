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
/// it was always automatable, just never automated. Runs its own daemon instance on a dedicated port
/// with <see cref="SessionTurnStubAdapter"/> substituted for both "claude" and "gemini" -- deliberately
/// NOT sharing <see cref="DaemonIntegrationTests"/>'s fixture/collection, since several of its tests
/// (capability discovery, "/compact" item presence) assert on the real adapter registry and would
/// break under a stubbed one.
/// </summary>
[Collection("SessionTurnBranchingTests")]
public class SessionTurnBranchingTests : IAsyncLifetime
{
    private Task? _daemonTask;
    private const string BaseUrl = "http://localhost:5051";
    private readonly HttpClient _client = new();

    public async ValueTask InitializeAsync()
    {
        IReadOnlyDictionary<string, IWorkerAdapter> stubAdapters = new Dictionary<string, IWorkerAdapter>
        {
            ["claude"] = new SessionTurnStubAdapter(),
            ["gemini"] = new SessionTurnStubAdapter(),
        };

        _daemonTask = DaemonHost.RunDaemonAsync(new[] { "--port", "5051", "--no-mutex" }, stubAdapters);

        for (int i = 0; i < 30; i++)
        {
            try
            {
                var response = await _client.GetAsync($"{BaseUrl}/api/version", TestContext.Current.CancellationToken);
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

        var response = await _client.PostAsJsonAsync($"{BaseUrl}/api/sessions/start", request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        return await PollUntilTurnCountAsync(metadata.SessionId, expectedTurnCount: 1);
    }

    private async Task<SessionMetadata> PollUntilTurnCountAsync(string sessionId, int expectedTurnCount)
    {
        SessionMetadata? metadata = null;
        for (var i = 0; i < 100; i++)
        {
            var response = await _client.GetAsync($"{BaseUrl}/api/sessions/{sessionId}", TestContext.Current.CancellationToken);
            if (response.IsSuccessStatusCode)
            {
                metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
                if (metadata != null && metadata.Turns.Count >= expectedTurnCount)
                {
                    return metadata;
                }
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Session {sessionId} never reached {expectedTurnCount} turn(s); last seen: {metadata?.Turns.Count ?? -1}.");
        return null!;
    }

    [Fact]
    public async Task SendMessage_WithDifferentAdapterThanCurrent_SynthesizesHandoffAndSwitchesCurrentAdapter()
    {
        var started = await StartStubSessionAsync("hello");
        Assert.Equal("claude", started.CurrentAdapter);
        Assert.False(started.Turns[0].VendorHandoffSynthesized);

        var sendRequest = new SendSessionMessageRequest(SessionId: started.SessionId, Message: "switch to gemini", Adapter: "gemini");
        var sendResponse = await _client.PostAsJsonAsync($"{BaseUrl}/api/sessions/send", sendRequest, TestContext.Current.CancellationToken);
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
        var secondResponse = await _client.PostAsJsonAsync($"{BaseUrl}/api/sessions/send", secondRequest, TestContext.Current.CancellationToken);
        Assert.True(secondResponse.IsSuccessStatusCode);
        var afterSecond = await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: 2);
        Assert.Equal(2, afterSecond.TurnCount);
        Assert.False(afterSecond.Turns[1].VendorHandoffSynthesized);

        // metadata.TurnCount (2) >= SafetyCeiling (2) on this third turn -- crosses the ceiling.
        var thirdVendorSessionId = afterSecond.CurrentVendorSessionId;
        var thirdRequest = new SendSessionMessageRequest(SessionId: started.SessionId, Message: "third turn crosses ceiling");
        var thirdResponse = await _client.PostAsJsonAsync($"{BaseUrl}/api/sessions/send", thirdRequest, TestContext.Current.CancellationToken);
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

        var compactResponse = await _client.PostAsync($"{BaseUrl}/api/sessions/{started.SessionId}/compact", null, TestContext.Current.CancellationToken);
        Assert.True(compactResponse.IsSuccessStatusCode);

        var afterCompact = await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: 2);

        var compactTurn = afterCompact.Turns[1];
        Assert.True(compactTurn.VendorHandoffSynthesized);
        Assert.False(compactTurn.NativeSessionResumed);
        Assert.NotEqual(vendorSessionIdBeforeCompact, afterCompact.CurrentVendorSessionId);
    }

    [Fact]
    public async Task RepeatedSupersede_HoldsAtSixtyFastSequentialTurns()
    {
        const int turnCount = 60;
        var started = await StartStubSessionAsync("hello", safetyCeiling: turnCount + 10);

        for (var i = 2; i <= turnCount; i++)
        {
            var request = new SendSessionMessageRequest(SessionId: started.SessionId, Message: $"turn {i}");
            var response = await _client.PostAsJsonAsync($"{BaseUrl}/api/sessions/send", request, TestContext.Current.CancellationToken);
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
