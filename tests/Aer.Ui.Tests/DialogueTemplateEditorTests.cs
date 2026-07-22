using Aer.Ui.Tests.TestSupport;
using Aer.Adapters;
using Aer.Workers.Dialogue;
using Avalonia.Headless.XUnit;

namespace Aer.Ui.Tests;

/// <summary>
/// M23 Phase 1's named verification requirement (#270): "a Template Editor round trip with no
/// hand-edited JSON" — an N-party dialogue step authored entirely through
/// <see cref="WorkerBindingEntryViewModel"/>'s structured dialogue fields (never a raw JSON text
/// box, unlike <see cref="MainWindowBindingsEditorTests"/>'s pre-existing opaque-JSON coverage for
/// <c>ProducedOutputsJson</c>), saved, and reopened with full fidelity — proving the dialogue worker
/// is now a first-class Template Editor step type rather than wizard-only (<see cref="NewWorkflowViewModel"/>).
/// </summary>
public class DialogueTemplateEditorTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter>
        {
            ["claude"] = new ClaudeWorkerAdapter(),
            ["gemini"] = new GeminiWorkerAdapter(),
            ["dialogue"] = new DialogueWorkerAdapter(),
        };

    private static MainWindow NewWindow() => new(
        new LocalUiConfigurationStore(Path.Combine(Path.GetTempPath(), $"aer-ui-dialogue-config-{Guid.NewGuid():N}", "recent-task-directories.json")),
        Adapters);

    private static string TempBindingsPath(string directory) => Path.Combine(directory, "bindings.json");

    [AvaloniaFact]
    public async Task An_N_party_dialogue_step_authored_structurally_round_trips_with_no_hand_edited_json()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dialogue-template-editor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = TempBindingsPath(directory);
        try
        {
            var window = NewWindow();
            window.NewBindings();
            window.ViewModel.BindingsEditor.AddEntry();
            var entry = window.ViewModel.BindingsEditor.Entries[0];
            entry.WorkerName = "debate";
            entry.Adapter = "dialogue";
            entry.TimeoutText = "00:05:00";

            // Switching Adapter to "dialogue" auto-seeds the two-party default.
            Assert.True(entry.IsDialogueAdapter);
            Assert.Equal(2, entry.DialogueParticipants.Count);

            entry.DialogueSeedPromptText = "Propose a caching strategy.";
            entry.DialogueTurnBudgetText = "6";
            entry.DialogueStopSentinelText = "CONSENSUS";
            entry.DialogueFinalOutputNameText = "verdict.md";

            entry.DialogueParticipants[0].Role = "architect";
            entry.DialogueParticipants[0].Vendor = "claude";
            entry.DialogueParticipants[0].Preamble = "You design the cache.";
            entry.DialogueParticipants[1].Role = "critic";
            entry.DialogueParticipants[1].Vendor = "gemini";
            entry.DialogueParticipants[1].Preamble = "You critique the design.";

            // Third participant — proves this is genuinely N-party, not just the wizard's fixed pair.
            entry.AddDialogueParticipantCommand.Execute(null);
            Assert.Equal(3, entry.DialogueParticipants.Count);
            entry.DialogueParticipants[2].Role = "arbiter";
            entry.DialogueParticipants[2].Vendor = "claude";
            entry.DialogueParticipants[2].Model = "claude-haiku-4-5";
            entry.DialogueParticipants[2].Preamble = "You break ties.";

            // PromptTemplate (the sidecar path) is deliberately left blank — Save auto-names it.
            Assert.True(window.ViewModel.BindingsEditor.IsDirty);

            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("Saved", window.ViewModel.BindingsEditor.StatusText);
            Assert.False(window.ViewModel.BindingsEditor.IsDirty);

            var parsed = await WorkerBindingConfigParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);
            var savedEntry = parsed["debate"];
            Assert.Equal("dialogue", savedEntry.Adapter);
            Assert.Equal("dialogue-debate.json", savedEntry.PromptTemplate);

            var sidecarPath = Path.Combine(directory, savedEntry.PromptTemplate);
            Assert.True(File.Exists(sidecarPath));
            var sidecarConfig = await DialogueWorkerConfigParser.LoadFromFileAsync(sidecarPath, TestContext.Current.CancellationToken);
            Assert.Equal("Propose a caching strategy.", sidecarConfig.SeedPrompt);
            Assert.Equal(6, sidecarConfig.TurnBudget);
            Assert.Equal("CONSENSUS", sidecarConfig.StopSentinel);
            Assert.Equal("verdict.md", sidecarConfig.FinalOutputName);
            Assert.Equal(3, sidecarConfig.Participants.Count);
            Assert.Equal(["architect", "critic", "arbiter"], sidecarConfig.Participants.Select(p => p.Role));
            Assert.Equal(["claude", "gemini", "claude"], sidecarConfig.Participants.Select(p => p.Vendor));
            Assert.Equal("claude-haiku-4-5", sidecarConfig.Participants[2].Model);
            Assert.Contains(DialogueParticipant.PromptPlaceholder, sidecarConfig.Participants[0].Args);

            // Reopening loads the sidecar's content back into structured fields — never re-parsed by
            // the test itself, and the reopened session isn't dirty (true round-trip fidelity).
            await window.OpenBindingsInEditorAsync(path, TestContext.Current.CancellationToken);
            var reopened = window.ViewModel.BindingsEditor.Entries.Single(e => e.WorkerName == "debate");
            Assert.False(window.ViewModel.BindingsEditor.IsDirty);
            Assert.True(reopened.IsDialogueAdapter);
            Assert.Equal("Propose a caching strategy.", reopened.DialogueSeedPromptText);
            Assert.Equal("6", reopened.DialogueTurnBudgetText);
            Assert.Equal("CONSENSUS", reopened.DialogueStopSentinelText);
            Assert.Equal("verdict.md", reopened.DialogueFinalOutputNameText);
            Assert.Equal(3, reopened.DialogueParticipants.Count);
            Assert.Equal(["architect", "critic", "arbiter"], reopened.DialogueParticipants.Select(p => p.Role));
            Assert.Equal("claude-haiku-4-5", reopened.DialogueParticipants[2].Model);
            Assert.Equal("You break ties.", reopened.DialogueParticipants[2].Preamble);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [AvaloniaFact]
    public async Task Removing_a_dialogue_participant_below_two_blocks_save_with_no_write()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dialogue-template-editor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = TempBindingsPath(directory);
        try
        {
            var window = NewWindow();
            window.NewBindings();
            window.ViewModel.BindingsEditor.AddEntry();
            var entry = window.ViewModel.BindingsEditor.Entries[0];
            entry.WorkerName = "debate";
            entry.Adapter = "dialogue";
            entry.TimeoutText = "00:05:00";
            entry.DialogueSeedPromptText = "Propose a caching strategy.";
            entry.DialogueFinalOutputNameText = "verdict.md";
            entry.DialogueParticipants[0].Preamble = "Side A.";
            entry.DialogueParticipants[1].Preamble = "Side B.";

            entry.DialogueParticipants[1].RemoveCommand.Execute(null);
            Assert.Single(entry.DialogueParticipants);

            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);

            Assert.Contains("at least two", window.ViewModel.BindingsEditor.StatusText);
            Assert.False(File.Exists(path));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }
}
