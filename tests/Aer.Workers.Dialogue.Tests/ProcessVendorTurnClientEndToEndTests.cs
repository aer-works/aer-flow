using Aer.Workers.Dialogue;
using Aer.Workers.Dialogue.Tests.TestSupport;

namespace Aer.Workers.Dialogue.Tests;

/// <summary>
/// M17 Phase 2's (#165) actual deliverable proven end to end: a real dialogue exchange, spawning
/// real (stub) processes via <see cref="ProcessVendorTurnClient"/>, no mocking of process spawning
/// itself — the same "no mocking the real seam" bar <c>Aer.Flow.Tests.EndToEnd</c> holds for Core
/// dispatch.
/// </summary>
public class ProcessVendorTurnClientEndToEndTests
{
    [Fact]
    public async Task A_full_exchange_runs_against_stub_vendor_CLIs_and_writes_a_schema_valid_transcript()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dialogue-e2e-{Guid.NewGuid():N}");
        var scriptDirectory = Path.Combine(root, "scripts");
        var outputDirectory = Path.Combine(root, "output");
        try
        {
            var initiator = StubVendorScripts.EchoingSuffix(scriptDirectory, "initiator", "claude", "You are the architect.", " [drafted]");
            var responder = StubVendorScripts.EchoingSuffix(scriptDirectory, "responder", "gemini", "You are the critic.", " [reviewed]");
            var config = new DialogueWorkerConfig(
                SeedPrompt: "Design a cache.",
                TurnBudget: 3,
                FinalOutputName: "transcript-summary.md",
                StopSentinel: null,
                Initiator: initiator,
                Responder: responder);

            var runner = new DialogueRunner(new ProcessVendorTurnClient());
            var turns = await runner.RunAsync(config, outputDirectory);

            Assert.Equal(3, turns.Count);
            Assert.Equal(["initiator", "responder", "initiator"], turns.Select(t => t.Role));

            // Turn 1: initiator's stub echoes its own prompt (preamble + seed) with its suffix.
            Assert.EndsWith("[drafted]", turns[0].Text);
            Assert.Contains("Design a cache.", turns[0].Text);

            // Turn 2: responder's stub echoed turn 1's *text* threaded forward, with its own suffix.
            Assert.EndsWith("[reviewed]", turns[1].Text);
            Assert.Contains("[drafted]", turns[1].Text);

            // Turn 3: initiator again, now threading turn 2's text.
            Assert.EndsWith("[drafted]", turns[2].Text);
            Assert.Contains("[reviewed]", turns[2].Text);

            var transcriptPath = Path.Combine(outputDirectory, "transcript.jsonl");
            var lines = await File.ReadAllLinesAsync(transcriptPath);
            Assert.Equal(3, lines.Length);

            var finalOutputPath = Path.Combine(outputDirectory, "transcript-summary.md");
            Assert.True(File.Exists(finalOutputPath));
            Assert.Equal(turns[^1].Text, await File.ReadAllTextAsync(finalOutputPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
