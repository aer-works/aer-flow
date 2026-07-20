using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Workers.Dialogue;

namespace Aer.Cli.SmokeTests;

/// <summary>
/// M17 Phase 5 (#168), half of the milestone's completion gate (the other half is
/// <c>Aer.Cli.Tests.DialogueDispatchEndToEndTests</c>, which already proves the same shape
/// unattended in default CI against stub vendor scripts): a workflow step bound to the
/// <c>"dialogue"</c> adapter, run through <see cref="RunCommand.ExecuteAsync"/> — the exact call
/// <c>Program.cs</c> makes — with the dialogue worker's two participants spawning the real,
/// authenticated <c>claude</c> and <c>agy</c> CLIs directly (no shell wrapper — see
/// <see cref="ProcessVendorTurnClient"/>), producing a real, live-vendor <c>transcript.jsonl</c> and
/// declared final output on disk. This is the first time this repo's own code runs a Case 2
/// dialogue worker against real models instead of stub scripts, mirroring what
/// <c>LiveClaudeRunSmokeTest</c>/<c>LiveMixedVendorPausedRunSmokeTest</c> proved for the two
/// per-role vendor adapters.
/// <para>
/// <b>Deliberately excluded from <c>AerFlow.slnx</c></b>, exactly like every other test in this
/// project: it never builds, restores, or runs as a side effect of <c>pixi run build</c>/<c>test</c>/
/// <c>lint</c>/<c>fmt-check</c>. It requires an authenticated <c>claude</c> CLI and an authenticated
/// <c>agy</c> CLI both on <c>PATH</c>, and outbound network access to both vendors' APIs. Invoke only
/// via <c>pixi run smoke-dialogue</c>; see <c>docs/runbooks/live-dialogue-smoke.md</c> for the full
/// runbook.
/// </para>
/// </summary>
public class LiveDialogueSmokeTest
{
    [Fact]
    public async Task A_dialogue_step_runs_to_completion_against_the_real_claude_and_agy_clis()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dialogue-smoke-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);
            var workflowFilePath = await WriteDialogueWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteDialogueBindingsAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);

            var finalState = (await RunCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default)).State;

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            var stepState = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Succeeded, stepState.Status);

            var outputDirectory = Path.Combine(taskDirectory, "artifacts", $"execution_{stepState.LatestExecutionId}");

            var transcriptPath = Path.Combine(outputDirectory, "transcript.jsonl");
            Assert.True(File.Exists(transcriptPath), $"Expected a transcript at '{transcriptPath}'.");
            var transcriptLines = await File.ReadAllLinesAsync(transcriptPath);
            Assert.Equal(2, transcriptLines.Length);
            var turns = transcriptLines.Select(line => JsonSerializer.Deserialize<TranscriptTurn>(line)!).ToList();
            Assert.Equal(["initiator", "responder"], turns.Select(t => t.Role));
            Assert.All(turns, turn => Assert.False(string.IsNullOrWhiteSpace(turn.Text)));

            var finalOutputPath = Path.Combine(outputDirectory, "verdict.md");
            Assert.True(File.Exists(finalOutputPath), $"Expected worker output at '{finalOutputPath}'.");
            Assert.False(string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(finalOutputPath)));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task<string> WriteDialogueWorkflowAsync(string directory)
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("live-dialogue-smoke"),
            1,
            [new WorkflowStepDefinition(new StepId("debate"), "debate", [], ["verdict.md"], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    /// <summary>
    /// A short, bounded exchange (<see cref="DialogueWorkerConfig.TurnBudget"/> of 2 — one turn per
    /// side) — this is a smoke test proving the wiring, not a real debate, so it stays cheap and fast
    /// exactly like <c>LiveClaudeRunSmokeTest</c>'s single-sentence draft prompt does for the same
    /// reason. Each participant spawns the real CLI directly (<see cref="DialogueParticipant.Command"/>/
    /// <see cref="DialogueParticipant.Args"/>, substituted by <see cref="ProcessVendorTurnClient"/> —
    /// no shell involved, so no quoting question the way <see cref="ClaudeWorkerAdapter"/>/
    /// <see cref="GeminiWorkerAdapter"/>'s top-level dispatch has), using the same flags those two
    /// adapters build for a one-shot text turn with no file I/O of its own.
    /// </summary>
    private static async Task<string> WriteDialogueBindingsAsync(string directory)
    {
        var dialogueConfig = new DialogueWorkerConfig(
            SeedPrompt: "In one sentence, name the single most important quality of a good workflow engine.",
            TurnBudget: 2,
            FinalOutputName: "verdict.md",
            StopSentinel: null,
            Participants:
            [
                new DialogueParticipant(
                    "initiator", "claude", "claude-haiku-4-5-20251001",
                    "You are debating in favor of the position. Respond in one sentence.",
                    "claude", ["-p", DialogueParticipant.PromptPlaceholder, "--allowedTools", "Write", "--output-format", "text", "--model", "claude-haiku-4-5-20251001"]),
                new DialogueParticipant(
                    "responder", "gemini", "gemini-3-flash",
                    "You are debating against the position. Respond in one sentence.",
                    "agy", ["-p", DialogueParticipant.PromptPlaceholder, "--mode", "accept-edits", "--model", "gemini-3-flash"]),
            ]);

        var dialogueConfigPath = Path.Combine(directory, "dialogue-config.json");
        await File.WriteAllTextAsync(dialogueConfigPath, JsonSerializer.Serialize(dialogueConfig));

        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["debate"] = new WorkerBindingConfigEntry(
                "dialogue",
                new WorkerContract("debate", [], [new ProducedOutput("verdict.md")], []),
                dialogueConfigPath,
                TimeSpan.FromMinutes(5)),
        };

        var bindingsPath = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(bindingsPath, JsonSerializer.Serialize(config));
        return bindingsPath;
    }
}
