using Aer.Workers.Dialogue.Tests.TestSupport;
using System.Text.Json;
using Aer.Workers.Dialogue;

namespace Aer.Workers.Dialogue.Tests;

public class DialogueRunnerTests
{
    private static DialogueWorkerConfig BuildConfig(int turnBudget, string? stopSentinel = null) => new(
        SeedPrompt: "seed",
        TurnBudget: turnBudget,
        FinalOutputName: "final.md",
        StopSentinel: stopSentinel,
        Participants:
        [
            new DialogueParticipant("initiator", "claude", null, "Initiator preamble", "stub-claude", ["{PROMPT}"]),
            new DialogueParticipant("responder", "gemini", null, "Responder preamble", "stub-gemini", ["{PROMPT}"]),
        ]);

    private static DialogueWorkerConfig BuildConfig(int turnBudget, IReadOnlyList<DialogueParticipant> participants, string? stopSentinel = null) => new(
        SeedPrompt: "seed",
        TurnBudget: turnBudget,
        FinalOutputName: "final.md",
        StopSentinel: stopSentinel,
        Participants: participants);

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
            Assert.Equal(["claude", "gemini", "claude", "gemini"], turns.Select(t => t.Vendor));
            Assert.Equal([1, 2, 3, 4], turns.Select(t => t.Sequence));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public async Task Threads_the_full_transcript_so_far_into_each_next_turns_prompt()
    {
        var client = new ScriptedTurnClient(callIndex => new VendorTurnResult($"response-{callIndex}", 0, ""));
        var runner = new DialogueRunner(client);
        var outputDirectory = CreateTempDir();
        try
        {
            var turns = await runner.RunAsync(BuildConfig(3), outputDirectory);

            Assert.Contains("seed", turns[0].Prompt);
            Assert.Contains("Initiator preamble", turns[0].Prompt);
            Assert.Contains("Responder preamble", turns[1].Prompt);

            // Turn 3's prompt carries turn 1's *and* turn 2's text, not just the immediately preceding turn.
            Assert.Contains(turns[0].Text, turns[2].Prompt);
            Assert.Contains(turns[1].Text, turns[2].Prompt);
            Assert.Contains("seed", turns[2].Prompt);
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
    public async Task A_stop_sentinel_ends_the_exchange_before_the_turn_budget_is_exhausted()
    {
        var client = new ScriptedTurnClient(callIndex => callIndex == 2
            ? new VendorTurnResult("Looks good. STOP_DIALOGUE", 0, "")
            : new VendorTurnResult($"response-{callIndex}", 0, ""));
        var runner = new DialogueRunner(client);
        var outputDirectory = CreateTempDir();
        try
        {
            var turns = await runner.RunAsync(BuildConfig(6, stopSentinel: "STOP_DIALOGUE"), outputDirectory);

            Assert.Equal(2, turns.Count);
            Assert.Equal(2, client.CallCount);
            Assert.Equal("Looks good.", turns[^1].Text);
            Assert.DoesNotContain("STOP_DIALOGUE", turns[^1].Text);

            var finalOutputPath = Path.Combine(outputDirectory, "final.md");
            Assert.Equal("Looks good.", await File.ReadAllTextAsync(finalOutputPath));
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

        public Task<VendorTurnResult> SendTurnAsync(DialogueParticipant participant, string prompt, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(resultForCall(CallCount));
        }
    }
}
