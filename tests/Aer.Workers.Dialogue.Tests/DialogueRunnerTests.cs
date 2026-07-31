using Aer.Workers.Dialogue.Tests.TestSupport;
using System.Text.Json;
using Aer.Workers.Dialogue;

namespace Aer.Workers.Dialogue.Tests;

public class DialogueRunnerTests
{
    private static DialogueWorkerConfig BuildConfig(int turnBudget) => new(
        SeedPrompt: "seed",
        TurnBudget: turnBudget,
        FinalOutputName: "final.md",
        Participants:
        [
            // These exercise turn sequencing and never needed a vendor identity; stub-claude is not
            // claude. See DialogueWorkerAdapter.Gate for what declaring one now costs (#703).
            new DialogueParticipant("initiator", "stub-claude", null, "Initiator preamble", "stub-claude", ["{PROMPT}"]),
            new DialogueParticipant("responder", "stub-gemini", null, "Responder preamble", "stub-gemini", ["{PROMPT}"]),
        ]);

    private static DialogueWorkerConfig BuildThreePartyConfig(int turnBudget) => BuildConfig(
        turnBudget,
        [
            new DialogueParticipant("first", "stub-a", null, "First preamble", "stub-a", ["{PROMPT}"]),
            new DialogueParticipant("second", "stub-b", null, "Second preamble", "stub-b", ["{PROMPT}"]),
            new DialogueParticipant("third", "stub-c", null, "Third preamble", "stub-c", ["{PROMPT}"]),
        ]);

    private static DialogueWorkerConfig BuildConfig(int turnBudget, IReadOnlyList<DialogueParticipant> participants) => new(
        SeedPrompt: "seed",
        TurnBudget: turnBudget,
        FinalOutputName: "final.md",
        Participants: participants);

    private static DialogueWorkerConfig BuildConfig(int turnBudget, FinalOutputMode finalOutputMode) => new(
        SeedPrompt: "seed",
        TurnBudget: turnBudget,
        FinalOutputName: "final.md",
        Participants:
        [
            new DialogueParticipant("initiator", "stub-claude", null, "Initiator preamble", "stub-claude", ["{PROMPT}"]),
            new DialogueParticipant("responder", "stub-gemini", null, "Responder preamble", "stub-gemini", ["{PROMPT}"]),
        ],
        FinalOutputMode: finalOutputMode);

