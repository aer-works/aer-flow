using System.Text.Json;
using Aer.Flow.Domain;
using Aer.Flow.Templates;
using Aer.Workers.Dialogue;

namespace Aer.Adapters;

/// <summary>
/// Information describing a built-in workflow template (M22 Phase 1).
/// </summary>
public sealed record BuiltInTemplateInfo(
    string Id,
    string Title,
    string Description,
    bool RequiresSecondaryVendor);

/// <summary>
/// Pre-authored workflow template catalog and materialization engine (M22 Phase 1).
/// Provides Solo Run and Review Run templates that materialize valid workflow definitions
/// and worker bindings against available vendor CLIs.
/// </summary>
public static class BuiltInWorkflowTemplates
{
    public static readonly BuiltInTemplateInfo ChatSession = new(
        Id: "chat-session",
        Title: "Chat (Interactive Session)",
        Description: "Interactive 1-on-1 session with an AI worker (Claude or Gemini) with live turn streaming and session resumption.",
        RequiresSecondaryVendor: false);

    public static readonly BuiltInTemplateInfo CodebaseSession = new(
        Id: "codebase-session",
        Title: "Codebase Session",
        Description: "Interactive AI agent session bound to a project directory with conservative file/command permissions.",
        RequiresSecondaryVendor: false);

    public static readonly BuiltInTemplateInfo TwoVendorDialogue = new(
        Id: "two-vendor-dialogue",
        Title: "Two-Vendor Dialogue",
        Description: "Multi-vendor dialogue exchange between Claude and Gemini with turn-by-turn context synthesis.",
        RequiresSecondaryVendor: true);

    public static readonly BuiltInTemplateInfo SoloRun = new(
        Id: "solo-run",
        Title: "Solo Run (Advanced)",
        Description: "Single-step execution using an installed AI worker (Claude or Gemini).",
        RequiresSecondaryVendor: false);

    public static readonly BuiltInTemplateInfo ReviewRun = new(
        Id: "review-run",
        Title: "Review Run (Advanced)",
        Description: "Two-step workflow where one AI worker drafts content and another AI worker reviews it with human sign-off.",
        RequiresSecondaryVendor: true);

    public static IReadOnlyList<BuiltInTemplateInfo> Catalog { get; } = [ChatSession, CodebaseSession, TwoVendorDialogue, SoloRun, ReviewRun];

