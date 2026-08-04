using Aer.Ui.Tests.TestSupport;
using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Workers.Dialogue;

namespace Aer.Ui.Tests;

/// <summary>
/// M19 Phase 4 (issue #189): the guided New Workflow flow — form-first authoring whose Save
/// writes the same durable files (workflow definition, bindings, dialogue config sidecars) every
/// existing loader consumes, verified by loading them back through those exact loaders. Plain
/// ViewModel tests, no window: the flow's state and file I/O live entirely in
/// <see cref="NewWorkflowViewModel"/> (Aer.Ui.Core), which is the point of the seam.
/// </summary>
public class GuidedAuthoringTests
{
    private static string NewWorkspacePath() =>
        Path.Combine(Path.GetTempPath(), $"ui-guided-{Guid.NewGuid():N}");

    private static NewWorkflowViewModel DraftAndReviewFlow(string workspacePath)
    {
        var flow = new NewWorkflowViewModel
        {
            WorkflowName = "draft-and-review",
            WorkspaceOverridePath = workspacePath,
        };

        flow.AddStepCommand.Execute(null);
        var draft = flow.Steps[0];
        draft.Name = "draft";
        draft.Prompt = "Write the draft.";
        draft.ProducesFileName = "draft.md";

        flow.AddStepCommand.Execute(null);
        var review = flow.Steps[1];
        review.Name = "review";
        review.Kind = GuidedStepKind.Claude;
        review.Prompt = "Critique the draft.";
        review.ProducesFileName = "review.md";
        review.HasReviewGate = true;
        review.DependsOnOptions.Single(option => option.StepName == "draft").IsSelected = true;

        return flow;
    }

    [Fact]
    public async Task Save_writes_a_workflow_and_bindings_the_existing_loaders_load_back()
    {
        var workspacePath = NewWorkspacePath();
        try
        {
            var flow = DraftAndReviewFlow(workspacePath);
            var paths = await flow.SaveAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(paths);
            var definition = await TemplateProjectionLoader.LoadAsync(
                paths.Value.WorkflowFilePath, TestContext.Current.CancellationToken);
            Assert.Equal(new WorkflowTemplateId("draft-and-review"), definition.WorkflowTemplateId);
            Assert.Equal(2, definition.Steps.Count);

            var review = definition.Steps.Single(step => step.StepId.Value == "review");
            Assert.Equal(["draft.md"], review.Inputs);
            Assert.Equal(["review.md"], review.Outputs);
            Assert.Equal([new StepId("draft")], review.DependsOn);
            Assert.NotNull(review.PausePoint);
            Assert.Equal([new StepId("draft")], review.PausePoint!.SupersedeTargets);

            var bindings = await BindingsProjectionLoader.LoadAsync(
                paths.Value.BindingsFilePath, TestContext.Current.CancellationToken);
            var reviewBinding = bindings["review"];
            Assert.Equal("claude", reviewBinding.Adapter);
            Assert.Equal("Critique the draft.", reviewBinding.PromptTemplate);
            Assert.Equal(GuidedStepViewModel.DefaultTimeout, reviewBinding.Timeout);
            Assert.Null(reviewBinding.Model);
            Assert.Null(reviewBinding.PermissionScope);
            Assert.Null(reviewBinding.PermissionGrant);
            Assert.Equal(["draft.md"], reviewBinding.Contract.RequiredInputs);
            Assert.Equal("review.md", Assert.Single(reviewBinding.Contract.ProducedOutputs).Name);
        }
        finally
        {
            if (Directory.Exists(workspacePath))
            {
                DirectoryCleanup.DeleteRecursively(workspacePath);
            }
        }
    }

