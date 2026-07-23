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

    /// <summary>
    /// The default <see cref="PermissionGrant"/> for an interactive session that supplied no explicit
    /// grant. A working directory is a project ceiling (decision 0004); with none, the effective grant
    /// floors to the intersection and MUST fail closed -- no filesystem, shell, or network -- so a
    /// directory-less "plain chat" cannot inherit the daemon/app cwd with write access nobody scoped
    /// (#321). With a directory, the conservative codebase default applies (read + write, no shell or
    /// network). This is the single home for that policy; every materialize path routes through it.
    /// </summary>
    public static PermissionGrant DefaultGrantForWorkingDirectory(string? workingDirectory) =>
        string.IsNullOrWhiteSpace(workingDirectory)
            ? new PermissionGrant(ReadFiles: false, WriteFiles: false, RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false)
            : new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false);

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
        var defaultGrant = grant ?? DefaultGrantForWorkingDirectory(workingDirectory);

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
                    // NeedsInput, not the default ReadyForReview: a settled chat turn is "awaiting your
                    // next message," never "approve finished work" (#334). This is the one declaration
                    // site that opts out of the approval-gate default; every authored review gate keeps it.
                    PausePoint: new PausePoint([new StepId(DefaultStepId)], PausePointKind.NeedsInput))
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

        var baseSessionsDir = AerPaths.Sessions;
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

    /// <summary>
    /// How many times <see cref="SaveMetadataAsync"/> and <see cref="LoadMetadataAsync"/> retry a
    /// sharing-violation before giving up, and how long they wait between attempts. Small on
    /// purpose: the contended window is a single file replace, so anything that outlasts a handful
    /// of these is a real fault rather than contention, and should surface as one.
    /// </summary>
    private const int MetadataIoAttempts = 12;
    private static readonly TimeSpan MetadataIoRetryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Writes <c>session.json</c> so that a concurrent reader can neither fail the write nor observe
    /// a half-written file.
    /// <para>
    /// Issue #341: this used a plain <c>File.WriteAllTextAsync</c> against the live path while
    /// <see cref="LoadMetadataAsync"/> used a plain <c>File.ReadAllTextAsync</c>. <c>ReadAllText</c>
    /// opens with <c>FileShare.Read</c>, which denies writers -- so on Windows any client polling
    /// <c>GET /api/sessions/{id}</c> while a turn finished made the turn's own metadata write throw
    /// <c>IOException</c>. That throw happened *after* the Supersede decision had already been
    /// recorded, so the workflow was healthy and only <c>TurnCount</c> never persisted: the chat
    /// stalled forever with an intact event log, and the exception died in a fire-and-forget task.
    /// POSIX permits the concurrent open, which is why this only ever reproduced on Windows.
    /// </para>
    /// <para>
    /// The fix is on the reader: once <see cref="LoadMetadataAsync"/> stops denying write access,
    /// this ordinary write succeeds. A brief retry stays for the genuinely concurrent case (two
    /// turns finishing at once), since the writer's own <c>FileShare.Read</c> excludes a second
    /// writer. Replace-via-temp was tried first and is worse here: Windows'
    /// <c>MOVEFILE_REPLACE_EXISTING</c> needs delete rights on the target and throws
    /// <see cref="UnauthorizedAccessException"/> against a live reader, trading one race for another.
    /// </para>
    /// </summary>
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

        await RetryOnSharingViolationAsync(
            () => File.WriteAllTextAsync(filePath, json, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads <c>session.json</c> without denying a concurrent writer -- see
    /// <see cref="SaveMetadataAsync"/> for why that matters. Opening with
    /// <c>FileShare.ReadWrite | FileShare.Delete</c> also permits the replace this file's writer
    /// performs.
    /// </summary>
    public static async Task<SessionMetadata?> LoadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) return null;

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        SessionMetadata? result = null;
        await RetryOnSharingViolationAsync(
            async () =>
            {
                using var stream = new FileStream(
                    filePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096, useAsync: true);
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                // Permitting a concurrent writer is what makes the write above succeed, and the
                // cost is that this read can land mid-rewrite and see a truncated document. That
                // state is always transient -- the writer completes -- so a torn parse is retried
                // rather than surfaced. Deserialize inside the retry so both failures share it.
                result = JsonSerializer.Deserialize<SessionMetadata>(json, options);
            },
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Retries <paramref name="action"/> through the transient states of a concurrently
    /// read-and-rewritten file: a sharing violation, a denied replace, or a document parsed
    /// mid-write. The final attempt rethrows, so a genuine fault (a corrupt file that never settles,
    /// a real permissions problem) still surfaces rather than being retried into silence -- silence
    /// is what made #341 cost a day.
    /// </summary>
    private static async Task RetryOnSharingViolationAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < MetadataIoAttempts
                                       && ex is IOException or UnauthorizedAccessException or JsonException)
            {
                await Task.Delay(MetadataIoRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
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