    /// <summary>
    /// Materializes a built-in template's <see cref="WorkflowDefinition"/> and worker bindings.
    /// </summary>
    public static (WorkflowDefinition Definition, IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings) Materialize(
        string templateId,
        string primaryAdapter,
        string? secondaryAdapter = null,
        string? customPrompt = null,
        string? secondaryCustomPrompt = null,
        string? taskDirectoryPath = null)
    {
        var normalizedPrimary = string.IsNullOrWhiteSpace(primaryAdapter) ? "claude" : primaryAdapter.Trim().ToLowerInvariant();
        var normalizedSecondary = string.IsNullOrWhiteSpace(secondaryAdapter) ? normalizedPrimary : secondaryAdapter.Trim().ToLowerInvariant();

        if (string.Equals(templateId, ChatSession.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(templateId, CodebaseSession.Id, StringComparison.OrdinalIgnoreCase))
        {
            var (def, bindings, _) = InteractiveSessionMaterializer.Materialize(
                sessionId: Guid.NewGuid().ToString("N")[..12],
                taskDirectoryPath: string.Empty,
                adapter: normalizedPrimary,
                initialMessage: customPrompt);
            return (def, bindings);
        }

        if (string.Equals(templateId, TwoVendorDialogue.Id, StringComparison.OrdinalIgnoreCase))
        {
            // M23 Phase 1's real N-party dialogue worker (Aer.Workers.Dialogue), not a hand-rolled
            // draft/review DAG: a two-vendor dialogue is a single bounded exchange the worker itself
            // round-robins through DialogueWorkerConfig.Participants, so this is one step, one
            // binding, dispatched through the "dialogue" adapter -- exactly the shape
            // NewWorkflowViewModel's guided authoring already produces (Aer.Ui.Core/NewWorkflowViewModel.cs).
            const string finalOutputName = "transcript.md";

            var dialogueConfig = new DialogueWorkerConfig(
                SeedPrompt: string.IsNullOrWhiteSpace(customPrompt) ? "Discuss the topic thoroughly, considering multiple angles." : customPrompt,
                TurnBudget: 6,
                FinalOutputName: finalOutputName,
                StopSentinel: null,
                Participants:
                [
                    DialogueParticipantPresets.For(
                        normalizedPrimary,
                        "initiator",
                        string.IsNullOrWhiteSpace(customPrompt) ? "You are the initiator of this dialogue. Open with your position and respond to the other side's points." : customPrompt,
                        model: null),
                    DialogueParticipantPresets.For(
                        normalizedSecondary,
                        "responder",
                        string.IsNullOrWhiteSpace(secondaryCustomPrompt) ? "You are the responder in this dialogue. Engage constructively with the initiator's points." : secondaryCustomPrompt,
                        model: null),
                ]);

            var sidecarDirectory = string.IsNullOrWhiteSpace(taskDirectoryPath) ? Path.GetTempPath() : taskDirectoryPath;
            Directory.CreateDirectory(sidecarDirectory);
            var sidecarPath = Path.Combine(sidecarDirectory, "dialogue-config.json");
            File.WriteAllText(sidecarPath, JsonSerializer.Serialize(dialogueConfig, new JsonSerializerOptions { WriteIndented = true }));

            var definition = new WorkflowDefinition(
                WorkflowTemplateId: new WorkflowTemplateId("two-vendor-dialogue-template"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        StepId: new StepId("dialogue"),
                        Worker: "dialogue-worker",
                        Inputs: [],
                        Outputs: [finalOutputName],
                        DependsOn: [],
                        RetryPolicy: new RetryPolicy(3),
                        PausePoint: null)
                ]);

            var bindings = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["dialogue-worker"] = new WorkerBindingConfigEntry(
                    Adapter: "dialogue",
                    Contract: new WorkerContract(
                        WorkerName: "dialogue-worker",
                        RequiredInputs: [],
                        ProducedOutputs: [new ProducedOutput(finalOutputName)],
                        OptionalMetadata: []),
                    PromptTemplate: sidecarPath,
                    Timeout: TimeSpan.FromMinutes(20))
            };

            return (definition, bindings);
        }

        if (string.Equals(templateId, SoloRun.Id, StringComparison.OrdinalIgnoreCase))
        {
            var definition = new WorkflowDefinition(
                WorkflowTemplateId: new WorkflowTemplateId("solo-run-template"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        StepId: new StepId("solo-step"),
                        Worker: "solo-worker",
                        Inputs: [],
                        Outputs: ["output.md"],
                        DependsOn: [],
                        RetryPolicy: new RetryPolicy(3),
                        PausePoint: null)
                ]);