    [Fact]
    public async Task A_dialogue_step_writes_its_config_sidecar_with_the_vendor_preset_participants()
    {
        var workspacePath = NewWorkspacePath();
        try
        {
            var flow = new NewWorkflowViewModel
            {
                WorkflowName = "debate",
                WorkspaceOverridePath = workspacePath,
            };
            flow.AddStepCommand.Execute(null);
            var debate = flow.Steps[0];
            debate.Name = "debate";
            debate.Kind = GuidedStepKind.Dialogue;
            debate.ProducesFileName = "verdict.md";
            debate.SeedPrompt = "Open with your position.";
            debate.TurnBudgetText = "2";
            debate.InitiatorPreamble = "Argue for.";
            debate.ResponderPreamble = "Argue against.";

            var paths = await flow.SaveAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(paths);
            var bindings = await BindingsProjectionLoader.LoadAsync(
                paths.Value.BindingsFilePath, TestContext.Current.CancellationToken);
            var entry = bindings["debate"];
            Assert.Equal("dialogue", entry.Adapter);

            // The §4-amendment sidecar: the bindings entry references it; the user never opened it.
            var sidecarPath = entry.PromptTemplate;
            Assert.Equal(Path.Combine(workspacePath, "dialogue-debate.json"), sidecarPath);
            var config = JsonSerializer.Deserialize<DialogueWorkerConfig>(
                await File.ReadAllTextAsync(sidecarPath, TestContext.Current.CancellationToken))!;
            Assert.Equal("Open with your position.", config.SeedPrompt);
            Assert.Equal(2, config.TurnBudget);
            Assert.Equal("verdict.md", config.FinalOutputName);
            Assert.Equal(2, config.Participants.Count);
            Assert.Equal("claude", config.Participants[0].Command);
            Assert.Equal("agy", config.Participants[1].Command);
            Assert.Contains(config.Participants[0].Args, a => a == DialogueParticipant.PromptPlaceholder);
            Assert.Contains(config.Participants[1].Args, a => a == DialogueParticipant.PromptPlaceholder);
        }
        finally
        {
            if (Directory.Exists(workspacePath))
            {
                DirectoryCleanup.DeleteRecursively(workspacePath);
            }
        }
    }

