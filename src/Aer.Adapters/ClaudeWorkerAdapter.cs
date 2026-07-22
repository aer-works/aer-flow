using System.Text;
using System.Text.Json;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters;

/// <summary>
/// Direct shell-less <see cref="IWorkerAdapter"/> (M20 Phase 4): resolves a
/// <see cref="WorkerInvocation"/>/<see cref="WorkerContract"/> pair into a direct <c>claude</c>
/// invocation without shell wrappers. Bypasses cmd.exe and sh, eliminating quoting and command injection risks.
/// Stdin redirection to null is handled natively by the process host.
/// <para>
/// <b>M21 Phase 1's <see cref="IPermissionGrantTranslator"/>:</b> Claude Code's <c>--allowedTools</c>
/// is tool-name-based (<c>Read</c>, <c>Edit</c>, <c>Write</c>, <c>Bash</c>/<c>Bash(pattern)</c>,
/// <c>WebFetch</c>, <c>WebSearch</c>), which composes every <see cref="PermissionGrant"/> category
/// exactly — this adapter's translation never refuses.
/// </para>
/// </summary>
public sealed class ClaudeWorkerAdapter : IWorkerAdapter, IPermissionGrantTranslator
{
    private const string DefaultPermissionScope = "Write";

    public bool TryTranslatePermissionGrant(PermissionGrant grant, out string? resolvedValue, out string? gapReason)
    {
        ArgumentNullException.ThrowIfNull(grant);

        List<string> tools = [];
        if (grant.ReadFiles)
        {
            tools.Add("Read");
        }

        if (grant.WriteFiles)
        {
            tools.Add("Edit");
            tools.Add("Write");
        }

        if (grant.RunShellCommands)
        {
            if (grant.ShellCommandPatterns is { Count: > 0 } patterns)
            {
                tools.AddRange(patterns.Select(pattern => $"Bash({pattern})"));
            }
            else
            {
                tools.Add("Bash");
            }
        }

        if (grant.NetworkAccess)
        {
            tools.Add("WebFetch");
            tools.Add("WebSearch");
        }

        resolvedValue = string.Join(',', tools);
        gapReason = null;
        return true;
    }

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(contract);

        var isWindows = OperatingSystem.IsWindows();
        var prompt = BuildPrompt(invocation.PromptTemplate, contract, isWindows);
        var permissionScope = ResolvePermissionScope(invocation);
        var artifactsRoot = EnvironmentReference("AER_ARTIFACTS_ROOT", isWindows);

        List<string> args =
        [
            "-p", prompt,
            "--allowedTools", permissionScope,
            // #289: Claude Code enforces its own directory-trust sandbox independent of
            // --allowedTools, and (confirmed empirically against the real, authenticated CLI)
            // non-deterministically refuses to write outside it when AER_OUTPUT_DIR falls outside
            // the spawned process's cwd -- which it always does for a plain chat session, since
            // ExecuteSessionTurnAsync never sets WorkerInvocation.WorkingDirectory unless the
            // session is attached to a codebase. Reproduced identically via a bare manual `claude`
            // invocation (not daemon-specific): ~50% of otherwise-identical trials silently failed
            // to produce their declared output file, each citing "outside the sandboxed worktree" /
            // "outside the allowed working directories" as its own reason, until this flag was
            // added -- 0/6 failures with it across the same trial shape. Mirrors the same grant
            // GeminiWorkerAdapter has carried since spike #21 for the identical reason (agy ignores
            // the invoking process's cwd entirely); Claude turned out to need it too, just only
            // sometimes, which is what made the gap easy to miss.
            "--add-dir", artifactsRoot,
        ];

        if (invocation.StreamJson)
        {
            // --print + --output-format=stream-json refuses to run without --verbose (confirmed
            // against the installed claude CLI directly: "Error: When using --print,
            // --output-format=stream-json requires --verbose") -- without this flag every
            // streaming session turn would fail at the CLI invocation itself, before producing any
            // output at all.
            args.Add("--output-format");
            args.Add("stream-json");
            args.Add("--include-partial-messages");
            args.Add("--verbose");
        }
        else
        {
            args.Add("--output-format");
            args.Add("text");
        }

        if (invocation.MinimalOverhead)
        {
            args.Add("--bare");
        }

