using Aer.Adapters;
using Avalonia.Headless.XUnit;

namespace Aer.Ui.Tests;

/// <summary>
/// M16 Phase 4's authoring surface (issue #153), driven through the real <see cref="MainWindow"/>:
/// create a new bindings file or open an existing one, edit its entries, save — and prove the saved
/// file round-trips through the engine's own <see cref="WorkerBindingConfigParser.Parse"/>. Also
/// covers the phase's other named open questions: adapter names offered from the window's own
/// registry (never invented), and the template↔bindings advisory cross-check (never a save gate).
/// </summary>
public class MainWindowBindingsEditorTests
{
    // "claude"/"gemini" carry the real adapters (M21 Phase 1): none of these tests ever dispatch
    // (Resolve is never called), so this is safe, and it lets the permission-grant-builder tests
    // exercise the real ClaudeWorkerAdapter/GeminiWorkerAdapter IPermissionGrantTranslator
    // implementations rather than a stub. "dialogue" stays a NoopWorkerAdapter specifically to cover
    // the "adapter has no structured builder support at all" gap path.
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter>
        {
            ["claude"] = new ClaudeWorkerAdapter(),
            ["gemini"] = new GeminiWorkerAdapter(),
            ["dialogue"] = new NoopWorkerAdapter(),
        };

    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static string TempBindingsPath() => Path.Combine(Path.GetTempPath(), $"ui-bindings-editor-{Guid.NewGuid():N}.json");

    // A temp config store per window, never the real per-user one (MainWindowRunTests' own
    // precedent) — these tests don't exercise recents/config persistence at all, but a stray write
    // to the real per-user config directory would still be an unwanted side effect of running them.
    private static MainWindow NewWindow() => new(
        new LocalUiConfigurationStore(Path.Combine(Path.GetTempPath(), $"aer-ui-bindings-config-{Guid.NewGuid():N}", "recent-task-directories.json")),
        Adapters);

    private static string CopyFixtureToTemp(string fileName)
    {
        var path = TempBindingsPath();
        File.Copy(FixturePath(fileName), path);
        return path;
    }

    [AvaloniaFact]
    public async Task A_new_bindings_file_is_created_entry_added_saved_and_engine_valid()
    {
        var path = TempBindingsPath();
        try
        {
            var window = NewWindow();

            window.NewBindings();
            Assert.True(window.ViewModel.BindingsEditor.IsOpen);
            // A never-yet-saved bindings file is dirty by construction, same as a new template.
            Assert.True(window.ViewModel.BindingsEditor.IsDirty);

            window.ViewModel.BindingsEditor.AddEntry();
            var entry = Assert.Single(window.ViewModel.BindingsEditor.Entries);
            entry.WorkerName = "architect";
            entry.Adapter = "claude";
            entry.PromptTemplate = "Draft a plan.";
            entry.TimeoutText = "00:05:00";

            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);

            var parsed = await WorkerBindingConfigParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);
            var parsedEntry = Assert.Single(parsed).Value;
            Assert.Equal("claude", parsedEntry.Adapter);
            Assert.Equal("Draft a plan.", parsedEntry.PromptTemplate);
            Assert.Equal(TimeSpan.FromMinutes(5), parsedEntry.Timeout);

