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
/// <para>
/// SCOPE — what this class does NOT prove. The stub ignores the permission grant entirely and
/// reproduces the no-file case from a prompt sentinel, so these are tests of the establishment branch,
/// not end-to-end evidence that a real directory-less session produces the stdout-only shape. That
/// rests on #534's live measurement against `claude`, and it is claude-only: stdout is captured only
/// when <c>StreamJson</c> is set, which is claude-only, so the agy half of this defect is untouched
/// and tracked as #545.
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

    /// <summary>
    /// The one other case this change flips, pinned so it is a decision rather than a side effect.
    /// </summary>
    /// <remarks>
    /// A handoff mints a fresh vendor session id, so prior establishment cannot carry over and
    /// <c>establishedThisTurn</c> is the sole determinant (<c>handoff ? establishedThisTurn : ...</c>).
    /// Keyed to the output file, a handoff turn that answered without writing one was recorded as NOT
    /// established — the new id was real and resumable, and AER threw it away. This asserts the
    /// corrected direction. The handoff must be TO <c>claude</c>: the recovered answer arrives on
    /// stdout, which is only captured for claude, so the same turn handed to agy still has no signal
    /// (#545).
    /// </remarks>
    [Fact]
    public async Task A_handoff_turn_that_answers_without_writing_a_file_is_still_established()
    {
        var start = new StartSessionRequest(
            Adapter: "gemini",
            TaskName: "handoff-nofile-" + Guid.NewGuid().ToString("N"),
            InitialMessage: "turn one",
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
            Message: "switch to claude " + SessionTurnStubAdapter.NoOutputFileSentinel,
            Adapter: "claude");
        var sendResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/send", send, TestContext.Current.CancellationToken);
        Assert.True(sendResponse.IsSuccessStatusCode, $"send failed: {sendResponse.StatusCode}");

        var metadata = await PollForTurnsAsync(started.SessionId, 2);
        var handoffTurn = metadata.Turns[^1];

        Assert.True(handoffTurn.VendorHandoffSynthesized,
            "this turn was not a handoff, so it does not exercise the branch under test");
        Assert.True(metadata.VendorSessionEstablished,
            "a handoff turn that answered was recorded as unestablished because it wrote no file -- "
            + "the freshly minted vendor session id is real and resumable, and discarding it makes "
            + "the next turn start over (#537)");
    }

    /// <summary>
    /// The control for agy/gemini (#545). When the worker writes its output file, the gemini session is
    /// marked established and the second turn resumes. Without this passing, the agy measurement below
    /// proves nothing.
    /// </summary>
    [Fact]
    public async Task An_agy_session_whose_worker_writes_the_file_resumes_on_the_second_turn()
    {
        var (metadata, secondTurn) = await TwoTurnsAsync(withOutputFile: true, adapter: "gemini");

        Assert.True(metadata.VendorSessionEstablished,
            "the agy control session was not marked established, so nothing discriminates for agy");
        Assert.True(secondTurn.NativeSessionResumed,
            "the agy control's second turn did not resume, so a non-resume below proves nothing");
    }

    /// <summary>
    /// The measurement for agy/gemini (#545). Same shape, one variable: the agy worker succeeds and
    /// writes no output file, establishing the session via the scraped conversation id instead.
    /// </summary>
    [Fact]
    public async Task An_agy_session_whose_worker_writes_no_file_still_carries_continuity()
    {
        var (metadata, secondTurn) = await TwoTurnsAsync(withOutputFile: false, adapter: "gemini");

        Assert.True(metadata.VendorSessionEstablished,
            "the agy vendor ran and answered via conversation id on both turns, but AER never recorded "
            + "the session as established -- continuity for agy was keyed to a file write (#545)");
        Assert.True(secondTurn.NativeSessionResumed,
            "turn 2 did not resume the agy vendor session, so a directory-less agy chat starts fresh every "
            + "turn and carries no memory (#545)");
        // #837: agy's log line trails the id with a comma; the scrape must not capture it.
        Assert.Equal(SessionTurnStubAdapter.StubAgyConversationId, metadata.CurrentVendorSessionId);
    }

    /// <summary>
    /// #545, found by an independent agy review pass (confirmed via a reconciled empirical repro,
    /// not just the review's own static reading): establishment was keyed to
    /// <c>vendorSessionId != null</c>, which stays true on every turn after the one that first
    /// established a session (the variable is deliberately never cleared -- see
    /// <c>agyLogFreshThisTurn</c>'s doc comment in <c>Program.cs</c>). So a SECOND turn that
    /// genuinely produced nothing at all was still reported established, with its real (blank)
    /// outcome silently overwritten. Turn 1 must actually establish for this to be a meaningful
    /// measurement -- a version of this test where turn 1 also fails to establish would pass for
    /// the wrong reason (nothing to carry over stale), which is exactly what happened before the
    /// log-write dispatch bug earlier in #545 was fixed.
    /// </summary>
    [Fact]
    public async Task A_second_agy_turn_that_produces_nothing_is_not_misreported_as_established()
    {
        var start = new StartSessionRequest(
            Adapter: "gemini",
            TaskName: "agy-silent-second-turn-" + Guid.NewGuid().ToString("N"),
            InitialMessage: "turn one " + SessionTurnStubAdapter.AgyNoOutputFileSentinel,
            SafetyCeiling: 200);

        var startResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/start", start, TestContext.Current.CancellationToken);
        Assert.True(startResponse.IsSuccessStatusCode, $"session start failed: {startResponse.StatusCode}");
        var started = await startResponse.Content.ReadFromJsonAsync<SessionMetadata>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(started);
        var firstMetadata = await PollForTurnsAsync(started.SessionId, 1);
        Assert.True(firstMetadata.VendorSessionEstablished,
            "turn 1 did not establish, so this test cannot discriminate 'stale carry-over' from " +
            "'nothing ever established' -- see this test's own doc comment");

        var send = new SendSessionMessageRequest(
            SessionId: started.SessionId,
            Message: SessionTurnStubAdapter.AgySilentSuccessSentinel,
            Adapter: "gemini");
        var sendResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/send", send, TestContext.Current.CancellationToken);
        Assert.True(sendResponse.IsSuccessStatusCode, $"send failed: {sendResponse.StatusCode}");

        var metadata = await PollForTurnsAsync(started.SessionId, 2);
        var secondTurn = metadata.Turns[^1];

        Assert.False(string.IsNullOrWhiteSpace(secondTurn.ErrorMessage),
            "turn 2 produced nothing at all (no file, no fresh conversation= line) but ErrorMessage " +
            "is blank -- establishment is wrongly keyed to a conversation id existing at all, carried " +
            "over from turn 1, rather than to what THIS turn actually produced (#545)");
    }

    private async Task<(SessionMetadata Metadata, SessionTurn SecondTurn)> TwoTurnsAsync(bool withOutputFile, string adapter = "claude")
    {
        var sentinel = string.Equals(adapter, "gemini", StringComparison.OrdinalIgnoreCase)
            ? SessionTurnStubAdapter.AgyNoOutputFileSentinel
            : SessionTurnStubAdapter.NoOutputFileSentinel;
        var suffix = withOutputFile ? "" : " " + sentinel;

        var start = new StartSessionRequest(
            Adapter: adapter,
            TaskName: $"continuity-{adapter}-" + Guid.NewGuid().ToString("N"),
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
            Message: "turn two" + suffix,
            Adapter: adapter);
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
