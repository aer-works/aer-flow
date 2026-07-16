using System.Text.Json;
using Aer.Workers.Dialogue;

namespace Aer.Workers.Dialogue.Tests;

public class DialogueRunnerTests
{
    private static DialogueWorkerConfig BuildConfig(int turnBudget) => new(
        SeedPrompt: "seed",
        TurnBudget: turnBudget,
        FinalOutputName: "final.md",
        StopSentinel: null,
        Initiator: new DialogueParticipant("initiator", "claude", null, "Initiator preamble", "stub-claude", ["{PROMPT}"]),
        Responder: new DialogueParticipant("responder", "gemini", null, "Responder preamble", "stub-gemini", ["{PROMPT}"]));

    [Fact]
    public async Task Runs_exactly_TurnBudget_turns_alternating_speakers()
    {
        var client = new RecordingTurnClient();
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
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Threads_each_turns_text_into_the_next_turns_prompt()
    {
        var client = new RecordingTurnClient();
        var runner = new DialogueRunner(client);
        var outputDirectory = CreateTempDir();
        try
        {
            var turns = await runner.RunAsync(BuildConfig(3), outputDirectory);

            Assert.Contains("seed", turns[0].Prompt);
            Assert.Contains(turns[0].Text, turns[1].Prompt);
            Assert.Contains(turns[1].Text, turns[2].Prompt);
            Assert.Contains("Initiator preamble", turns[0].Prompt);
            Assert.Contains("Responder preamble", turns[1].Prompt);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Writes_a_schema_valid_transcript_and_the_declared_final_output()
    {
        var client = new RecordingTurnClient();
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
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dialogue-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A stub <see cref="IVendorTurnClient"/> returning a deterministic, distinguishable response per call, without spawning any process.</summary>
    private sealed class RecordingTurnClient : IVendorTurnClient
    {
        private int _callCount;

        public Task<string> SendTurnAsync(DialogueParticipant participant, string prompt, CancellationToken cancellationToken = default)
        {
            _callCount++;
            return Task.FromResult($"response-{_callCount}-from-{participant.Role}");
        }
    }
}
