using System.Net.Http.Json;
using Aer.Adapters;
using Aer.Daemon;
using Aer.Ui.Tests.TestSupport;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// #537: whether a session whose worker writes no output file still carries conversation continuity.
/// </summary>
/// <remarks>
/// <para>
/// The hypothesis filed in #537, read from the code and NOT previously measured: AER decides the
/// vendor session exists by checking whether <c>response.md</c> was written. Those are unrelated
/// facts — writing the file is a permission outcome, establishment is a session outcome. A
/// directory-less session gets an all-deny grant (fail-closed, #321) so it can never write the file,
/// which would mean <c>VendorSessionEstablished</c> never becomes true and <c>--resume</c> is never
/// passed, leaving chat with no memory between turns.
/// </para>
/// <para>
/// These tests exist to CONFIRM OR KILL that, before any fix. Both arms differ in exactly one thing
/// — whether the worker writes the file — and <see cref="SessionTurn.NativeSessionResumed"/> on the
/// second turn is the observable: it records whether that turn actually resumed the vendor session.
/// </para>
/// <para>
/// The control is not optional here. "Turn 2 did not resume" is only a finding about the missing file
/// if turn 2 DOES resume when the file is present; otherwise it is a fact about this harness.
/// </para>
/// </remarks>
[Collection("DaemonIntegrationTests")]
public class SessionContinuityWithoutOutputFileTests : IAsyncLifetime
{
    private DaemonTestInstance? _daemon;
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

        _daemon = await DaemonTestHost.StartAsync(stubAdapters);
        _baseUrl = _daemon.BaseUrl;

        var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
        if (File.Exists(tokenFile))
        {
            var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
            _client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        for (var i = 0; i < 30; i++)
        {
            try
            {
                var response = await _client.GetAsync($"{_baseUrl}/api/version", TestContext.Current.CancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch (HttpRequestException)
            {
                // Kestrel still binding; retry.
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        if (_daemon is not null)
        {
            await _daemon.DisposeAsync();
        }
    }

    /// <summary>
    /// The control. When the worker writes its output file, the session is marked established and
    /// the second turn resumes. Without this passing, the arm below measures nothing.
    /// </summary>
    [Fact]
    public async Task A_session_whose_worker_writes_the_file_resumes_on_the_second_turn()
    {
        var (metadata, secondTurn) = await TwoTurnsAsync(withOutputFile: true);

        Assert.True(metadata.VendorSessionEstablished,
            "the control session was not marked established, so nothing in this class discriminates");
        Assert.True(secondTurn.NativeSessionResumed,
            "the control's second turn did not resume, so a non-resume below proves nothing");
    }

    /// <summary>
    /// The measurement. Same shape, one variable: the worker succeeds and writes no output file.
    /// </summary>
    [Fact]
    public async Task A_session_whose_worker_writes_no_file_still_carries_continuity()
    {
        var (metadata, secondTurn) = await TwoTurnsAsync(withOutputFile: false);

        Assert.True(metadata.VendorSessionEstablished,
            "the vendor ran and answered on both turns, but AER never recorded the session as "
            + "established -- continuity is keyed to a file write rather than to the session (#537)");
        Assert.True(secondTurn.NativeSessionResumed,
            "turn 2 did not resume the vendor session, so a directory-less chat starts fresh every "
            + "turn and carries no memory (#537)");
    }

    private async Task<(SessionMetadata Metadata, SessionTurn SecondTurn)> TwoTurnsAsync(bool withOutputFile)
    {
        var suffix = withOutputFile ? "" : " " + SessionTurnStubAdapter.NoOutputFileSentinel;

        var start = new StartSessionRequest(
            Adapter: "claude",
            TaskName: "continuity-" + Guid.NewGuid().ToString("N"),
            InitialMessage: "turn one" + suffix,
            SafetyCeiling: 200);

        var startResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/start", start, TestContext.Current.CancellationToken);
        Assert.True(startResponse.IsSuccessStatusCode, $"session start failed: {startResponse.StatusCode}");
        var started = await startResponse.Content.ReadFromJsonAsync<SessionMetadata>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(started);

        await PollForTurnsAsync(started.SessionId, 1);

        var send = new SendSessionMessageRequest(
            SessionId: started.SessionId,
            Message: "turn two" + suffix);
        var sendResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/send", send, TestContext.Current.CancellationToken);
        Assert.True(sendResponse.IsSuccessStatusCode, $"send failed: {sendResponse.StatusCode}");

        var metadata = await PollForTurnsAsync(started.SessionId, 2);
        return (metadata, metadata.Turns[^1]);
    }

    private async Task<SessionMetadata> PollForTurnsAsync(string sessionId, int expected)
    {
        for (var i = 0; i < 600; i++)
        {
            var response = await _client.GetAsync(
                $"{_baseUrl}/api/sessions/{sessionId}", TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode);
            var metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(metadata);
            if (metadata.Turns.Count >= expected)
            {
                return metadata;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"session never reached {expected} turn(s)");
        return null!;
    }
}
