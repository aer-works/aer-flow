using System.Text.Json;
using Aer.Adapters;
using Aer.Cli;
using Aer.Flow.Domain;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Aer.Workers.Dialogue;

namespace Aer.Ui.Tests;

/// <summary>
/// M18's completion gate (Phase 3, #179): a real, unstubbed dialogue run — a step bound to
/// <c>"dialogue"</c> through the real <see cref="WorkerAdapterRegistry.Default"/>, run to terminal
/// via <c>RunCommand.ExecuteAsync</c> over stub vendor CLIs (the exact machinery
/// <c>Aer.Cli.Tests.DialogueDispatchEndToEndTests</c> proved in M17 Phase 4) — whose resulting
/// artifacts the conversation view then projects and this class asserts, control by control. This
/// is the producer/consumer agreement check Phases 1–2 deliberately did not make: their fixtures
/// were hand-written to UI spec §10.1's reader contract; here the producer is the real
/// <c>Aer.Workers.Dialogue</c> executable. Fixture builders are duplicated from
/// <c>DialogueDispatchEndToEndTests</c> per this codebase's convention of copying test support
/// per test project rather than referencing across assemblies. CI-safe on every OS: no real
/// vendor CLI or network access involved.
/// </summary>
public class ConversationRoundTripTests
{
    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-conv-gate-config-{Guid.NewGuid():N}", "recent-task-directories.json");

    [AvaloniaFact]
    public async Task A_dialogue_run_to_terminal_projects_its_conversation_in_the_ui()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"conv-gate-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);
            var workflowFilePath = await WriteSingleDialogueStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteDialogueBindingsAsync(testRoot, responderFails: false);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);

            var finalState = (await RunCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default)).State;
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(StepStatus.Succeeded, Assert.Single(finalState.Steps).Status);

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            // Discovery: the run's single execution offers exactly one conversation row.
            var entriesPanel = window.FindViewControl<StackPanel>("ConversationExecutionsPanel")!;
            var row = Assert.Single(entriesPanel.Children.OfType<StackPanel>());
            var label = row.Children.OfType<TextBlock>().Single().Text!;
            Assert.StartsWith("debate —", label);

            var executionId = Assert.Single(finalState.Steps).LatestExecutionId;
            var outputDirectory = Path.Combine(taskDirectory, "artifacts", $"execution_{executionId}");
            window.ShowConversation(outputDirectory, label);

            // The rendered conversation matches what the real worker's stub exchange produced:
            // two turns, in order, with §10.1's role/vendor labeling and the seed prompt on demand.
            var conversationPanel = window.FindViewControl<StackPanel>("ConversationPanel")!;
            var turnPanels = conversationPanel.Children.OfType<Border>()
                .Select(border => (StackPanel)border.Child!)
                .ToList();
            Assert.Equal(2, turnPanels.Count);

            var headers = turnPanels
                .Select(panel => panel.Children.OfType<TextBlock>().First().Text)
                .ToList();
            Assert.Equal(["1 · initiator (claude)", "2 · responder (gemini)"], headers);

            var texts = turnPanels
                .Select(panel => panel.Children.OfType<TextBlock>().Last().Text!)
                .ToList();
            Assert.EndsWith(" from initiator", texts[0]);
            Assert.EndsWith(" from responder", texts[1]);

            var firstPrompt = (TextBlock)turnPanels[0].Children.OfType<Expander>().Single().Content!;
            Assert.Contains("Open with your position.", firstPrompt.Text);

            // No malformed markers: a clean run projects nothing but turns.
            Assert.DoesNotContain(conversationPanel.Children.OfType<TextBlock>(),
                block => block.Text!.Contains("not a schema-valid turn"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [AvaloniaFact]
    public async Task A_failed_exchanges_forensic_prefix_still_renders_as_a_conversation()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"conv-gate-fail-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);
            var workflowFilePath = await WriteSingleDialogueStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteDialogueBindingsAsync(testRoot, responderFails: true);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);

            var finalState = (await RunCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default)).State;
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            var stepState = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, stepState.Status);

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            // The failed execution still gets a conversation row — the transcript on disk is the
            // forensic record M17 Phase 3 deliberately leaves (§10.1: partial is honest data).
            var entriesPanel = window.FindViewControl<StackPanel>("ConversationExecutionsPanel")!;
            var row = Assert.Single(entriesPanel.Children.OfType<StackPanel>());
            var label = row.Children.OfType<TextBlock>().Single().Text!;

            var outputDirectory = Path.Combine(
                taskDirectory, "artifacts", $"execution_{stepState.LatestExecutionId}");
            window.ShowConversation(outputDirectory, label);

            // Exactly the prefix that completed before the failing turn: turn 1, intact, no
            // malformed marker — the failing turn was never appended (M17 Phase 3's decision).
            var conversationPanel = window.FindViewControl<StackPanel>("ConversationPanel")!;
            var turnPanel = (StackPanel)Assert.Single(conversationPanel.Children.OfType<Border>()).Child!;
            Assert.Equal("1 · initiator (claude)", turnPanel.Children.OfType<TextBlock>().First().Text);
            Assert.DoesNotContain(conversationPanel.Children.OfType<TextBlock>(),
                block => block.Text!.Contains("not a schema-valid turn"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
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

    private static async Task<string> WriteDialogueBindingsAsync(string directory, bool responderFails)
    {
        var scriptDirectory = Path.Combine(directory, "scripts");
        var dialogueConfig = new DialogueWorkerConfig(
            SeedPrompt: "Open with your position.",
            TurnBudget: 2,
            FinalOutputName: "verdict.md",
            StopSentinel: null,
            Participants:
            [
                EchoingParticipant(scriptDirectory, "initiator", "claude", "You argue for.", fails: false),
                EchoingParticipant(scriptDirectory, "responder", "gemini", "You argue against.", responderFails),
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
    /// The same stub-CLI shape as <c>DialogueDispatchEndToEndTests.EchoingParticipant</c> (including
    /// its Windows <c>powershell -File</c> rationale), extended with <paramref name="fails"/> for the
    /// forensic-prefix gate: a failing participant exits nonzero before producing a turn, which
    /// <c>DialogueRunner</c> maps to a failed execution with the prior turns left on disk.
    /// </summary>
    private static DialogueParticipant EchoingParticipant(
        string scriptDirectory, string role, string vendor, string preamble, bool fails)
    {
        Directory.CreateDirectory(scriptDirectory);

        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(scriptDirectory, $"{role}.ps1");
            var body = fails
                ? "param([string]$Prompt)\r\nexit 1\r\n"
                : $"param([string]$Prompt)\r\nWrite-Output ($Prompt + ' from {role}')\r\n";
            File.WriteAllText(scriptPath, body);

            return new DialogueParticipant(
                role, vendor, Model: null, preamble, "powershell",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, DialogueParticipant.PromptPlaceholder]);
        }

        var shScriptPath = Path.Combine(scriptDirectory, $"{role}.sh");
        var shBody = fails
            ? "#!/bin/sh\nexit 1\n"
            : $"#!/bin/sh\necho \"$1 from {role}\"\n";
        File.WriteAllText(shScriptPath, shBody);

        return new DialogueParticipant(role, vendor, Model: null, preamble, "sh", [shScriptPath, DialogueParticipant.PromptPlaceholder]);
    }
}