    [Fact]
    public async Task Guidance_blocks_save_in_plain_words_until_the_flow_is_complete()
    {
        var flow = new NewWorkflowViewModel { WorkspaceOverridePath = NewWorkspacePath() };
        flow.RefreshStructure();

        Assert.Contains("Give the workflow a name — it names the plan and its folder.", flow.GuidanceMessages);
        Assert.Contains("Add at least one step.", flow.GuidanceMessages);
        Assert.False(flow.CanSave);
        Assert.Null(await flow.SaveAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Not saved — finish the guidance items above.", flow.StatusText);

        flow.WorkflowName = "one-step";
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";
        flow.RefreshStructure();

        Assert.Empty(flow.GuidanceMessages);
        Assert.True(flow.CanSave);
    }

    [Fact]
    public async Task Save_and_run_raises_RunRequested_with_the_saved_paths()
    {
        var workspacePath = NewWorkspacePath();
        try
        {
            var flow = DraftAndReviewFlow(workspacePath);
            string? requestedWorkflowPath = null;
            string? requestedBindingsPath = null;
            flow.RunRequested += (workflowFilePath, bindingsFilePath) =>
            {
                requestedWorkflowPath = workflowFilePath;
                requestedBindingsPath = bindingsFilePath;
                return Task.CompletedTask;
            };

            await flow.SaveAndRunCommand.ExecuteAsync(null);

            Assert.Equal(Path.Combine(workspacePath, "workflow.json"), requestedWorkflowPath);
            Assert.Equal(Path.Combine(workspacePath, "bindings.json"), requestedBindingsPath);
        }
        finally
        {
            if (Directory.Exists(workspacePath))
            {
                DirectoryCleanup.DeleteRecursively(workspacePath);
            }
        }
    }

    [Fact]
    public void Vendor_readiness_reports_presence_in_plain_words_and_never_gates()
    {
        var flow = new NewWorkflowViewModel();
        flow.RefreshVendorReadiness(isOnPath: binary => binary == "claude");

        Assert.Equal(
            [
                "Claude: available",
                "Agy: not found — install and sign in to the agy CLI to run steps with it",
            ],
            flow.VendorReadinessLines);

        // Readiness is informational only: nothing in the guidance path reads it, so an
        // unavailable vendor never blocks authoring or saving.
        Assert.DoesNotContain(flow.GuidanceMessages, message => message.Contains("Agy"));
    }

    // M21 Phase 1 follow-up (owner feedback on the initial per-entry-only builder): permissions are
    // set once per workflow and applied to every non-dialogue step at Save, not configured per step.

    [Fact]
    public async Task A_shared_permission_grant_applies_to_every_non_dialogue_step()
    {
        var workspacePath = NewWorkspacePath();
        try
        {
            var flow = DraftAndReviewFlow(workspacePath);
            flow.SetAdapterRegistry(new Dictionary<string, IWorkerAdapter>
            {
                ["claude"] = new ClaudeWorkerAdapter(),
                ["gemini"] = new GeminiWorkerAdapter(),
            });
            flow.GrantReadFiles = true;
            flow.GrantWriteFiles = true;

            var paths = await flow.SaveAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(paths);
            var bindings = await BindingsProjectionLoader.LoadAsync(
                paths.Value.BindingsFilePath, TestContext.Current.CancellationToken);
            foreach (var stepName in new[] { "draft", "review" })
            {
                var grant = bindings[stepName].PermissionGrant;
                Assert.NotNull(grant);
                Assert.True(grant!.ReadFiles);
                Assert.True(grant.WriteFiles);
                Assert.Null(bindings[stepName].PermissionScope);
            }
        }
        finally
        {
            if (Directory.Exists(workspacePath))
            {
                DirectoryCleanup.DeleteRecursively(workspacePath);
            }
        }
    }

    [Fact]
    public async Task A_dialogue_steps_binding_entry_is_unaffected_by_the_shared_permission_grant()
    {
        var workspacePath = NewWorkspacePath();
        try
        {
            var flow = new NewWorkflowViewModel { WorkflowName = "debate", WorkspaceOverridePath = workspacePath };
            flow.AddStepCommand.Execute(null);
            var debate = flow.Steps[0];
            debate.Name = "debate";
            debate.Kind = GuidedStepKind.Dialogue;
            debate.ProducesFileName = "verdict.md";
            debate.SeedPrompt = "Open with your position.";
            debate.TurnBudgetText = "2";
            debate.InitiatorPreamble = "Argue for.";
            debate.ResponderPreamble = "Argue against.";
            flow.GrantReadFiles = true;
            flow.GrantNetworkAccess = true;

            var paths = await flow.SaveAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(paths);
            var bindings = await BindingsProjectionLoader.LoadAsync(
                paths.Value.BindingsFilePath, TestContext.Current.CancellationToken);
            Assert.Null(bindings["debate"].PermissionGrant);
            Assert.Null(bindings["debate"].PermissionScope);
        }
        finally
        {
            if (Directory.Exists(workspacePath))
            {
                DirectoryCleanup.DeleteRecursively(workspacePath);
            }
        }
    }

    [Fact]
    public async Task A_permission_grant_an_in_use_adapter_cant_honor_blocks_save_with_a_plain_language_message()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.SetAdapterRegistry(new Dictionary<string, IWorkerAdapter> { ["gemini"] = new GeminiWorkerAdapter() });
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Kind = GuidedStepKind.Gemini;
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";

        flow.GrantRunShellCommands = true;

        Assert.Contains(
            flow.GuidanceMessages,
            message => message.Contains("gemini", StringComparison.Ordinal) && message.Contains("shell", StringComparison.OrdinalIgnoreCase));
        Assert.False(flow.CanSave);
        Assert.Null(await flow.SaveAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// #657's wording fix reaches this surface too. The old phrasing blamed the builder; what the
    /// operator needs to know is that the values would be ignored at dispatch — recorded once, beside
    /// the string in <c>WorkerBindingEntryViewModel</c>.
    /// </summary>
    [Fact]
    public void An_adapter_that_ignores_grants_says_so_in_guidance_rather_than_blaming_the_builder()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.SetAdapterRegistry(new Dictionary<string, IWorkerAdapter> { ["claude"] = new NoTranslatorWorkerAdapter() });
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Kind = GuidedStepKind.Claude;
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";

        flow.GrantReadFiles = true;

        Assert.Contains(
            flow.GuidanceMessages,
            message => message.Contains("does not enforce permission grants", StringComparison.Ordinal)
                       && message.Contains("ignored at dispatch", StringComparison.Ordinal));
        Assert.DoesNotContain(
            flow.GuidanceMessages,
            message => message.Contains("no structured permission builder support", StringComparison.Ordinal));
        Assert.False(flow.CanSave);
    }

    /// <summary>
    /// #645 on the guided wizard, which is a fourth authoring surface and was missed when the rule
    /// was given one home: it built a grant, validated registry membership and vendor translation,
    /// and never asked whether the grant was coherent at all.
    /// <para>
    /// Claude is the adapter here deliberately: <c>ClaudeWorkerAdapter</c> never refuses a
    /// translation, so nothing else on this path would have caught it. On gemini a shell-only grant
    /// happens to be stopped by the vendor gap instead, which is coincidence rather than coverage.
    /// What this surface costs when the check is missing is recorded at the check itself.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_grant_the_engine_refuses_at_bind_time_blocks_save_in_the_wizard()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.SetAdapterRegistry(new Dictionary<string, IWorkerAdapter> { ["claude"] = new ClaudeWorkerAdapter() });
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Kind = GuidedStepKind.Claude;
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";

        // The shell reaches reads, writes and the network, and all three are unticked.
        flow.GrantRunShellCommands = true;

        Assert.Contains(
            flow.GuidanceMessages,
            message => message.Contains("shell is granted while", StringComparison.Ordinal)
                       && message.Contains("bind time", StringComparison.Ordinal));
        Assert.False(flow.CanSave);
        Assert.Null(await flow.SaveAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The control for the test above: a coherent grant on the same adapter and the same step still
    /// saves. Without this, a validator that refused everything would pass the test above.
    /// </summary>
    [Fact]
    public async Task A_coherent_grant_still_saves_in_the_wizard()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.SetAdapterRegistry(new Dictionary<string, IWorkerAdapter> { ["claude"] = new ClaudeWorkerAdapter() });
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Kind = GuidedStepKind.Claude;
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";

        flow.GrantRunShellCommands = true;
        flow.GrantReadFiles = true;
        flow.GrantWriteFiles = true;
        flow.GrantNetworkAccess = true;

        Assert.DoesNotContain(
            flow.GuidanceMessages,
            message => message.Contains("shell is granted while", StringComparison.Ordinal));
        Assert.True(flow.CanSave);
        Assert.NotNull(await flow.SaveAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Leaving_permissions_unset_never_blocks_save_even_with_no_adapter_registry()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";
        flow.RefreshStructure();

        Assert.Empty(flow.GuidanceMessages);
        Assert.True(flow.CanSave);
    }

    [Fact]
    public void Depends_on_options_follow_the_other_steps_names()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf" };
        flow.AddStepCommand.Execute(null);
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[1].Name = "review";

        Assert.Equal("draft", Assert.Single(flow.Steps[1].DependsOnOptions).StepName);
        Assert.Equal("review", Assert.Single(flow.Steps[0].DependsOnOptions).StepName);

        // A selection survives an unrelated structural refresh (options are rebuilt, state kept).
        flow.Steps[1].DependsOnOptions[0].IsSelected = true;
        flow.RefreshStructure();
        Assert.True(flow.Steps[1].DependsOnOptions.Single(option => option.StepName == "draft").IsSelected);
    }

    [Fact]
    public void Validate_refuses_grant_shapes_that_gemini_adapter_refuses_at_bind()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Kind = GuidedStepKind.Gemini;
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";

        // Shell + patterns is coherent (CategoriesDefeatedByTheShell is empty) but refused by GeminiWorkerAdapter.
        flow.GrantRunShellCommands = true;
        flow.GrantReadFiles = true;
        flow.GrantWriteFiles = true;
        flow.GrantNetworkAccess = true;
        flow.ShellCommandPatternsText = "git:*";

        var message = Assert.Single(flow.GuidanceMessages);
        Assert.Contains("'draft'", message, StringComparison.Ordinal);
        Assert.Contains("AER cannot yet scope an agy shell grant to ShellCommandPatterns", message, StringComparison.Ordinal);
        Assert.False(flow.CanSave);
    }

