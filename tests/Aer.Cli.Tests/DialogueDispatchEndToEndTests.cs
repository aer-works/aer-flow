using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Workers.Dialogue;

namespace Aer.Cli.Tests;

/// <summary>
/// M17 Phase 4's completion gate (#167): a workflow step bound to the <c>"dialogue"</c> adapter,
/// resolved through the real <see cref="WorkerAdapterRegistry.Default"/> (not a shell-stub test
/// adapter, unlike <see cref="RunCommandEndToEndTests"/>), run to terminal via <c>RunCommand.ExecuteAsync</c>
/// — the exact call <c>Program.cs</c> makes. This is what proves "dialogue-as-a-step, runnable from
/// CLI" rather than merely asserting <see cref="DialogueWorkerAdapter"/>'s resolved command shape
/// (<c>Aer.Adapters.Tests.DialogueWorkerAdapterTests</c>): the real <c>dotnet exec</c> spawn, the real
/// <c>Aer.Workers.Dialogue</c> executable, and its own child spawns of two stub vendor scripts (the
/// same "stub vendor CLIs" convention M17 Phase 2/3's own tests use) all actually run here. CI-safe on
/// every OS — no real vendor CLI or network access involved, exactly like every other default-CI gate
/// in this project.
/// </summary>
public class DialogueDispatchEndToEndTests
{
    [Fact]
    public async Task A_dialogue_step_runs_to_terminal_and_writes_the_transcript_and_final_output()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dialogue-e2e-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);
            var workflowFilePath = await WriteSingleDialogueStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteDialogueBindingsAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);

            var finalState = (await RunCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, cancellationToken: TestContext.Current.CancellationToken)).State;

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            var stepState = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Succeeded, stepState.Status);

            var outputDirectory = Path.Combine(taskDirectory, "artifacts", $"execution_{stepState.LatestExecutionId}");

            var transcriptPath = Path.Combine(outputDirectory, "transcript.jsonl");
            Assert.True(File.Exists(transcriptPath));
            var transcriptLines = await File.ReadAllLinesAsync(transcriptPath, TestContext.Current.CancellationToken);
            Assert.Equal(2, transcriptLines.Length);
            var turns = transcriptLines.Select(line => JsonSerializer.Deserialize<TranscriptTurn>(line)!).ToList();
            Assert.Equal(["initiator", "responder"], turns.Select(t => t.Role));
            Assert.Contains("Open with your position.", turns[0].Prompt);
            Assert.Contains(" from initiator", turns[0].Text);
            Assert.Contains(" from responder", turns[1].Text);

            var finalOutputPath = Path.Combine(outputDirectory, "verdict.md");
            Assert.True(File.Exists(finalOutputPath));
            Assert.Equal(turns[^1].Text, await File.ReadAllTextAsync(finalOutputPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    /// <summary>
    /// M23 Phase 1's own acceptance bar (#270): "one real multi-turn correspondence run end-to-end"
    /// for the N-party generalization specifically — three participants, five turns, dispatched
    /// through the same real engine/adapter/worker-binary path as the two-party test above (not just
    /// <c>Aer.Workers.Dialogue.Tests.DialogueRunnerTests</c>' isolated-runner unit coverage of
    /// round-robin sequencing).
    /// </summary>
    [Fact]
    public async Task A_three_party_dialogue_step_round_robins_to_terminal_through_the_real_engine()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dialogue-e2e-3party-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);
            var workflowFilePath = await WriteSingleDialogueStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreePartyDialogueBindingsAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);

            var finalState = (await RunCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default)).State;

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            var stepState = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Succeeded, stepState.Status);

            var outputDirectory = Path.Combine(taskDirectory, "artifacts", $"execution_{stepState.LatestExecutionId}");

            var transcriptPath = Path.Combine(outputDirectory, "transcript.jsonl");
            var transcriptLines = await File.ReadAllLinesAsync(transcriptPath);
            Assert.Equal(5, transcriptLines.Length);
            var turns = transcriptLines.Select(line => JsonSerializer.Deserialize<TranscriptTurn>(line)!).ToList();
            Assert.Equal(["architect", "critic", "arbiter", "architect", "critic"], turns.Select(t => t.Role));
            Assert.Equal([1, 2, 3, 4, 5], turns.Select(t => t.Sequence));

            var finalOutputPath = Path.Combine(outputDirectory, "verdict.md");
            Assert.Equal(turns[^1].Text, await File.ReadAllTextAsync(finalOutputPath));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task<string> WriteThreePartyDialogueBindingsAsync(string directory)
    {
        var scriptDirectory = Path.Combine(directory, "scripts");
        var dialogueConfig = new DialogueWorkerConfig(
            SeedPrompt: "Open with your position.",
            TurnBudget: 5,
            FinalOutputName: "verdict.md",
            StopSentinel: null,
            Participants:
            [
                EchoingParticipant(scriptDirectory, "architect", "claude", "You design."),
                EchoingParticipant(scriptDirectory, "critic", "gemini", "You critique."),
                EchoingParticipant(scriptDirectory, "arbiter", "claude", "You decide."),
            ]);

        var dialogueConfigPath = Path.Combine(directory, "dialogue-config.json");
        await File.WriteAllTextAsync(dialogueConfigPath, JsonSerializer.Serialize(dialogueConfig));

        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["debate"] = new WorkerBindingConfigEntry(
                "dialogue",
                new WorkerContract("debate", [], [new ProducedOutput("verdict.md")], []),
                dialogueConfigPath,
                TimeSpan.FromSeconds(30)),
        };

        var bindingsPath = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(bindingsPath, JsonSerializer.Serialize(config));
        return bindingsPath;
    }

    private static async Task<string> WriteSingleDialogueStepWorkflowAsync(string directory)
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("single-dialogue-step"),
            1,
            [new WorkflowStepDefinition(new StepId("debate"), "debate", [], ["verdict.md"], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteDialogueBindingsAsync(string directory)
    {
        var scriptDirectory = Path.Combine(directory, "scripts");
        var dialogueConfig = new DialogueWorkerConfig(
            SeedPrompt: "Open with your position.",
            TurnBudget: 2,
            FinalOutputName: "verdict.md",
            StopSentinel: null,
            Participants:
            [
                EchoingParticipant(scriptDirectory, "initiator", "claude", "You argue for."),
                EchoingParticipant(scriptDirectory, "responder", "gemini", "You argue against."),
            ]);

        var dialogueConfigPath = Path.Combine(directory, "dialogue-config.json");
        await File.WriteAllTextAsync(dialogueConfigPath, JsonSerializer.Serialize(dialogueConfig));

        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["debate"] = new WorkerBindingConfigEntry(
                "dialogue",
                new WorkerContract("debate", [], [new ProducedOutput("verdict.md")], []),
                dialogueConfigPath,
                TimeSpan.FromSeconds(30)),
        };

        var bindingsPath = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(bindingsPath, JsonSerializer.Serialize(config));
        return bindingsPath;
    }

    /// <summary>
    /// A participant whose stub CLI echoes the received prompt followed by <c>" from {role}"</c> — the
    /// same "stub vendor CLI" shape <c>Aer.Workers.Dialogue.Tests.TestSupport.StubVendorScripts</c>
    /// uses, duplicated here rather than shared across test assemblies (this codebase's existing
    /// convention — see e.g. <c>ShellCommandWorkerAdapter</c>, copied per test project rather than
    /// referenced across them). Windows uses <c>powershell -File</c>, not <c>cmd /c &lt;path&gt;</c>:
    /// <see cref="DialogueRunner"/> threads prior turns' text into each next prompt, so by turn 2 the
    /// prompt is genuinely multi-line, and <c>cmd.exe</c>'s own <c>/c</c> tail parser truncates at an
    /// embedded newline even inside a quoted argument (confirmed live in <c>ClaudeWorkerAdapter</c>'s
    /// remarks) — <c>powershell.exe</c>'s <c>-File</c> parameter binding does not have this problem.
    /// </summary>
    private static DialogueParticipant EchoingParticipant(string scriptDirectory, string role, string vendor, string preamble)
    {
        Directory.CreateDirectory(scriptDirectory);

        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(scriptDirectory, $"{role}.ps1");
            File.WriteAllText(scriptPath, $"param([string]$Prompt)\r\nWrite-Output ($Prompt + ' from {role}')\r\n");

            return new DialogueParticipant(
                role, vendor, Model: null, preamble, "powershell",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, DialogueParticipant.PromptPlaceholder]);
        }

        var shScriptPath = Path.Combine(scriptDirectory, $"{role}.sh");
        File.WriteAllText(shScriptPath, $"#!/bin/sh\necho \"$1 from {role}\"\n");

        return new DialogueParticipant(role, vendor, Model: null, preamble, "sh", [shScriptPath, DialogueParticipant.PromptPlaceholder]);
    }
}
