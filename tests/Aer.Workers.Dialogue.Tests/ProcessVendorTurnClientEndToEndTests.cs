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
            var responder = StubVendorScripts.EchoingSuffix(scriptDirectory, "responder", "agy", "You are the critic.", " [reviewed]");
            var config = new DialogueWorkerConfig(
                SeedPrompt: "Design a cache.",
                TurnBudget: 3,
                FinalOutputName: "transcript-summary.md",
                Participants: [initiator, responder]);

            var runner = new DialogueRunner(new ProcessVendorTurnClient());
            var turns = await runner.RunAsync(config, outputDirectory, TestContext.Current.CancellationToken);

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
            var lines = await File.ReadAllLinesAsync(transcriptPath, TestContext.Current.CancellationToken);
            Assert.Equal(3, lines.Length);

            var finalOutputPath = Path.Combine(outputDirectory, "transcript-summary.md");
            Assert.True(File.Exists(finalOutputPath));
            Assert.Equal(turns[^1].Text, await File.ReadAllTextAsync(finalOutputPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DirectoryCleanup.DeleteRecursively(root);
            }
        }
    }

    [Fact]
    public async Task A_real_stub_process_exiting_non_zero_fails_the_exchange_and_writes_no_final_output()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dialogue-e2e-{Guid.NewGuid():N}");
        var scriptDirectory = Path.Combine(root, "scripts");
        var outputDirectory = Path.Combine(root, "output");
        try
        {
            var initiator = StubVendorScripts.EchoingSuffix(scriptDirectory, "initiator", "claude", "You are the architect.", " [drafted]");
            var responder = StubVendorScripts.ExitingWithCode(scriptDirectory, "responder", "agy", "You are the critic.", exitCode: 1, stderrText: "quota exhausted");
            var config = new DialogueWorkerConfig(
                SeedPrompt: "Design a cache.",
                TurnBudget: 4,
                FinalOutputName: "transcript-summary.md",
                Participants: [initiator, responder]);

            var runner = new DialogueRunner(new ProcessVendorTurnClient());

            var ex = await Assert.ThrowsAsync<DialogueExecutionException>(() => runner.RunAsync(config, outputDirectory, TestContext.Current.CancellationToken));

            Assert.Contains("quota exhausted", ex.Message);
            Assert.False(File.Exists(Path.Combine(outputDirectory, "transcript-summary.md")));

            var transcriptPath = Path.Combine(outputDirectory, "transcript.jsonl");
            Assert.Single(await File.ReadAllLinesAsync(transcriptPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DirectoryCleanup.DeleteRecursively(root);
            }
        }
    }

    [Fact]
    public async Task A_real_stub_process_producing_no_output_fails_the_exchange()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dialogue-e2e-{Guid.NewGuid():N}");
        var scriptDirectory = Path.Combine(root, "scripts");
        var outputDirectory = Path.Combine(root, "output");
        try
        {
            var initiator = StubVendorScripts.ProducingEmptyOutput(scriptDirectory, "initiator", "claude", "You are the architect.");
            var responder = StubVendorScripts.EchoingSuffix(scriptDirectory, "responder", "agy", "You are the critic.", " [reviewed]");
            var config = new DialogueWorkerConfig(
                SeedPrompt: "Design a cache.",
                TurnBudget: 4,
                FinalOutputName: "transcript-summary.md",
                Participants: [initiator, responder]);

            var runner = new DialogueRunner(new ProcessVendorTurnClient());

            var ex = await Assert.ThrowsAsync<DialogueExecutionException>(() => runner.RunAsync(config, outputDirectory, TestContext.Current.CancellationToken));

            Assert.Contains("no text", ex.Message);
            Assert.False(File.Exists(Path.Combine(outputDirectory, "transcript-summary.md")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DirectoryCleanup.DeleteRecursively(root);
            }
        }
    }

    /// <summary>
    /// Decision 0039's replacement for the old "long prompt survives via {PROMPT_FILE}" test:
    /// {PROMPT_FILE} is retired, and a bounded per-turn prompt (preamble + seed or the single
    /// immediately preceding reply) should never approach the oversize guard's threshold. A huge
    /// preamble is the one thing that can still make a single turn's prompt large regardless of
    /// bounding, so it is exactly what should trip the guard now instead of silently reaching argv.
    /// </summary>
    [Fact]
    public async Task A_participant_whose_preamble_alone_exceeds_the_oversize_guard_fails_the_exchange()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dialogue-e2e-{Guid.NewGuid():N}");
        var scriptDirectory = Path.Combine(root, "scripts");
        var outputDirectory = Path.Combine(root, "output");
        try
        {
            var oversizePreamble = new string('x', ProcessVendorTurnClient.MaxArgumentLength + 1);
            var initiator = StubVendorScripts.EchoingSuffix(scriptDirectory, "initiator", "claude", oversizePreamble, " [drafted]");
            var responder = StubVendorScripts.EchoingSuffix(scriptDirectory, "responder", "agy", "Responder preamble", " [reviewed]");
            var config = new DialogueWorkerConfig(
                SeedPrompt: "Design a cache.",
                TurnBudget: 1,
                FinalOutputName: "transcript-summary.md",
                Participants: [initiator, responder]);

            var runner = new DialogueRunner(new ProcessVendorTurnClient());

            await Assert.ThrowsAsync<DialogueArgumentTooLargeException>(() => runner.RunAsync(config, outputDirectory, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DirectoryCleanup.DeleteRecursively(root);
            }
        }
    }
}