    [Fact]
    public void Gemini_step_with_shell_and_network_grant_validates()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Kind = GuidedStepKind.Gemini;
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";

        flow.GrantRunShellCommands = true;
        flow.GrantReadFiles = true;
        flow.GrantWriteFiles = true;
        flow.GrantNetworkAccess = true;

        Assert.DoesNotContain(flow.GuidanceMessages, m => m.Contains("can't be granted to", StringComparison.Ordinal));
        Assert.True(flow.CanSave);
    }

    [Fact]
    public void Claude_step_with_shell_and_patterns_grant_validates()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Kind = GuidedStepKind.Claude;
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";

        flow.GrantRunShellCommands = true;
        flow.GrantReadFiles = true;
        flow.GrantWriteFiles = true;
        flow.GrantNetworkAccess = true;
        flow.ShellCommandPatternsText = "git:*";

        Assert.DoesNotContain(flow.GuidanceMessages, m => m.Contains("can't be granted to", StringComparison.Ordinal));
        Assert.True(flow.CanSave);
    }

    [Fact]
    public void Dialogue_step_is_unaffected_by_adapter_grant_refusal()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Kind = GuidedStepKind.Dialogue;
        flow.Steps[0].ProducesFileName = "draft.md";
        flow.Steps[0].SeedPrompt = "Opening prompt.";
        flow.Steps[0].InitiatorPreamble = "Preamble 1";
        flow.Steps[0].ResponderPreamble = "Preamble 2";

        flow.GrantRunShellCommands = true;
        flow.GrantReadFiles = true;
        flow.GrantWriteFiles = true;
        flow.GrantNetworkAccess = true;
        flow.ShellCommandPatternsText = "git:*";

        Assert.DoesNotContain(flow.GuidanceMessages, m => m.Contains("can't be granted to", StringComparison.Ordinal));
        Assert.True(flow.CanSave);
    }
}

/// <summary>An adapter that never implements <see cref="IPermissionGrantTranslator"/> — the "no structured permission builder support" guidance path.</summary>
internal sealed class NoTranslatorWorkerAdapter : IWorkerAdapter
{
    public Aer.Flow.Dispatch.CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract) =>
        throw new NotSupportedException("This test adapter never dispatches a real invocation.");
}
