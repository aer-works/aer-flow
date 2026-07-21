using System.Text;
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

        List<string> args =
        [
            "-p", prompt,
            "--allowedTools", permissionScope,
        ];

        if (invocation.StreamJson)
        {
            args.Add("--output-format");
            args.Add("stream-json");
            args.Add("--include-partial-messages");
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

        return new CoreDispatchTarget("claude", [.. args], invocation.WorkingDirectory);
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

    public WorkerCapabilities DiscoverCapabilities(string? workingDirectory = null)
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

        var models = new List<string> { "claude-3-5-sonnet", "claude-3-5-haiku", "claude-3-opus" };
        var uniqueItems = items.GroupBy(i => i.Name).Select(g => g.First()).ToList();
        return new WorkerCapabilities("claude", uniqueItems, models);
    }
}
