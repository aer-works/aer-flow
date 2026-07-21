using System.Net.Http.Json;
using Aer.Adapters;
using Aer.Daemon;

namespace Aer.Cli.SmokeTests;

/// <summary>
/// M24 Phase 1/2 (#262/#263) completion gate, added retroactively: two of #262's own six stated
/// verification checks -- a ~50-100 turn stress test at real chat frequency, and compact's actual
/// vendor-side context shrinkage -- were never run against a live vendor. Interactive sessions are
/// daemon-only (no <c>aer</c> CLI command wraps <c>ExecuteSessionTurnAsync</c>), so unlike every
/// other smoke test in this project, this drives <see cref="DaemonHost.RunDaemonAsync"/>'s real HTTP
/// surface directly against the real, authenticated <c>claude</c> CLI via
/// <see cref="WorkerAdapterRegistry.Default"/>.
/// <para>
/// <b>Deliberately excluded from <c>AerFlow.slnx</c></b>, same as every other test here -- see
/// <c>docs/runbooks/live-session-smoke.md</c> for the full runbook, including two checks this test
/// does NOT automate (vendor-handoff-retains-context, and the minimal-overhead latency comparison --
/// the latter isn't currently possible at all, since <c>MinimalOverhead</c> is hardcoded <c>true</c>
/// for every interactive session with no API-level override).
/// </para>
/// </summary>
public class LiveSessionSmokeTest
{
    private const int Port = 5099;
    private static readonly string BaseUrl = $"http://localhost:{Port}";

    [Fact]
    public async Task Fifty_sequential_turns_hold_and_compact_produces_a_real_vendor_summary_from_a_fresh_session()
    {
        var daemonTask = DaemonHost.RunDaemonAsync([
            "--port", Port.ToString(), "--no-mutex"
        ]);

        using var client = new HttpClient();
        try
        {
            for (var i = 0; i < 50; i++)
            {
                try
                {
                    var response = await client.GetAsync($"{BaseUrl}/api/version", TestContext.Current.CancellationToken);
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
            var token = (await File.ReadAllTextAsync(Path.Combine(aerDir, "daemon.token"), TestContext.Current.CancellationToken)).Trim();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var startRequest = new StartSessionRequest(
                Adapter: "claude",
                TaskName: "live-session-smoke-" + Guid.NewGuid().ToString("N"),
                InitialMessage: "Reply with a single short sentence acknowledging this is turn 1 of a smoke test. Do not ask questions.",
                SafetyCeiling: 200);

            var startResponse = await client.PostAsJsonAsync($"{BaseUrl}/api/sessions/start", startRequest, TestContext.Current.CancellationToken);
            Assert.True(startResponse.IsSuccessStatusCode);
            var started = await startResponse.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(started);

            var afterTurn1 = await PollUntilTurnCountAsync(client, started.SessionId, expectedTurnCount: 1);
            Assert.False(string.IsNullOrWhiteSpace(afterTurn1.Turns[0].AssistantResponse));

            const int totalTurns = 50;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 2; i <= totalTurns; i++)
            {
                var sendRequest = new SendSessionMessageRequest(
                    SessionId: started.SessionId,
                    Message: $"Reply with a single short sentence acknowledging this is turn {i}. Do not ask questions.");
                var sendResponse = await client.PostAsJsonAsync($"{BaseUrl}/api/sessions/send", sendRequest, TestContext.Current.CancellationToken);
                Assert.True(sendResponse.IsSuccessStatusCode);
                await PollUntilTurnCountAsync(client, started.SessionId, expectedTurnCount: i);
            }
            stopwatch.Stop();
            Console.WriteLine($"{totalTurns} sequential real-Claude turns completed in {stopwatch.Elapsed}.");

            var beforeCompact = await GetSessionAsync(client, started.SessionId);
            var vendorSessionIdBeforeCompact = beforeCompact.CurrentVendorSessionId;

            var compactResponse = await client.PostAsync($"{BaseUrl}/api/sessions/{started.SessionId}/compact", null, TestContext.Current.CancellationToken);
            Assert.True(compactResponse.IsSuccessStatusCode);

            var afterCompact = await PollUntilTurnCountAsync(client, started.SessionId, expectedTurnCount: totalTurns + 1);
            var compactTurn = afterCompact.Turns[^1];
            Assert.True(compactTurn.VendorHandoffSynthesized);
            Assert.NotEqual(vendorSessionIdBeforeCompact, afterCompact.CurrentVendorSessionId);

            // The only thing distinguishing "compact worked" from "compact is just plumbing" against
            // a live vendor: the response to a summarization request is real, substantial prose --
            // not asserting its exact content (this repo's convention against parsing worker output),
            // just that the live CLI actually produced a real summary rather than an empty/error reply.
            Assert.False(string.IsNullOrWhiteSpace(compactTurn.AssistantResponse));
            Assert.True(compactTurn.AssistantResponse!.Length > 40, "Expected a real, non-trivial summary from the live vendor's compact turn.");
        }
        finally
        {
            if (DaemonHost.App != null)
            {
                await DaemonHost.App.StopAsync(TestContext.Current.CancellationToken);
            }

            await daemonTask;
        }
    }

    private static async Task<SessionMetadata> GetSessionAsync(HttpClient client, string sessionId)
    {
        var response = await client.GetAsync($"{BaseUrl}/api/sessions/{sessionId}", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);
        var metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        return metadata;
    }

    private static async Task<SessionMetadata> PollUntilTurnCountAsync(HttpClient client, string sessionId, int expectedTurnCount)
    {
        for (var i = 0; i < 600; i++)
        {
            var metadata = await GetSessionAsync(client, sessionId);
            if (metadata.Turns.Count >= expectedTurnCount)
            {
                return metadata;
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Session {sessionId} never reached {expectedTurnCount} turn(s) within the live-CLI timeout.");
        return null!;
    }
}
