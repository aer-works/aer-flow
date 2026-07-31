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
            Assert.Equal("verdict.md", sidecarConfig.FinalOutputName);
            Assert.Equal(3, sidecarConfig.Participants.Count);
            Assert.Equal(["architect", "critic", "arbiter"], sidecarConfig.Participants.Select(p => p.Role));
            Assert.Equal(["claude", "gemini", "claude"], sidecarConfig.Participants.Select(p => p.Vendor));
            Assert.Equal("claude-haiku-4-5", sidecarConfig.Participants[2].Model);
            Assert.Contains(sidecarConfig.Participants[0].Args, a => a.Contains(DialogueParticipant.PromptFilePlaceholder, StringComparison.Ordinal));

            // Reopening loads the sidecar's content back into structured fields — never re-parsed by
            // the test itself, and the reopened session isn't dirty (true round-trip fidelity).
            await window.OpenBindingsInEditorAsync(path, TestContext.Current.CancellationToken);
            var reopened = window.ViewModel.BindingsEditor.Entries.Single(e => e.WorkerName == "debate");
            Assert.False(window.ViewModel.BindingsEditor.IsDirty);
            Assert.True(reopened.IsDialogueAdapter);
            Assert.Equal("Propose a caching strategy.", reopened.DialogueSeedPromptText);
            Assert.Equal("6", reopened.DialogueTurnBudgetText);
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

    /// <summary>
    /// #743 regression, found while implementing #736: <see cref="WorkerBindingEntryViewModel.FromEntry"/>
    /// loaded a dialogue row's structured fields from its sidecar <see cref="DialogueWorkerConfig"/>
    /// but never carried <see cref="DialogueWorkerConfig.FinalOutputMode"/> through, so reopening a
    /// <c>Transcript</c>-mode config and re-saving it — with no other edit — silently downgraded it
    /// to the <c>FinalTurn</c> default. This pins <c>Transcript</c> surviving an unrelated edit
    /// (turn budget) and a full save/reopen/save round trip.
    /// </summary>
    [AvaloniaFact]
    public async Task A_dialogue_steps_Transcript_FinalOutputMode_survives_reopen_and_an_unrelated_resave()
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

            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);
            var savedEntry = (await WorkerBindingConfigParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken))["debate"];
            var sidecarPath = Path.Combine(directory, savedEntry.PromptTemplate);

            // Hand-author Transcript mode directly onto the sidecar — the UI has no control for it
            // (#736's "no UI half" is deliberate), so this is the only way an existing config gets it.
            var sidecarConfig = await DialogueWorkerConfigParser.LoadFromFileAsync(sidecarPath, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                sidecarPath,
                System.Text.Json.JsonSerializer.Serialize(sidecarConfig with { FinalOutputMode = FinalOutputMode.Transcript }),
                TestContext.Current.CancellationToken);

            await window.OpenBindingsInEditorAsync(path, TestContext.Current.CancellationToken);
            var reopened = window.ViewModel.BindingsEditor.Entries.Single(e => e.WorkerName == "debate");

            // An unrelated edit — turn budget — then save, exactly the shape that silently dropped
            // FinalOutputMode before #743's fix.
            reopened.DialogueTurnBudgetText = "5";
            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);

            var resavedConfig = await DialogueWorkerConfigParser.LoadFromFileAsync(sidecarPath, TestContext.Current.CancellationToken);
            Assert.Equal(FinalOutputMode.Transcript, resavedConfig.FinalOutputMode);
            Assert.Equal(5, resavedConfig.TurnBudget);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// The identical round-trip drop one field over from #743's, for
    /// <see cref="DialogueWorkerConfig.TurnTimeout"/> — mechanism and provenance on
    /// <c>WorkerBindingEntryViewModel._dialogueTurnTimeout</c>'s doc, which this pins. Same
    /// shape as the test above, same reason.
    /// </summary>
    [AvaloniaFact]
    public async Task A_dialogue_steps_hand_authored_TurnTimeout_survives_reopen_and_an_unrelated_resave()
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

            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);
            var savedEntry = (await WorkerBindingConfigParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken))["debate"];
            var sidecarPath = Path.Combine(directory, savedEntry.PromptTemplate);

            var sidecarConfig = await DialogueWorkerConfigParser.LoadFromFileAsync(sidecarPath, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                sidecarPath,
                System.Text.Json.JsonSerializer.Serialize(sidecarConfig with { TurnTimeout = TimeSpan.FromMinutes(20) }),
                TestContext.Current.CancellationToken);

            await window.OpenBindingsInEditorAsync(path, TestContext.Current.CancellationToken);
            var reopened = window.ViewModel.BindingsEditor.Entries.Single(e => e.WorkerName == "debate");

            reopened.DialogueTurnBudgetText = "5";
            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);

            var resavedConfig = await DialogueWorkerConfigParser.LoadFromFileAsync(sidecarPath, TestContext.Current.CancellationToken);
            Assert.Equal(TimeSpan.FromMinutes(20), resavedConfig.TurnTimeout);
            Assert.Equal(5, resavedConfig.TurnBudget);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    /// <summary>
    /// #820 compat polarity: a sidecar persisted before StopSentinel was retired from
    /// <see cref="DialogueWorkerConfig"/> still opens in the authoring surface (the field is simply
    /// unmapped — <see cref="DialogueWorkerConfigParserTests.A_config_carrying_the_retired_StopSentinel_key_still_parses"/>
    /// pins the parser side of this), and re-saving through the editor rewrites the sidecar without
    /// the retired key at all rather than round-tripping it back in.
    /// </summary>
    [AvaloniaFact]
    public async Task A_sidecar_carrying_the_retired_StopSentinel_key_opens_and_resave_drops_it()
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

            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);
            var savedEntry = (await WorkerBindingConfigParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken))["debate"];
            var sidecarPath = Path.Combine(directory, savedEntry.PromptTemplate);

            // Hand-author the retired key directly onto the sidecar JSON — the record no longer
            // declares it, so this is the only way to reproduce an old, pre-#820 persisted file.
            var rawSidecarJson = await File.ReadAllTextAsync(sidecarPath, TestContext.Current.CancellationToken);
            var legacySidecarJson = rawSidecarJson.Replace(
                "\"FinalOutputName\": \"verdict.md\"",
                "\"FinalOutputName\": \"verdict.md\", \"StopSentinel\": \"CONSENSUS\"");
            Assert.Contains("StopSentinel", legacySidecarJson);
            Assert.NotEqual(rawSidecarJson, legacySidecarJson);
            await File.WriteAllTextAsync(sidecarPath, legacySidecarJson, TestContext.Current.CancellationToken);

            await window.OpenBindingsInEditorAsync(path, TestContext.Current.CancellationToken);
            var reopened = window.ViewModel.BindingsEditor.Entries.Single(e => e.WorkerName == "debate");
            Assert.Equal("Propose a caching strategy.", reopened.DialogueSeedPromptText);

            reopened.DialogueTurnBudgetText = "5";
            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);

            var resavedJson = await File.ReadAllTextAsync(sidecarPath, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("StopSentinel", resavedJson);
            var resavedConfig = await DialogueWorkerConfigParser.LoadFromFileAsync(sidecarPath, TestContext.Current.CancellationToken);
            Assert.Equal(5, resavedConfig.TurnBudget);
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
