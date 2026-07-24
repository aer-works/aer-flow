using System.Diagnostics;
using System.Net.Http.Json;
using Aer.Adapters;
using Aer.Ui.Tests.TestSupport;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// #393 over a real daemon: overlapping <c>POST /api/sessions/send</c> calls for one session must
/// not lose turns or destroy the flow log.
///
/// The half this reproduces deterministically is the <em>metadata</em> race, which is what made a
/// body-only lock a false fix. The send endpoint loads <c>session.json</c> and then queues the turn
/// fire-and-forget behind an already-returned 200, so without serialisation two overlapping sends
/// both read the same pre-turn metadata, both append to the same N-turn transcript and both write
/// N+1 -- one turn silently vanishes. Serialising execution alone does not fix that: the turn must
/// re-read metadata *inside* the lock, which is what <c>ExecuteSessionTurnAsync</c> now does.
///
/// The destructive branch itself (delete snapshot/flow log/artifacts before <c>RunAsync</c> takes
/// Flow's lock) is not reachable here -- once turn one leaves the anchor Paused every later turn
/// takes the Supersede branch -- and a synchronous stub cannot hold an anchor <c>Running</c> at the
/// instant of the check, the same limitation <see cref="SessionReMaterializationGuardTests"/>
/// records. That half stays a by-construction guarantee: the read, the branch and the delete are now
/// inside one critical section. This test still asserts the log survives, so a regression that
/// reintroduced an unguarded wipe on this path would fail here.
///
/// Shares the <c>DaemonIntegrationTests</c> collection for the same reason
/// <see cref="SessionTurnBranchingTests"/> does: every class here spins up a real Kestrel daemon and
/// points an independently-constructed config store at the same per-user file, so they must run
/// strictly sequentially.
/// </summary>
[Collection("DaemonIntegrationTests")]
public class SessionTurnSerializationEndToEndTests : IAsyncLifetime
{
    private DaemonTestInstance? _daemon;
    private string _baseUrl = "";
    private readonly HttpClient _client = new();

    /// <summary>
    /// Concurrent sends fired at one session. Enough to make a lost update near-certain without the
    /// fix (every one of them races the same pre-turn metadata read), small enough that the
    /// serialised run stays well inside <see cref="TurnCountTimeout"/>.
    /// </summary>
    private const int ConcurrentSends = 4;

    private static readonly TimeSpan TurnCountTimeout = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan TurnCountPollInterval = TimeSpan.FromMilliseconds(100);

    public async ValueTask InitializeAsync()
    {
        IReadOnlyDictionary<string, IWorkerAdapter> stubAdapters = new Dictionary<string, IWorkerAdapter>
        {
            ["claude"] = new SessionTurnStubAdapter(),
            ["gemini"] = new SessionTurnStubAdapter(),
            [NoOpWorkerAdapter.AdapterName] = new NoOpWorkerAdapter(),
        };

        _daemon = await DaemonTestHost.StartAsync(stubAdapters);
        _baseUrl = _daemon.BaseUrl;

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

        var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
        if (File.Exists(tokenFile))
        {
            var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_daemon != null)
        {
            await _daemon.DisposeAsync();
        }

        _client.Dispose();
    }

    [Fact]
    public async Task ConcurrentSendsForOneSession_LoseNoTurnsAndLeaveTheFlowLogIntact()
    {
        var started = await StartStubSessionAsync("hello");
        Assert.Single(started.Turns);

        var messages = Enumerable.Range(1, ConcurrentSends).Select(i => $"concurrent turn {i}").ToArray();

        // Fire them together: no awaits between the posts, so they land on the endpoint overlapping.
        var sends = messages
            .Select(message => _client.PostAsJsonAsync(
                $"{_baseUrl}/api/sessions/send",
                new SendSessionMessageRequest(SessionId: started.SessionId, Message: message),
                TestContext.Current.CancellationToken))
            .ToArray();

        foreach (var response in await Task.WhenAll(sends))
        {
            Assert.True(response.IsSuccessStatusCode);
        }

        var final = await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: 1 + ConcurrentSends);

        // The lost-update assertion: every message must appear exactly once. Order is deliberately
        // not asserted -- SemaphoreSlim is not FIFO, so serialised turns may still run out of order.
        // That is a separate ordering concern from this fix (see the issue), not a defect here.
        Assert.Equal(1 + ConcurrentSends, final.Turns.Count);
        foreach (var message in messages)
        {
            Assert.Equal(1, final.Turns.Count(t => t.HumanMessage == message));
        }

        // TurnCount is the ceiling counter that drives handoff; a lost update would desync it.
        Assert.Equal(1 + ConcurrentSends, final.TurnCount);

        // And the destructive branch never ran: Flow's own bookkeeping survived every overlap.
        var taskDirectory = final.TaskDirectoryPath;
        Assert.False(string.IsNullOrWhiteSpace(taskDirectory));
        Assert.True(File.Exists(Path.Combine(taskDirectory, "flow.jsonl")), "flow.jsonl was deleted by a racing turn.");
        Assert.True(File.Exists(Path.Combine(taskDirectory, "snapshot.json")), "snapshot.json was deleted by a racing turn.");
    }

    private async Task<SessionMetadata> StartStubSessionAsync(string initialMessage)
    {
        var request = new StartSessionRequest(
            Adapter: "claude",
            TaskName: "serialization-session-" + Guid.NewGuid().ToString("N"),
            InitialMessage: initialMessage);

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/start", request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        return await PollUntilTurnCountAsync(metadata.SessionId, expectedTurnCount: 1);
    }

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
}