        if (invocation.SessionId is not null)
        {
            if (invocation.ResumeSession)
            {
                args.Add("--resume");
                args.Add(invocation.SessionId);
            }
            else
            {
                args.Add("--session-id");
                args.Add(invocation.SessionId);
            }
        }

        if (invocation.Model is not null)
        {
            args.Add("--model");
            args.Add(invocation.Model);
        }

        return new CoreDispatchTarget("claude", [.. args], invocation.WorkingDirectory, PromptText: prompt);
    }

    /// <summary>
    /// A structured <see cref="WorkerInvocation.PermissionGrant"/> always wins over the raw
    /// <see cref="WorkerInvocation.PermissionScope"/> string (<see cref="PermissionGrant"/>'s own
    /// docs record this precedence); <see cref="TryTranslatePermissionGrant"/> never refuses for
    /// this adapter, so this never throws.
    /// </summary>
    private string ResolvePermissionScope(WorkerInvocation invocation)
    {
        if (invocation.PermissionGrant is { } grant)
        {
            if (!TryTranslatePermissionGrant(grant, out var resolved, out var gapReason))
            {
                throw new PermissionGrantUnsupportedException("claude", gapReason!);
            }

            return resolved!;
        }

        return invocation.PermissionScope ?? DefaultPermissionScope;
    }

    private static string BuildPrompt(string promptTemplate, WorkerContract contract, bool isWindows)
    {
        var prompt = new StringBuilder(promptTemplate);

        if (contract.RequiredInputs.Count > 0)
        {
            prompt.Append("\n\nInputs, in the order listed, are available at:\n");
            for (var i = 0; i < contract.RequiredInputs.Count; i++)
            {
                prompt.Append($"- {contract.RequiredInputs[i]}: {EnvironmentReference($"AER_INPUT_{i}", isWindows)}\n");
            }
        }

        if (contract.ProducedOutputs.Count > 0)
        {
            prompt.Append("\nWrite each of the following outputs to the exact path shown, creating parent directories as needed:\n");
            foreach (var output in contract.ProducedOutputs)
            {
                var outputDir = EnvironmentReference("AER_OUTPUT_DIR", isWindows);
                var separator = isWindows ? '\\' : '/';
                prompt.Append($"- {output.Name}: {outputDir}{separator}{output.Name}\n");
            }
        }

        return prompt.ToString();
    }

    private static string EnvironmentReference(string name, bool isWindows) => isWindows ? $"%{name}%" : $"${name}";

    /// <summary>
    /// Claude Code has no machine-readable "list models" subcommand — <c>--model</c> only documents
    /// its accepted values as help-text examples (<c>claude --help</c>: "Provide an alias for the
    /// latest model (e.g. 'sonnet', 'opus') or a model's full name"). Aliases are the stable
    /// interface here: each always resolves to that tier's current model, so this list doesn't need
    /// updating every model generation the way a hardcoded full model ID would.
    /// </summary>
    private static readonly IReadOnlyList<string> ModelAliases = ["sonnet", "opus", "haiku"];

    public Task<WorkerCapabilities> DiscoverCapabilitiesAsync(string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        var items = new List<WorkerCapabilityItem>();
        var searchDirs = new List<string>();

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            searchDirs.Add(workingDirectory);
        }
        var userClaudeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        if (Directory.Exists(userClaudeDir))
        {
            searchDirs.Add(userClaudeDir);
        }

        foreach (var baseDir in searchDirs)
        {
            var skillsDir = Path.Combine(baseDir, ".claude", "skills");
            if (Directory.Exists(skillsDir))
            {
                foreach (var skillSubDir in Directory.GetDirectories(skillsDir))
                {
                    var skillFile = Path.Combine(skillSubDir, "SKILL.md");
                    var name = Path.GetFileName(skillSubDir);
                    var desc = $"Skill in {name}";
                    if (File.Exists(skillFile))
                    {
                        try
                        {
                            var text = File.ReadAllText(skillFile);
                            var lines = text.Split('\n');
                            foreach (var l in lines)
                            {
                                if (l.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                                {
                                    desc = l["description:".Length..].Trim().Trim('"', '\'');
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                    items.Add(new WorkerCapabilityItem(name, "skill", desc));
                }
            }

            var commandsDir = Path.Combine(baseDir, ".claude", "commands");
            if (Directory.Exists(commandsDir))
            {
                foreach (var file in Directory.GetFiles(commandsDir, "*.md"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    items.Add(new WorkerCapabilityItem($"/{name}", "command", $"Custom command /{name}"));
                }
            }
        }

        items.Add(new WorkerCapabilityItem("/compact", "command", "Summarize and compact session history"));
        items.Add(new WorkerCapabilityItem("/clear", "command", "Clear session context"));

        var uniqueItems = items.GroupBy(i => i.Name).Select(g => g.First()).ToList();
        return Task.FromResult(new WorkerCapabilities("claude", uniqueItems, ModelAliases));
    }

    /// <summary>
    /// Parses one line of <c>claude --output-format stream-json --include-partial-messages</c>'s
    /// newline-delimited JSON (M24 Phase 1's live in-turn streaming). The <c>system</c>/<c>assistant</c>
    /// envelopes below are confirmed against a real, live invocation of the installed CLI (a
    /// same-shape <c>{"type":"assistant","message":{"content":[{"type":"text",...}]}}</c> line came
    /// back even from an unauthenticated run's error response) — those branches are load-bearing.
    /// The <c>stream_event</c>/<c>content_block_delta</c> branch mirrors the publicly documented
    /// Anthropic Messages streaming event shape Claude Code wraps for <c>--include-partial-messages</c>'
    /// token-level deltas, but no authenticated session was available to observe one directly; if the
    /// real shape differs, this simply never matches and contributes no partial deltas — full
    /// per-message text (the confirmed branch above) still arrives once each block completes, so
    /// streaming degrades to coarser granularity rather than silently breaking.
    /// </summary>
    public bool TryParseProgressEvent(string rawLine, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeProp))
            {
                return false;
            }

            return typeProp.GetString() switch
            {
                "system" => TryParseSystemEvent(root, out progressEvent),
                "assistant" => TryParseAssistantEvent(root, out progressEvent),
                "stream_event" => TryParseStreamEvent(root, out progressEvent),
                _ => false,
            };
        }
        catch (JsonException)
        {
            // A line split across a stdout chunk boundary, or a non-JSON line this format never
            // produces -- not a progress event, not an error.
            return false;
        }
    }

    private static bool TryParseSystemEvent(JsonElement root, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (!root.TryGetProperty("subtype", out var subtypeProp))
        {
            return false;
        }

        switch (subtypeProp.GetString())
        {
            case "init":
                progressEvent = new WorkerProgressEvent("status", "Session started");
                return true;
            case "status" when root.TryGetProperty("status", out var statusProp) && statusProp.GetString() is { Length: > 0 } status:
                progressEvent = new WorkerProgressEvent("status", status);
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseAssistantEvent(JsonElement root, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (!root.TryGetProperty("message", out var messageProp) ||
            !messageProp.TryGetProperty("content", out var contentProp) ||
            contentProp.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var block in contentProp.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var blockTypeProp))
            {
                continue;
            }

            switch (blockTypeProp.GetString())
            {
                case "text" when block.TryGetProperty("text", out var textProp) && textProp.GetString() is { Length: > 0 } text:
                    progressEvent = new WorkerProgressEvent("text", text);
                    return true;
                case "tool_use" when block.TryGetProperty("name", out var nameProp) && nameProp.GetString() is { Length: > 0 } toolName:
                    progressEvent = new WorkerProgressEvent("tool", toolName);
                    return true;
            }
        }

        return false;
    }

    private static bool TryParseStreamEvent(JsonElement root, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        if (root.TryGetProperty("event", out var eventProp) &&
            eventProp.TryGetProperty("type", out var eventTypeProp) &&
            eventTypeProp.GetString() == "content_block_delta" &&
            eventProp.TryGetProperty("delta", out var deltaProp) &&
            deltaProp.TryGetProperty("type", out var deltaTypeProp) &&
            deltaTypeProp.GetString() == "text_delta" &&
            deltaProp.TryGetProperty("text", out var deltaTextProp) &&
            deltaTextProp.GetString() is { Length: > 0 } deltaText)
        {
            progressEvent = new WorkerProgressEvent("text", deltaText, IsPartial: true);
            return true;
        }

        return false;
    }
}