            Assert.False(window.ViewModel.BindingsEditor.IsDirty);
            Assert.Contains("Saved", window.ViewModel.BindingsEditor.StatusText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void Adapter_candidates_reflect_the_windows_own_registry()
    {
        var window = NewWindow();

        window.NewBindings();
        window.ViewModel.BindingsEditor.AddEntry();
        var entry = Assert.Single(window.ViewModel.BindingsEditor.Entries);

        Assert.Equal(["claude", "dialogue", "gemini"], entry.AdapterCandidates);
    }

    [AvaloniaFact]
    public async Task Opening_an_existing_bindings_file_loads_one_row_per_entry()
    {
        var path = CopyFixtureToTemp("two-worker-bindings.json");
        try
        {
            var window = NewWindow();
            await window.OpenBindingsInEditorAsync(path, TestContext.Current.CancellationToken);

            Assert.True(window.ViewModel.BindingsEditor.IsOpen);
            Assert.False(window.ViewModel.BindingsEditor.IsDirty);
            Assert.Equal(2, window.ViewModel.BindingsEditor.Entries.Count);

            var architect = window.ViewModel.BindingsEditor.Entries.Single(e => e.WorkerName == "architect");
            Assert.Equal("claude", architect.Adapter);
            Assert.Equal("claude-opus-4", architect.Model);

            var critic = window.ViewModel.BindingsEditor.Entries.Single(e => e.WorkerName == "critic");
            Assert.Equal("plan", critic.RequiredInputsText);
            Assert.Contains("review", critic.ProducedOutputsJson);

            // M21 Phase 1: neither fixture entry carries a structured PermissionGrant, so both load
            // into Advanced mode — round-trip fidelity for a file this new builder never wrote.
            Assert.True(critic.IsAdvancedPermissionScope);
            Assert.Equal("read-only", critic.PermissionScope);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task A_no_op_save_writes_nothing()
    {
        var path = CopyFixtureToTemp("two-worker-bindings.json");
        try
        {
            var contentBefore = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

            var window = NewWindow();
            await window.OpenBindingsInEditorAsync(path, TestContext.Current.CancellationToken);
            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal("No changes to save.", window.ViewModel.BindingsEditor.StatusText);
            Assert.Equal(contentBefore, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task Editing_an_entry_then_saving_round_trips_the_change()
    {
        var path = CopyFixtureToTemp("two-worker-bindings.json");
        try
        {
            var window = NewWindow();
            await window.OpenBindingsInEditorAsync(path, TestContext.Current.CancellationToken);

            var architect = window.ViewModel.BindingsEditor.Entries.Single(e => e.WorkerName == "architect");
            architect.Model = "claude-haiku-4-5";
            Assert.True(window.ViewModel.BindingsEditor.IsDirty);

            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);

            var parsed = await WorkerBindingConfigParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal("claude-haiku-4-5", parsed["architect"].Model);
            Assert.False(window.ViewModel.BindingsEditor.IsDirty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task Removing_an_entry_then_saving_drops_it_from_the_file()
    {
        var path = CopyFixtureToTemp("two-worker-bindings.json");
        try
        {
            var window = NewWindow();
            await window.OpenBindingsInEditorAsync(path, TestContext.Current.CancellationToken);

            var critic = window.ViewModel.BindingsEditor.Entries.Single(e => e.WorkerName == "critic");
            critic.RemoveCommand.Execute(null);

            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);

            var parsed = await WorkerBindingConfigParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(["architect"], parsed.Keys);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task A_blank_worker_name_renders_in_window_and_writes_nothing()
    {
        var path = TempBindingsPath();
        try
        {
            var window = NewWindow();
            window.NewBindings();
            window.ViewModel.BindingsEditor.AddEntry();

            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);

            Assert.Contains("worker role name", window.ViewModel.BindingsEditor.StatusText);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task A_duplicate_worker_name_renders_in_window_and_writes_nothing()
    {
        var path = TempBindingsPath();
        try
        {
            var window = NewWindow();
            window.NewBindings();

            window.ViewModel.BindingsEditor.AddEntry();
            window.ViewModel.BindingsEditor.Entries[0].WorkerName = "architect";
            window.ViewModel.BindingsEditor.Entries[0].Adapter = "claude";
            window.ViewModel.BindingsEditor.Entries[0].PromptTemplate = "a";
            window.ViewModel.BindingsEditor.Entries[0].TimeoutText = "00:01:00";

            window.ViewModel.BindingsEditor.AddEntry();
            window.ViewModel.BindingsEditor.Entries[1].WorkerName = "architect";
            window.ViewModel.BindingsEditor.Entries[1].Adapter = "claude";
            window.ViewModel.BindingsEditor.Entries[1].PromptTemplate = "b";
            window.ViewModel.BindingsEditor.Entries[1].TimeoutText = "00:01:00";

            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);

            Assert.Contains("Duplicate", window.ViewModel.BindingsEditor.StatusText);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task An_invalid_timeout_renders_in_window_and_writes_nothing()
    {
        var path = TempBindingsPath();
        try
        {
            var window = NewWindow();
            window.NewBindings();
            window.ViewModel.BindingsEditor.AddEntry();
            var entry = window.ViewModel.BindingsEditor.Entries[0];
            entry.WorkerName = "architect";
            entry.Adapter = "claude";
            entry.PromptTemplate = "a";
            entry.TimeoutText = "not-a-duration";

            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);

            Assert.Contains("not a valid duration", window.ViewModel.BindingsEditor.StatusText);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task Malformed_produced_outputs_json_renders_in_window_and_writes_nothing()
    {
        var path = TempBindingsPath();
        try
        {
            var window = NewWindow();
            window.NewBindings();
            window.ViewModel.BindingsEditor.AddEntry();
            var entry = window.ViewModel.BindingsEditor.Entries[0];
            entry.WorkerName = "architect";
            entry.Adapter = "claude";
            entry.PromptTemplate = "a";
            entry.TimeoutText = "00:01:00";
            entry.ProducedOutputsJson = "{ not valid json";

            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);

            Assert.Contains("invalid", window.ViewModel.BindingsEditor.StatusText);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task Opening_a_missing_file_renders_in_window_and_leaves_no_stale_session()
    {
        var window = NewWindow();
        window.NewBindings();

        var missingPath = Path.Combine(Path.GetTempPath(), $"ui-bindings-missing-{Guid.NewGuid():N}.json");
        await window.OpenBindingsInEditorAsync(missingPath, TestContext.Current.CancellationToken);

        Assert.False(window.ViewModel.BindingsEditor.IsOpen);
        Assert.Contains(missingPath, window.ViewModel.BindingsEditor.StatusText);

        await window.SaveBindingsAsync(missingPath, TestContext.Current.CancellationToken);
        Assert.Contains("Nothing to save", window.ViewModel.BindingsEditor.StatusText);
        Assert.False(File.Exists(missingPath));
    }

    [AvaloniaFact]
    public async Task The_cross_check_lists_template_workers_with_no_binding_entry_and_never_gates_save()
    {
        var templatePath = FixturePath("three-step-linear-workflow.json");
        var bindingsPath = CopyFixtureToTemp("two-worker-bindings.json");
        try
        {
            var window = NewWindow();

            // The template referenced by three-step-linear-workflow.json declares architect, critic,
            // publisher; two-worker-bindings.json only binds architect and critic.
            await window.OpenTemplateInEditorAsync(templatePath, TestContext.Current.CancellationToken);
            await window.OpenBindingsInEditorAsync(bindingsPath, TestContext.Current.CancellationToken);

            Assert.Equal(["publisher"], window.ViewModel.BindingsEditor.MissingTemplateWorkerNames);

            // Advisory only — Save still succeeds with the gap still present.
            await window.SaveBindingsAsync(bindingsPath, TestContext.Current.CancellationToken);
            Assert.Equal("No changes to save.", window.ViewModel.BindingsEditor.StatusText);
        }
        finally
        {
            File.Delete(bindingsPath);
        }
    }

    [AvaloniaFact]
    public async Task The_cross_check_is_empty_when_no_template_is_open_in_the_editor()
    {
        var bindingsPath = CopyFixtureToTemp("two-worker-bindings.json");
        try
        {
            var window = NewWindow();
            await window.OpenBindingsInEditorAsync(bindingsPath, TestContext.Current.CancellationToken);

            Assert.Empty(window.ViewModel.BindingsEditor.MissingTemplateWorkerNames);
        }
        finally
        {
            File.Delete(bindingsPath);
        }
    }

    // M21 Phase 1: the structured permission-grant builder — save/reopen round trip, precedence
    // over a stale raw string, live in-UI gap surfacing, and Save refusing to persist a grant the
    // selected adapter can't honor.

    [AvaloniaFact]
    public async Task A_permission_grant_built_via_checkboxes_round_trips_as_the_structured_field()
    {
        var path = TempBindingsPath();
        try
        {
            var window = NewWindow();
            window.NewBindings();
            window.ViewModel.BindingsEditor.AddEntry();
            var entry = window.ViewModel.BindingsEditor.Entries[0];
            entry.WorkerName = "architect";
            entry.Adapter = "claude";
            entry.PromptTemplate = "Draft a plan.";
            entry.TimeoutText = "00:05:00";
            entry.GrantWriteFiles = true;
            entry.GrantRunShellCommands = true;
            entry.ShellCommandPatternsText = "git:*, npm:*";

            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("Saved", window.ViewModel.BindingsEditor.StatusText);

            var parsed = await WorkerBindingConfigParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);
            var saved = parsed["architect"];
            Assert.Null(saved.PermissionScope);
            Assert.NotNull(saved.PermissionGrant);
            Assert.True(saved.PermissionGrant!.WriteFiles);
            Assert.False(saved.PermissionGrant.ReadFiles);
            Assert.Equal(["git:*", "npm:*"], saved.PermissionGrant.ShellCommandPatterns);

            // Reopening lands back in Builder mode with the same checkboxes — round-trip fidelity.
            await window.OpenBindingsInEditorAsync(path, TestContext.Current.CancellationToken);
            var reopened = window.ViewModel.BindingsEditor.Entries[0];
            Assert.False(reopened.IsAdvancedPermissionScope);
            Assert.True(reopened.GrantWriteFiles);
            Assert.True(reopened.GrantRunShellCommands);
            Assert.Equal("git:*, npm:*", reopened.ShellCommandPatternsText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task An_unconfigured_builder_grant_saves_as_no_permission_field_at_all()
    {
        // Mirrors the pre-existing "blank PermissionScope means fall through to the adapter's
        // default" behavior for a never-touched builder row — Blank() defaults to Builder mode, but
        // nothing checked must not persist an explicit "grant nothing" record (see PermissionGrant's
        // own IsEmpty doc).
        var path = TempBindingsPath();
        try
        {
            var window = NewWindow();
            window.NewBindings();
            window.ViewModel.BindingsEditor.AddEntry();
            var entry = window.ViewModel.BindingsEditor.Entries[0];
            entry.WorkerName = "architect";
            entry.Adapter = "claude";
            entry.PromptTemplate = "Draft a plan.";
            entry.TimeoutText = "00:05:00";

            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);

            var parsed = await WorkerBindingConfigParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);
            var saved = parsed["architect"];
            Assert.Null(saved.PermissionScope);
            Assert.Null(saved.PermissionGrant);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task Advanced_mode_persists_the_raw_string_and_ignores_stale_builder_checkboxes()
    {
        var path = TempBindingsPath();
        try
        {
            var window = NewWindow();
            window.NewBindings();
            window.ViewModel.BindingsEditor.AddEntry();
            var entry = window.ViewModel.BindingsEditor.Entries[0];
            entry.WorkerName = "architect";
            entry.Adapter = "claude";
            entry.PromptTemplate = "Draft a plan.";
            entry.TimeoutText = "00:05:00";
            entry.GrantReadFiles = true; // set before switching, to prove Advanced mode ignores it
            entry.IsAdvancedPermissionScope = true;
            entry.PermissionScope = "Write,Bash(git:*)";

            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);

            var parsed = await WorkerBindingConfigParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);
            var saved = parsed["architect"];
            Assert.Equal("Write,Bash(git:*)", saved.PermissionScope);
            Assert.Null(saved.PermissionGrant);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void An_unsupported_grant_shows_a_live_gap_warning_before_save()
    {
        var window = NewWindow();
        window.NewBindings();
        window.ViewModel.BindingsEditor.AddEntry();
        var entry = window.ViewModel.BindingsEditor.Entries[0];
        entry.Adapter = "gemini";

        Assert.False(entry.HasPermissionGrantGapWarning);

        entry.GrantRunShellCommands = true;

        Assert.True(entry.HasPermissionGrantGapWarning);
        Assert.Contains("agy", entry.PermissionGrantGapWarning);
    }

    [AvaloniaFact]
    public void An_adapter_with_no_translator_shows_a_no_builder_support_gap_warning()
    {
        var window = NewWindow();
        window.NewBindings();
        window.ViewModel.BindingsEditor.AddEntry();
        var entry = window.ViewModel.BindingsEditor.Entries[0];
        entry.Adapter = "dialogue";

        entry.GrantReadFiles = true;

        Assert.True(entry.HasPermissionGrantGapWarning);
        Assert.Contains("no structured permission builder support", entry.PermissionGrantGapWarning);
    }

    [AvaloniaFact]
    public async Task Saving_an_unsupported_grant_is_refused_and_writes_nothing()
    {
        var path = TempBindingsPath();
        try
        {
            var window = NewWindow();
            window.NewBindings();
            window.ViewModel.BindingsEditor.AddEntry();
            var entry = window.ViewModel.BindingsEditor.Entries[0];
            entry.WorkerName = "architect";
            entry.Adapter = "gemini";
            entry.PromptTemplate = "Draft a plan.";
            entry.TimeoutText = "00:05:00";
            entry.GrantNetworkAccess = true;

            await window.SaveBindingsAsync(path, TestContext.Current.CancellationToken);

            Assert.Contains("can't be saved", window.ViewModel.BindingsEditor.StatusText);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>A minimal <see cref="IWorkerAdapter"/> stub — these tests never dispatch a worker, they only need adapter *names* for the registry-reflection assertions.</summary>
internal sealed class NoopWorkerAdapter : IWorkerAdapter
{
    public Aer.Flow.Dispatch.CoreDispatchTarget Resolve(WorkerInvocation invocation, Aer.Flow.Domain.WorkerContract contract) =>
        throw new NotSupportedException("This test adapter never dispatches a real invocation.");
}