            var defaultGrant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false);

            var bindings = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["solo-worker"] = new WorkerBindingConfigEntry(
                    Adapter: normalizedPrimary,
                    Contract: new WorkerContract(
                        WorkerName: "solo-worker",
                        RequiredInputs: [],
                        ProducedOutputs: [new ProducedOutput("output.md")],
                        OptionalMetadata: []),
                    PromptTemplate: string.IsNullOrWhiteSpace(customPrompt) ? "Perform the requested solo task and write the output to output.md." : customPrompt,
                    Timeout: TimeSpan.FromMinutes(10),
                    PermissionGrant: defaultGrant)
            };

            return (definition, bindings);
        }

        if (string.Equals(templateId, ReviewRun.Id, StringComparison.OrdinalIgnoreCase))
        {
            var defaultGrant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false);

            var definition = new WorkflowDefinition(
                WorkflowTemplateId: new WorkflowTemplateId("review-run-template"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        StepId: new StepId("draft"),
                        Worker: "draft-worker",
                        Inputs: [],
                        Outputs: ["draft.md"],
                        DependsOn: [],
                        RetryPolicy: new RetryPolicy(3),
                        PausePoint: null),
                    new WorkflowStepDefinition(
                        StepId: new StepId("review"),
                        Worker: "review-worker",
                        Inputs: ["draft.md"],
                        Outputs: ["review.md"],
                        DependsOn: [new StepId("draft")],
                        RetryPolicy: new RetryPolicy(3),
                        PausePoint: new PausePoint([new StepId("draft")]))
                ]);

            var bindings = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["draft-worker"] = new WorkerBindingConfigEntry(
                    Adapter: normalizedPrimary,
                    Contract: new WorkerContract(
                        WorkerName: "draft-worker",
                        RequiredInputs: [],
                        ProducedOutputs: [new ProducedOutput("draft.md")],
                        OptionalMetadata: []),
                    PromptTemplate: string.IsNullOrWhiteSpace(customPrompt) ? "Draft initial content for the requested topic and write to draft.md." : customPrompt,
                    Timeout: TimeSpan.FromMinutes(10),
                    PermissionGrant: defaultGrant),
                ["review-worker"] = new WorkerBindingConfigEntry(
                    Adapter: normalizedSecondary,
                    Contract: new WorkerContract(
                        WorkerName: "review-worker",
                        RequiredInputs: ["draft.md"],
                        ProducedOutputs: [new ProducedOutput("review.md")],
                        OptionalMetadata: []),
                    PromptTemplate: string.IsNullOrWhiteSpace(secondaryCustomPrompt) ? "Review draft.md carefully, provide feedback and recommendations, and write to review.md." : secondaryCustomPrompt,
                    Timeout: TimeSpan.FromMinutes(10),
                    PermissionGrant: defaultGrant)
            };

            return (definition, bindings);
        }

        throw new ArgumentException(
            $"Unknown template ID '{templateId}'. Valid template IDs are: {string.Join(", ", Catalog.Select(t => t.Id))}.",
            nameof(templateId));
    }

    /// <summary>
    /// Materializes and persists the template definition (<c>workflow.json</c>) and bindings (<c>bindings.json</c>)
    /// into <paramref name="taskDirectoryPath"/>, along with <c>.aer/workflow-path</c> and <c>.aer/bindings-path</c> metadata.
    /// </summary>
    public static async Task MaterializeToDirectoryAsync(
        string templateId,
        string primaryAdapter,
        string? secondaryAdapter,
        string taskDirectoryPath,
        string? customPrompt = null,
        string? secondaryCustomPrompt = null,
        CancellationToken cancellationToken = default)
    {
        var workflowFilePath = Path.Combine(taskDirectoryPath, "workflow.json");
        if (File.Exists(workflowFilePath))
        {
            throw new TaskDirectoryAlreadyExistsException(
                TaskLifecycle.IsArchived(taskDirectoryPath)
                    ? $"A task already exists at '{taskDirectoryPath}' and is archived. Unarchive or delete it before reusing this name."
                    : $"A task already exists at '{taskDirectoryPath}'. Choose a different task/session name.");
        }

        Directory.CreateDirectory(taskDirectoryPath);
        var (definition, bindings) = Materialize(templateId, primaryAdapter, secondaryAdapter, customPrompt, secondaryCustomPrompt, taskDirectoryPath);

        var bindingsFilePath = Path.Combine(taskDirectoryPath, "bindings.json");

        await WorkflowDefinitionWriter.SaveToFileAsync(definition, workflowFilePath, cancellationToken).ConfigureAwait(false);
        await WorkerBindingConfigWriter.SaveToFileAsync(bindings, bindingsFilePath, cancellationToken).ConfigureAwait(false);

        var aerDir = Path.Combine(taskDirectoryPath, ".aer");
        Directory.CreateDirectory(aerDir);
        await File.WriteAllTextAsync(Path.Combine(aerDir, "workflow-path"), workflowFilePath, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(aerDir, "bindings-path"), bindingsFilePath, cancellationToken).ConfigureAwait(false);
    }
}
