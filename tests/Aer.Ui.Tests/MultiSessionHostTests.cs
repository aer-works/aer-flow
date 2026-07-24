using System.Diagnostics;
using System.Net.Http.Json;
using Aer.Adapters;
using Aer.Ui.Tests.TestSupport;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// #335 over a real daemon: two sessions running at once, and a stop that reaches the one it was
/// asked for.
/// </summary>
/// <remarks>
/// <para>
/// Host state used to live in three single-slot fields, so a second concurrent run overwrote the
/// first's registry and stop source. <c>RequestHostStop()</c> then cancelled whichever run started
/// <em>last</em>, regardless of which session the caller named — asking the daemon to stop A stopped
/// B and left A running. That is the inversion this class pins down.
/// </para>
/// <para>
/// <b>Why blocking stubs.</b> A synchronous stub finishes before the second request arrives, so it
/// can only ever show two runs that happened to be quick — never two in flight at one instant, and
/// never one held open long enough to cancel. <see cref="BlockingSessionTurnStubAdapter"/> parks each
/// turn on a release file so both are provably live at the moment the stop is issued.
/// </para>
/// <para>
/// <b>Why absence of a finished marker means cancelled.</b> A cancelled run's blocked process is
/// killed, so its marker never appears however long the test waits — the assertion cannot pass by
/// being slow. The surviving session is then released and its marker <em>must</em> appear, so
/// "neither finished" fails too. Both halves are needed: either alone is satisfiable by a daemon
/// that simply killed everything.
/// </para>
/// <para>
/// Shares the <c>DaemonIntegrationTests</c> collection for the same reason
/// <see cref="SessionTurnSerializationEndToEndTests"/> does: every class here spins up a real Kestrel
/// daemon against the same per-user config file, so they must run strictly sequentially.
/// </para>
/// </remarks>
[Collection("DaemonIntegrationTests")]
public class MultiSessionHostTests : IAsyncLifetime
{
    private const string SessionKeyA = "aer335-alpha";
    private const string SessionKeyB = "aer335-beta";

    private static readonly TimeSpan MarkerTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long to keep watching for a marker that should <em>never</em> appear. Long enough that a
    /// merely-slow run would have finished (the release file is already present and the poll above
    /// takes ~100ms), short enough not to dominate the suite.
    /// </summary>
    private static readonly TimeSpan AbsenceWindow = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private DaemonTestInstance? _daemon;
    private string _baseUrl = "";
    private readonly HttpClient _client = new();

    private readonly string _markerDirectory =
        Path.Combine(Path.GetTempPath(), "aer-335-" + Guid.NewGuid().ToString("N"));

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_markerDirectory);

        var blocking = new BlockingSessionTurnStubAdapter(_markerDirectory, [SessionKeyA, SessionKeyB]);
        IReadOnlyDictionary<string, IWorkerAdapter> stubAdapters = new Dictionary<string, IWorkerAdapter>
        {
            ["claude"] = blocking,
            ["gemini"] = blocking,
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
        // Release everything first: a still-blocked stub would otherwise outlive the daemon and hold
        // its marker directory open, turning cleanup into a spurious failure.
        foreach (var key in new[] { SessionKeyA, SessionKeyB })
        {
            var release = BlockingSessionTurnStubAdapter.ReleaseFilePath(_markerDirectory, key);
            if (!File.Exists(release))
            {
                await File.WriteAllTextAsync(release, "release");
            }
        }

        if (_daemon != null)
        {
            await _daemon.DisposeAsync();
        }

        _client.Dispose();

        try
        {
            if (Directory.Exists(_markerDirectory))
            {
                Directory.Delete(_markerDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A stub process on its way out can still hold a handle. Leaking a temp directory is not
            // worth failing a passing test over.
        }
    }

    [Fact]
    public async Task StoppingOneOfTwoRunningSessions_StopsThatOneAndLeavesTheOtherRunning()
    {
        var alpha = await StartBlockedSessionAsync(SessionKeyA);
        var beta = await StartBlockedSessionAsync(SessionKeyB);

        // Both are parked in their worker at this instant. Before #335 the daemon could hold host
        // state for only one of them; this is the multi-session claim, observed rather than assumed.
        await WaitForMarkerAsync(BlockingSessionTurnStubAdapter.StartedMarkerPath(_markerDirectory, SessionKeyA), "alpha never started");
        await WaitForMarkerAsync(BlockingSessionTurnStubAdapter.StartedMarkerPath(_markerDirectory, SessionKeyB), "beta never started");

        // Stop alpha by name. A null ExecutionId is the "stop this session's pump" request the
        // desktop Stop button and the phone both send.
        var stop = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/cancel",
            new CancelTaskRequest(alpha, null),
            TestContext.Current.CancellationToken);
        Assert.True(stop.IsSuccessStatusCode);

        // Beta must still be alive to be released. If the stop went to the wrong session its worker
        // is already dead and this marker never appears.
        await File.WriteAllTextAsync(
            BlockingSessionTurnStubAdapter.ReleaseFilePath(_markerDirectory, SessionKeyB),
            "release",
            TestContext.Current.CancellationToken);
        await WaitForMarkerAsync(
            BlockingSessionTurnStubAdapter.FinishedMarkerPath(_markerDirectory, SessionKeyB),
            "beta was stopped even though alpha was the session named in the cancel request");

        // And alpha really did stop: releasing it now cannot revive a killed process.
        await File.WriteAllTextAsync(
            BlockingSessionTurnStubAdapter.ReleaseFilePath(_markerDirectory, SessionKeyA),
            "release",
            TestContext.Current.CancellationToken);
        await AssertMarkerNeverAppearsAsync(
            BlockingSessionTurnStubAdapter.FinishedMarkerPath(_markerDirectory, SessionKeyA),
            "alpha ran to completion despite being the session named in the cancel request");

        Assert.NotEqual(alpha, beta);
    }

    /// <summary>Starts a session whose worker parks, and returns its task directory path.</summary>
    private async Task<string> StartBlockedSessionAsync(string sessionKey)
    {
        var request = new StartSessionRequest(
            Adapter: "claude",
            TaskName: sessionKey + "-" + Guid.NewGuid().ToString("N"),
            InitialMessage: $"hold here {sessionKey}");

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/start", request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        Assert.False(string.IsNullOrWhiteSpace(metadata.TaskDirectoryPath));
        return metadata.TaskDirectoryPath;
    }

    private static async Task WaitForMarkerAsync(string markerPath, string because)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < MarkerTimeout)
        {
            if (File.Exists(markerPath))
            {
                return;
            }

            await Task.Delay(PollInterval, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"{because} (no '{Path.GetFileName(markerPath)}' within {MarkerTimeout.TotalSeconds:0}s).");
    }

    private static async Task AssertMarkerNeverAppearsAsync(string markerPath, string because)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < AbsenceWindow)
        {
            Assert.False(File.Exists(markerPath), $"{because} ('{Path.GetFileName(markerPath)}' appeared).");
            await Task.Delay(PollInterval, TestContext.Current.CancellationToken);
        }
    }
}