    [Fact]
    public async Task Runs_exactly_TurnBudget_turns_alternating_speakers()
    {
        var client = new ScriptedTurnClient(callIndex => new VendorTurnResult($"response-{callIndex}", 0, ""));
        var runner = new DialogueRunner(client);
        var outputDirectory = CreateTempDir();
        try
        {
            var turns = await runner.RunAsync(BuildConfig(4), outputDirectory);

            Assert.Equal(4, turns.Count);
            Assert.Equal(["initiator", "responder", "initiator", "responder"], turns.Select(t => t.Role));
            // Two distinct stub vendors rather than one repeated, so alternation stays visible without
            // either participant claiming to be a vendor it is not (#703).
            Assert.Equal(["stub-claude", "stub-gemini", "stub-claude", "stub-gemini"], turns.Select(t => t.Vendor));
            Assert.Equal([1, 2, 3, 4], turns.Select(t => t.Sequence));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task Each_turns_prompt_is_bounded_to_preamble_plus_the_immediately_preceding_reply()
    {
        // Decision 0039: no more full-transcript resend. Each turn carries exactly what its
        // speaker has not yet seen: the seed on a participant's own FIRST turn (its fresh vendor
        // session knows nothing yet — the original full-history prompt gave everyone the seed,
        // and dropping it for the second speaker was a silent regression in this branch's first
        // cut), then only the turns since that speaker last spoke.
        var client = new ScriptedTurnClient(callIndex => new VendorTurnResult($"response-{callIndex}", 0, ""));
        var runner = new DialogueRunner(client);
        var outputDirectory = CreateTempDir();
        try
        {
            var turns = await runner.RunAsync(BuildConfig(4), outputDirectory);

            Assert.Contains("seed", turns[0].Prompt);
            Assert.Contains("Initiator preamble", turns[0].Prompt);

            // The responder's first turn: a fresh session, so it gets the seed AND the opener's
            // reply — everything it has never seen.
            Assert.Contains("Responder preamble", turns[1].Prompt);
            Assert.Contains(turns[0].Text, turns[1].Prompt);
            Assert.Contains("seed", turns[1].Prompt);

            // Turn 3 carries turn 2's text, never turn 1's or the seed -- the bounded-increment
            // property this whole change exists to establish.
            Assert.Contains(turns[1].Text, turns[2].Prompt);
            Assert.DoesNotContain(turns[0].Text, turns[2].Prompt);
            Assert.DoesNotContain("seed", turns[2].Prompt);

            // Turn 4 carries turn 3's text, never turn 1's or turn 2's -- the property holds turn over
            // turn, not just once at turn 3.
            Assert.Contains(turns[2].Text, turns[3].Prompt);
            Assert.DoesNotContain(turns[0].Text, turns[3].Prompt);
            Assert.DoesNotContain(turns[1].Text, turns[3].Prompt);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task With_three_participants_each_turn_carries_every_turn_since_that_speaker_last_spoke()
    {
        // The review's confirmed high on this branch: threading only priorTurns[^1] means an
        // A/B/C round-robin never shows A what B said. The contract is "everything since this
        // speaker's own last turn" — bounded by the other participants' one turn each, never by
        // exchange length.
        var client = new ScriptedTurnClient(callIndex => new VendorTurnResult($"response-{callIndex}", 0, ""));
        var runner = new DialogueRunner(client);
        var outputDirectory = CreateTempDir();
        try
        {
            var turns = await runner.RunAsync(BuildThreePartyConfig(turnBudget: 5), outputDirectory);

            // C's first turn: seed plus BOTH prior turns (fresh session, seen nothing).
            Assert.Contains("seed", turns[2].Prompt);
            Assert.Contains(turns[0].Text, turns[2].Prompt);
            Assert.Contains(turns[1].Text, turns[2].Prompt);

            // A's second turn (turn 4): B's and C's intervening turns, NOT its own turn 1's text
            // as context and NOT the seed (A saw the seed on its first turn).
            Assert.Contains(turns[1].Text, turns[3].Prompt);
            Assert.Contains(turns[2].Text, turns[3].Prompt);
            Assert.DoesNotContain("seed", turns[3].Prompt);

            // B's second turn (turn 5): C's turn and A's second turn only — B's own first turn and
            // everything before it are its session's memory, not re-sent.
            Assert.Contains(turns[2].Text, turns[4].Prompt);
            Assert.Contains(turns[3].Text, turns[4].Prompt);
            Assert.DoesNotContain(turns[0].Text, turns[4].Prompt);
            Assert.DoesNotContain("seed", turns[4].Prompt);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task Each_participant_gets_its_own_session_id_established_on_its_first_turn_and_resumed_after()
    {
        // Decision 0039: DialogueRunner threads a per-participant session id, established (null in,
        // whatever the client returns out) on that participant's own first turn and resumed (the
        // previously-established id passed back in) on every one of its later turns -- independent
        // per participant, since the two sides are separate vendor sessions.
        var client = new ScriptedTurnClient(callIndex => new VendorTurnResult($"response-{callIndex}", 0, "", SessionId: $"session-for-call-{callIndex}"));
        var runner = new DialogueRunner(client);
        var outputDirectory = CreateTempDir();
        try
        {
            await runner.RunAsync(BuildConfig(4), outputDirectory);

            // Call 1 = initiator's first turn, call 2 = responder's first turn: both null in (nothing
            // established yet for either participant).
            Assert.Null(client.SessionIdsSeen[0]);
            Assert.Null(client.SessionIdsSeen[1]);

            // Call 3 = initiator's second turn: resumes what call 1 established, NOT what call 2
            // (the responder's own session) established.
            Assert.Equal("session-for-call-1", client.SessionIdsSeen[2]);

            // Call 4 = responder's second turn: resumes what call 2 established.
            Assert.Equal("session-for-call-2", client.SessionIdsSeen[3]);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task Writes_a_schema_valid_transcript_and_the_declared_final_output()
    {
        var client = new ScriptedTurnClient(callIndex => new VendorTurnResult($"response-{callIndex}", 0, ""));
        var runner = new DialogueRunner(client);
        var outputDirectory = CreateTempDir();
        try
        {
            var turns = await runner.RunAsync(BuildConfig(2), outputDirectory);

            var transcriptPath = Path.Combine(outputDirectory, "transcript.jsonl");
            Assert.True(File.Exists(transcriptPath));
            var lines = await File.ReadAllLinesAsync(transcriptPath);
            Assert.Equal(2, lines.Length);
            foreach (var line in lines)
            {
                var turn = JsonSerializer.Deserialize<TranscriptTurn>(line);
                Assert.NotNull(turn);
            }

            var finalOutputPath = Path.Combine(outputDirectory, "final.md");
            Assert.True(File.Exists(finalOutputPath));
            Assert.Equal(turns[^1].Text, await File.ReadAllTextAsync(finalOutputPath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task FinalOutputMode_FinalTurn_writes_only_the_last_turns_text()
    {
        var client = new ScriptedTurnClient(callIndex => new VendorTurnResult($"response-{callIndex}", 0, ""));
        var runner = new DialogueRunner(client);
        var outputDirectory = CreateTempDir();
        try
        {
            var turns = await runner.RunAsync(BuildConfig(2, FinalOutputMode.FinalTurn), outputDirectory);

            var finalOutputPath = Path.Combine(outputDirectory, "final.md");
            var finalOutput = await File.ReadAllTextAsync(finalOutputPath);
            Assert.Equal(turns[^1].Text, finalOutput);
            Assert.DoesNotContain("initiator", finalOutput);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task FinalOutputMode_Transcript_writes_the_full_role_attributed_exchange_in_order()
    {
        var client = new ScriptedTurnClient(callIndex => new VendorTurnResult($"response-{callIndex}", 0, ""));
        var runner = new DialogueRunner(client);
        var outputDirectory = CreateTempDir();
        try
        {
            var turns = await runner.RunAsync(BuildConfig(3, FinalOutputMode.Transcript), outputDirectory);

            var finalOutputPath = Path.Combine(outputDirectory, "final.md");
            var finalOutput = await File.ReadAllTextAsync(finalOutputPath);

            // Every turn's role and text show up, and the last turn's text does not appear alone —
            // this is the whole exchange, not just the final turn.
            Assert.Contains($"{turns[0].Role}: {turns[0].Text}", finalOutput);
            Assert.Contains($"{turns[1].Role}: {turns[1].Text}", finalOutput);
            Assert.Contains($"{turns[2].Role}: {turns[2].Text}", finalOutput);
            Assert.NotEqual(turns[^1].Text, finalOutput);

            // In order: turn 1's text appears before turn 2's, which appears before turn 3's.
            Assert.True(finalOutput.IndexOf(turns[0].Text, StringComparison.Ordinal)
                < finalOutput.IndexOf(turns[1].Text, StringComparison.Ordinal));
            Assert.True(finalOutput.IndexOf(turns[1].Text, StringComparison.Ordinal)
                < finalOutput.IndexOf(turns[2].Text, StringComparison.Ordinal));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task A_yield_call_ends_the_exchange_before_the_turn_budget_is_exhausted_concluded()
    {
        var outputDirectory = CreateTempDir();
        try
        {
            var config = BuildConfig(6);
            var participants = config.Participants;
            var captureFilePath = Path.Combine(outputDirectory, $"yield-capture-{participants[1].Role}.json");

            var client = new ScriptedTurnClient(callIndex =>
            {
                if (callIndex == 2)
                {
                    Directory.CreateDirectory(outputDirectory);
                    File.WriteAllText(captureFilePath, "{\"Outcome\":\"concluded\",\"Note\":\"agreed\"}");
                }

                return new VendorTurnResult($"response-{callIndex}", 0, "");
            });
            var runner = new DialogueRunner(client);

            var turns = await runner.RunAsync(config, outputDirectory);

            Assert.Equal(2, turns.Count);
            Assert.Equal(2, client.CallCount);
            Assert.Equal("concluded", turns[^1].YieldOutcome);
            Assert.Equal("agreed", turns[^1].YieldNote);
            Assert.False(File.Exists(captureFilePath), "the capture file must be consumed (deleted) once read");

            var finalOutputPath = Path.Combine(outputDirectory, "final.md");
            Assert.Equal("response-2", await File.ReadAllTextAsync(finalOutputPath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task A_yield_call_ends_the_exchange_with_a_stalemate_outcome()
    {
        var outputDirectory = CreateTempDir();
        try
        {
            var config = BuildConfig(6);
            var participants = config.Participants;
            var captureFilePath = Path.Combine(outputDirectory, $"yield-capture-{participants[0].Role}.json");

            var client = new ScriptedTurnClient(callIndex =>
            {
                if (callIndex == 1)
                {
                    Directory.CreateDirectory(outputDirectory);
                    File.WriteAllText(captureFilePath, "{\"Outcome\":\"stalemate\",\"Note\":null}");
                }

                return new VendorTurnResult($"response-{callIndex}", 0, "");
            });
            var runner = new DialogueRunner(client);

            var turns = await runner.RunAsync(config, outputDirectory);

            Assert.Single(turns);
            Assert.Equal("stalemate", turns[^1].YieldOutcome);
            Assert.Null(turns[^1].YieldNote);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task A_participants_yield_is_never_attributed_to_a_different_participants_turn()
    {
        // The responder's capture file already exists before the exchange starts (as if it were
        // written moments earlier by something else entirely). Turn 1 belongs to the initiator, whose
        // own capture path is different -- the responder's pre-existing file must never be checked
        // during turn 1, must not end the exchange early, and must be untouched afterward.
        var outputDirectory = CreateTempDir();
        Directory.CreateDirectory(outputDirectory);
        try
        {
            // TurnBudget 1: only the initiator ever speaks, so the responder's own turn -- and any
            // consumption of the responder's capture file -- never happens in this run at all.
            var config = BuildConfig(1);
            var responderCapture = Path.Combine(outputDirectory, $"yield-capture-{config.Participants[1].Role}.json");
            File.WriteAllText(responderCapture, "{\"Outcome\":\"concluded\",\"Note\":\"not the initiator's turn\"}");

            var client = new ScriptedTurnClient(callIndex => new VendorTurnResult($"response-{callIndex}", 0, ""));
            var runner = new DialogueRunner(client);

            var turns = await runner.RunAsync(config, outputDirectory);

            Assert.Single(turns);
            Assert.Null(turns[0].YieldOutcome);
            Assert.True(File.Exists(responderCapture), "a file belonging to a participant who hasn't spoken yet must not be consumed");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task A_stale_capture_file_from_before_the_exchange_started_is_never_read()
    {
        // A capture file for the initiator role, left over from some prior run in the same directory,
        // must not be picked up as if the initiator yielded on turn 1 of THIS exchange -- DialogueRunner
        // only ever reads a participant's file after that participant's own turn in the current run.
        var outputDirectory = CreateTempDir();
        try
        {
            var config = BuildConfig(2);
            var initiatorCapture = Path.Combine(outputDirectory, $"yield-capture-{config.Participants[0].Role}.json");
            File.WriteAllText(initiatorCapture, "{\"Outcome\":\"concluded\",\"Note\":\"stale from a prior run\"}");

            var client = new ScriptedTurnClient(callIndex => new VendorTurnResult($"response-{callIndex}", 0, ""));
            var runner = new DialogueRunner(client);

            var turns = await runner.RunAsync(config, outputDirectory);

            // The stale file IS consumed on turn 1 -- same participant, same path, and DialogueRunner
            // has no way to distinguish "written before this run" from "written during turn 1", which
            // is why capture files must live under a fresh per-run outputDirectory in production. The
            // assertion this test actually protects is narrower and still meaningful: whatever gets
            // read is attributed to turn 1 (the initiator, whose file it is), never to turn 2's
            // different participant.
            Assert.Single(turns);
            Assert.Equal("concluded", turns[0].YieldOutcome);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task A_non_zero_exit_from_a_vendor_CLI_fails_the_whole_exchange()
    {
        var client = new ScriptedTurnClient(callIndex => callIndex == 2
            ? new VendorTurnResult("", 1, "boom")
            : new VendorTurnResult($"response-{callIndex}", 0, ""));
        var runner = new DialogueRunner(client);
        var outputDirectory = CreateTempDir();
        try
        {
            var ex = await Assert.ThrowsAsync<DialogueExecutionException>(
                () => runner.RunAsync(BuildConfig(6), outputDirectory));

            Assert.Contains("2", ex.Message);
            Assert.Contains("responder", ex.Message);
            Assert.Contains("boom", ex.Message);

            // The failing turn's own line is never appended, but the one turn that succeeded before
            // it stays on disk as a forensic record (§18.2's "no partial resumption" tradeoff).
            var transcriptPath = Path.Combine(outputDirectory, "transcript.jsonl");
            var lines = await File.ReadAllLinesAsync(transcriptPath);
            Assert.Single(lines);

            Assert.False(File.Exists(Path.Combine(outputDirectory, "final.md")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task An_empty_turn_mid_exchange_fails_the_whole_exchange()
    {
        var client = new ScriptedTurnClient(callIndex => callIndex == 2
            ? new VendorTurnResult("   ", 0, "")
            : new VendorTurnResult($"response-{callIndex}", 0, ""));
        var runner = new DialogueRunner(client);
        var outputDirectory = CreateTempDir();
        try
        {
            var ex = await Assert.ThrowsAsync<DialogueExecutionException>(
                () => runner.RunAsync(BuildConfig(6), outputDirectory));

            Assert.Contains("no text", ex.Message);
            Assert.False(File.Exists(Path.Combine(outputDirectory, "final.md")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task Three_or_more_participants_round_robin_in_list_order()
    {
        var client = new ScriptedTurnClient(callIndex => new VendorTurnResult($"response-{callIndex}", 0, ""));
        var participants = new List<DialogueParticipant>
        {
            new("first", "claude", null, "First preamble", "stub-claude", ["{PROMPT}"]),
            new("second", "gemini", null, "Second preamble", "stub-gemini", ["{PROMPT}"]),
            new("third", "claude", null, "Third preamble", "stub-claude-2", ["{PROMPT}"]),
        };
        var runner = new DialogueRunner(client);
        var outputDirectory = CreateTempDir();
        try
        {
            var turns = await runner.RunAsync(BuildConfig(7, participants), outputDirectory);

            Assert.Equal(7, turns.Count);
            Assert.Equal(
                ["first", "second", "third", "first", "second", "third", "first"],
                turns.Select(t => t.Role));
            Assert.Equal([1, 2, 3, 4, 5, 6, 7], turns.Select(t => t.Sequence));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task A_turn_budget_above_the_hard_ceiling_is_clamped_to_the_ceiling()
    {
        var client = new ScriptedTurnClient(callIndex => new VendorTurnResult($"response-{callIndex}", 0, ""));
        var runner = new DialogueRunner(client);
        var outputDirectory = CreateTempDir();
        try
        {
            var turns = await runner.RunAsync(BuildConfig(DialogueWorkerConfig.HardTurnCeiling * 10), outputDirectory);

            Assert.Equal(DialogueWorkerConfig.HardTurnCeiling, turns.Count);
            Assert.Equal(DialogueWorkerConfig.HardTurnCeiling, client.CallCount);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task Timed_out_turn_reports_configured_ceiling_and_role()
    {
        var client = new ScriptedTurnClient(callIndex => new VendorTurnResult("", 124, "Turn timed out...", TimedOut: true));
        var runner = new DialogueRunner(client);
        var outputDirectory = CreateTempDir();
        try
        {
            var config = BuildConfig(2);
            var ex = await Assert.ThrowsAsync<DialogueExecutionException>(
                () => runner.RunAsync(config, outputDirectory));

            Assert.Contains("timed out", ex.Message);
            Assert.Contains("initiator", ex.Message);
            Assert.Contains((config.TurnTimeout ?? DialogueWorkerConfig.DefaultTurnTimeout).ToString(), ex.Message);
            Assert.DoesNotContain("exited with code 124", ex.Message);

        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task Real_process_exiting_124_is_not_reported_as_timeout()
    {
        var client = new ScriptedTurnClient(callIndex => new VendorTurnResult("", 124, "some vendor error", TimedOut: false));
        var runner = new DialogueRunner(client);
        var outputDirectory = CreateTempDir();
        try
        {
            var config = BuildConfig(2);
            var ex = await Assert.ThrowsAsync<DialogueExecutionException>(
                () => runner.RunAsync(config, outputDirectory));

            Assert.Contains("exited with code 124", ex.Message);
            Assert.DoesNotContain("timed out", ex.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    private static string CreateTempDir()

    {
        var path = Path.Combine(Path.GetTempPath(), $"dialogue-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A stub <see cref="IVendorTurnClient"/> whose result per call is driven entirely by the supplied function, keyed by 1-based call index, without spawning any process.</summary>
    private sealed class ScriptedTurnClient(Func<int, VendorTurnResult> resultForCall) : IVendorTurnClient
    {
        public int CallCount { get; private set; }

        /// <summary>Every <paramref name="sessionId"/> this client was called with, in call order — lets a test assert the fresh-vs-resumed sequence a caller passed in.</summary>
        public List<string?> SessionIdsSeen { get; } = [];

        public Task<VendorTurnResult> SendTurnAsync(
            DialogueParticipant participant, string prompt, string? sessionId = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            SessionIdsSeen.Add(sessionId);
            return Task.FromResult(resultForCall(CallCount));
        }
    }
}
