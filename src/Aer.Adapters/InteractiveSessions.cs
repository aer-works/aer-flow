using System.Text.Json;
using Aer.Flow.Domain;
using Aer.Flow.Templates;

namespace Aer.Adapters;

public sealed record SessionTurn(
    int TurnIndex,
    string Vendor,
    string HumanMessage,
    string? AssistantResponse,
    DateTimeOffset ExecutedAt,
    bool NativeSessionResumed,
    bool VendorHandoffSynthesized,
    string? ErrorMessage = null);

public sealed record SessionMetadata(
    string SessionId,
    string TaskDirectoryPath,
    string CurrentAdapter,
    string? CurrentVendorSessionId,
    string? Model,
    string? WorkingDirectory,
    int TurnCount,
    int SafetyCeiling,
    bool MinimalOverhead,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    List<SessionTurn> Turns,
    // False until a turn actually completes against CurrentVendorSessionId (M24 Phase 5.1, #285):
    // the id is minted client-side at materialization/handoff time, before the vendor CLI has ever
    // heard of it, so its mere presence can't tell a caller whether `--resume` is safe yet. Absent
    // in session.json files written before this field existed -- System.Text.Json defaults it to
    // false on load, which is the safe direction (worst case: one redundant `--session-id` retry
    // instead of a guaranteed-failing `--resume`).
    bool VendorSessionEstablished = false);

public sealed record StartSessionRequest(
    string? Adapter = null,
    string? Model = null,
    string? TaskName = null,
    string? DirectoryPath = null,
    string? WorkingDirectory = null,
    string? InitialMessage = null,
    int? SafetyCeiling = null,
    PermissionGrant? PermissionGrant = null);

public sealed record SendSessionMessageRequest(
    string? SessionId = null,
    string? DirectoryPath = null,
    string Message = "",
    string? Adapter = null,
    string? Model = null);

public static class InteractiveSessionMaterializer
{
    public const int DefaultSafetyCeiling = 100;
    public const string DefaultStepId = "chat";
    public const string DefaultWorkerName = "chat-worker";
    public const string DefaultOutputFileName = "response.md";

    // M24 Phase 5.2 (#285): a downstream anchor step exists purely to give a repeated-turn
    // `Supersede` (spec §17.5) a legal target. `Supersede`'s target must be a distinct transitive
    // ancestor (§17.1) -- a single "chat" step targeting itself is spec-illegal three ways (self-
    // target, no ancestor, no supplementary artifact possible) and was silently no-oping every turn
    // after the first (see #285's investigation notes). "chat" itself now declares no PausePoint at
    // all, so a successful turn flows straight through to the anchor without stopping -- Anchor's own
    // PausePoint (targeting "chat") is what actually pauses the workflow, ready for the next turn's
    // Supersede. This also means "chat" has no pause-driven retry path of its own: a first-ever turn
    // that fails outright leaves the workflow terminally failed with nothing to Decide against, which
    // is why Aer.Daemon's turn-execution code detects "anchor has never succeeded" and re-materializes
    // Flow's own state fresh for that one narrow case, rather than issuing a decision.
    public const string AnchorStepId = "turn-anchor";
    public const string AnchorWorkerName = "turn-anchor-worker";
    public const string AnchorOutputFileName = "turn.marker";

    public static (WorkflowDefinition Definition, IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings, SessionMetadata Metadata) Materialize(
        string sessionId,
        string taskDirectoryPath,
        string adapter,
        string? model = null,
        string? workingDirectory = null,
        string? initialMessage = null,
        int safetyCeiling = DefaultSafetyCeiling,
        PermissionGrant? grant = null)
    {
        var normalizedAdapter = string.IsNullOrWhiteSpace(adapter) ? "claude" : adapter.Trim().ToLowerInvariant();
        var defaultGrant = grant ?? new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false);

        var definition = new WorkflowDefinition(
            WorkflowTemplateId: new WorkflowTemplateId("interactive-session-template"),
            WorkflowTemplateVersion: 2,
            Steps:
            [
                new WorkflowStepDefinition(
                    StepId: new StepId(DefaultStepId),
                    Worker: DefaultWorkerName,
                    Inputs: [],
                    Outputs: [DefaultOutputFileName],
                    DependsOn: [],
                    RetryPolicy: new RetryPolicy(1)),
                new WorkflowStepDefinition(
                    StepId: new StepId(AnchorStepId),
                    Worker: AnchorWorkerName,
                    Inputs: [DefaultOutputFileName],
                    Outputs: [AnchorOutputFileName],
                    DependsOn: [new StepId(DefaultStepId)],
                    RetryPolicy: new RetryPolicy(1),
                    PausePoint: new PausePoint([new StepId(DefaultStepId)]))
            ]);

        var promptTemplate = string.IsNullOrWhiteSpace(initialMessage)
            ? "You are an AI assistant in an interactive session. Answer user questions and perform requested tasks."
            : initialMessage;

