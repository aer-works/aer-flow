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
    bool VendorHandoffSynthesized);

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
    List<SessionTurn> Turns);

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
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(
                    StepId: new StepId(DefaultStepId),
                    Worker: DefaultWorkerName,
                    Inputs: [],
                    Outputs: [DefaultOutputFileName],
                    DependsOn: [],
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
                WorkingDirectory: workingDirectory)
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
            MinimalOverhead: true,
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
                $"A task already exists at '{taskDirectoryPath}'. Choose a different task/session name.");
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
