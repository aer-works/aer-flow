using Aer.Flow.Domain;
using Aer.Flow.Templates;

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
    public static readonly BuiltInTemplateInfo SoloRun = new(
        Id: "solo-run",
        Title: "Solo Run",
        Description: "Single-step execution using an installed AI worker (Claude or Gemini).",
        RequiresSecondaryVendor: false);

    public static readonly BuiltInTemplateInfo ReviewRun = new(
        Id: "review-run",
        Title: "Review Run",
        Description: "Two-step workflow where one AI worker drafts content and another AI worker reviews it with human sign-off.",
        RequiresSecondaryVendor: true);

    public static IReadOnlyList<BuiltInTemplateInfo> Catalog { get; } = [SoloRun, ReviewRun];

    /// <summary>
    /// Materializes a built-in template's <see cref="WorkflowDefinition"/> and worker bindings.
    /// </summary>
    public static (WorkflowDefinition Definition, IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings) Materialize(
        string templateId,
        string primaryAdapter,
        string? secondaryAdapter = null,
        string? customPrompt = null)
    {
        var normalizedPrimary = string.IsNullOrWhiteSpace(primaryAdapter) ? "claude" : primaryAdapter.Trim().ToLowerInvariant();
        var normalizedSecondary = string.IsNullOrWhiteSpace(secondaryAdapter) ? normalizedPrimary : secondaryAdapter.Trim().ToLowerInvariant();

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
                    PromptTemplate: "Review draft.md carefully, provide feedback and recommendations, and write to review.md.",
                    Timeout: TimeSpan.FromMinutes(10),
                    PermissionGrant: defaultGrant)
            };

            return (definition, bindings);
        }

        throw new ArgumentException($"Unknown template ID '{templateId}'. Valid template IDs are 'solo-run' and 'review-run'.", nameof(templateId));
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
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(taskDirectoryPath);
        var (definition, bindings) = Materialize(templateId, primaryAdapter, secondaryAdapter, customPrompt);

        var workflowFilePath = Path.Combine(taskDirectoryPath, "workflow.json");
        var bindingsFilePath = Path.Combine(taskDirectoryPath, "bindings.json");

        await WorkflowDefinitionWriter.SaveToFileAsync(definition, workflowFilePath, cancellationToken).ConfigureAwait(false);
        await WorkerBindingConfigWriter.SaveToFileAsync(bindings, bindingsFilePath, cancellationToken).ConfigureAwait(false);

        var aerDir = Path.Combine(taskDirectoryPath, ".aer");
        Directory.CreateDirectory(aerDir);
        await File.WriteAllTextAsync(Path.Combine(aerDir, "workflow-path"), workflowFilePath, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(aerDir, "bindings-path"), bindingsFilePath, cancellationToken).ConfigureAwait(false);
    }
}