        var vendorSessionId = string.Equals(normalizedAdapter, "claude", StringComparison.OrdinalIgnoreCase)
            ? Guid.NewGuid().ToString()
            : null;

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            [DefaultWorkerName] = new WorkerBindingConfigEntry(
                Adapter: normalizedAdapter,
                Contract: new WorkerContract(
                    WorkerName: DefaultWorkerName,
                    RequiredInputs: [],
                    ProducedOutputs: [new ProducedOutput(DefaultOutputFileName)],
                    OptionalMetadata: []),
                PromptTemplate: promptTemplate,
                Timeout: TimeSpan.FromMinutes(10),
                PermissionGrant: defaultGrant,
                Model: model,
                WorkingDirectory: workingDirectory),
            [AnchorWorkerName] = new WorkerBindingConfigEntry(
                Adapter: NoOpWorkerAdapter.AdapterName,
                Contract: new WorkerContract(
                    WorkerName: AnchorWorkerName,
                    RequiredInputs: [DefaultOutputFileName],
                    ProducedOutputs: [new ProducedOutput(AnchorOutputFileName)],
                    OptionalMetadata: []),
                PromptTemplate: "(no-op bookkeeping step; ignored)",
                Timeout: TimeSpan.FromSeconds(30),
                PermissionGrant: new PermissionGrant(ReadFiles: false, WriteFiles: false, RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false))
        };

        var metadata = new SessionMetadata(
            SessionId: sessionId,
            TaskDirectoryPath: taskDirectoryPath,
            CurrentAdapter: normalizedAdapter,
            CurrentVendorSessionId: vendorSessionId,
            Model: model,
            WorkingDirectory: workingDirectory,
            TurnCount: 0,
            SafetyCeiling: safetyCeiling > 0 ? safetyCeiling : DefaultSafetyCeiling,
            // Live-verified (retroactive M24 Phase 1 gap-fill, #262): claude --bare's own --help
            // text documents that it skips "keychain reads" for minimal overhead -- which is exactly
            // where subscription OAuth login lives. A --bare dispatch against a real subscription
            // login fails immediately with "Not logged in", even with valid, unexpired credentials.
            // Since this project's whole point is working against subscriptions rather than API
            // keys (CLAUDE.md), MinimalOverhead can never default to true -- doing so silently broke
            // every interactive-session turn for the primary supported auth path.
            MinimalOverhead: false,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Turns: []);

        return (definition, bindings, metadata);
    }

    /// <summary>
    /// Computes a session's directory path the same way for every caller -- the daemon's
    /// POST /api/sessions/start handler and the desktop's in-process fallback both need this, and
    /// disagreeing between them is exactly the bug that made a session creatable but unreachable by
    /// id (fixed in Aer.Daemon.Program's session lookups). A caller-supplied <paramref name="taskName"/>
    /// produces a differently-named folder than the "session-{id}" fallback used when it is omitted.
    /// </summary>
    public static string ResolveTaskDirectoryPath(string sessionId, string? taskName, string? directoryPathOverride)
    {
        if (directoryPathOverride != null && Path.IsPathRooted(directoryPathOverride))
        {
            return directoryPathOverride;
        }

        var baseSessionsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aer", "sessions");
        var folderName = string.IsNullOrWhiteSpace(taskName) ? $"session-{sessionId}" : taskName.Trim();
        return Path.GetFullPath(Path.Combine(baseSessionsDir, folderName));
    }

    public static async Task<SessionMetadata> MaterializeToDirectoryAsync(
        string sessionId,
        string taskDirectoryPath,
        string adapter,
        string? model = null,
        string? workingDirectory = null,
        string? initialMessage = null,
        int safetyCeiling = DefaultSafetyCeiling,
        PermissionGrant? grant = null,
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
        var (definition, bindings, metadata) = Materialize(
            sessionId, taskDirectoryPath, adapter, model, workingDirectory, initialMessage, safetyCeiling, grant);

        var bindingsFilePath = Path.Combine(taskDirectoryPath, "bindings.json");
        var metadataFilePath = Path.Combine(taskDirectoryPath, ".aer", "session.json");

        await WorkflowDefinitionWriter.SaveToFileAsync(definition, workflowFilePath, cancellationToken).ConfigureAwait(false);
        await WorkerBindingConfigWriter.SaveToFileAsync(bindings, bindingsFilePath, cancellationToken).ConfigureAwait(false);

        var aerDir = Path.Combine(taskDirectoryPath, ".aer");
        Directory.CreateDirectory(aerDir);
        await File.WriteAllTextAsync(Path.Combine(aerDir, "workflow-path"), workflowFilePath, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(aerDir, "bindings-path"), bindingsFilePath, cancellationToken).ConfigureAwait(false);

        await SaveMetadataAsync(metadata, metadataFilePath, cancellationToken).ConfigureAwait(false);

        return metadata;
    }

    public static async Task SaveMetadataAsync(SessionMetadata metadata, string filePath, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var json = JsonSerializer.Serialize(metadata, options);
        await File.WriteAllTextAsync(filePath, json, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SessionMetadata?> LoadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) return null;

        var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        return JsonSerializer.Deserialize<SessionMetadata>(json, options);
    }

    public static string SynthesizeContextSummary(IReadOnlyList<SessionTurn> turns, string newMessage)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Previous conversation transcript summary for context:");
        foreach (var turn in turns)
        {
            sb.AppendLine($"User: {turn.HumanMessage}");
            if (!string.IsNullOrWhiteSpace(turn.AssistantResponse))
            {
                sb.AppendLine($"Assistant: {turn.AssistantResponse}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Now continue with the following user request:");
        sb.AppendLine(newMessage);
        return sb.ToString();
    }
}
