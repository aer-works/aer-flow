using System.Diagnostics;
using System.Text;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters;

/// <summary>
/// Direct shell-less <see cref="IWorkerAdapter"/> (M20 Phase 4): resolves a
/// <see cref="WorkerInvocation"/>/<see cref="WorkerContract"/> pair into a direct <c>agy</c>
/// (Google Gemini CLI) invocation without shell wrappers. Bypasses cmd.exe and sh, eliminating quoting and
/// command injection risks. Stdin redirection to null is handled natively by the process host.
/// <para>
/// <b>M21 Phase 1's <see cref="IPermissionGrantTranslator"/>:</b> unlike Claude's per-tool
/// <c>--allowedTools</c>, <c>agy</c>'s <c>--mode</c> is a single coarse setting with only three
/// confirmed values (<c>default</c>, <c>accept-edits</c>, <c>plan</c>) — there is no confirmed
/// non-interactive mode that auto-approves shell commands or network/web-fetch tool calls without
/// prompting (headless <c>-p</c> execution can't answer an interactive prompt, so an unsupported
/// grant would hang rather than fail cleanly). Requesting <see cref="PermissionGrant.RunShellCommands"/>
/// or <see cref="PermissionGrant.NetworkAccess"/> is therefore always refused here rather than
/// approximated to the nearest mode — see <see cref="TryTranslatePermissionGrant"/>.
/// </para>
/// <para>
/// <b>Why no <c>--disallowedTools</c> mirror (unlike Claude, #331):</b> a shell-<em>withheld</em>
/// grant maps to a plain <c>--mode</c> here, and <c>agy</c> has no deny-list flag — but it does not
/// need one. Headless <c>agy</c> <em>auto-denies</em> any tool needing a permission it cannot prompt
/// for (verified against the live CLI across <c>default</c>/<c>plan</c>/<c>accept-edits</c>; see
/// <c>docs/runbooks/live-claude-smoke.md</c>'s J6 section). Its default is fail-closed — the opposite
/// of Claude Code's headless auto-<em>approve</em>, which is exactly what made #331 possible there.
/// </para>
/// </summary>
public sealed class GeminiWorkerAdapter : IWorkerAdapter, IPermissionGrantTranslator
{
    private const string DefaultPermissionScope = "accept-edits";

    public bool TryTranslatePermissionGrant(PermissionGrant grant, out string? resolvedValue, out string? gapReason)
    {
        ArgumentNullException.ThrowIfNull(grant);

        if (grant.RunShellCommands)
        {
            resolvedValue = null;
            gapReason = "agy has no confirmed non-interactive --mode value that auto-approves shell " +
                "commands without prompting, so requesting shell access cannot be honored by the " +
                "structured builder. Use the Advanced raw permission-scope field with a --mode value " +
                "verified against your installed agy CLI instead.";
            return false;
        }

        if (grant.NetworkAccess)
        {
            resolvedValue = null;
            gapReason = "agy has no confirmed non-interactive --mode value that auto-approves " +
                "network/web-fetch tool calls without prompting, so requesting network access cannot " +
                "be honored by the structured builder. Use the Advanced raw permission-scope field instead.";
            return false;
        }

        resolvedValue = grant.WriteFiles ? "accept-edits" : grant.ReadFiles ? "plan" : "default";
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
            "--mode", permissionScope,
            "--add-dir", artifactsRoot,
        ];

        if (invocation.SessionId is not null && invocation.ResumeSession)
        {
            args.Add("--conversation");
            args.Add(invocation.SessionId);
        }

        if (invocation.LogFilePath is not null)
        {
            args.Add("--log-file");
            args.Add(invocation.LogFilePath);
        }

        if (invocation.Model is not null)
        {
            args.Add("--model");
            args.Add(invocation.Model);
        }

        return new CoreDispatchTarget("agy", [.. args], invocation.WorkingDirectory, PromptText: prompt);
    }

    /// <summary>
    /// A structured <see cref="WorkerInvocation.PermissionGrant"/> always wins over the raw
    /// <see cref="WorkerInvocation.PermissionScope"/> string (<see cref="PermissionGrant"/>'s own
    /// docs record this precedence).
    /// </summary>
    /// <exception cref="PermissionGrantUnsupportedException">
    /// <paramref name="invocation"/> carries a <see cref="WorkerInvocation.PermissionGrant"/>
    /// <see cref="TryTranslatePermissionGrant"/> refuses (<see cref="PermissionGrant.RunShellCommands"/>
    /// or <see cref="PermissionGrant.NetworkAccess"/>).
    /// </exception>
    private string ResolvePermissionScope(WorkerInvocation invocation)
    {
        if (invocation.PermissionGrant is { } grant)
        {
            if (!TryTranslatePermissionGrant(grant, out var resolved, out var gapReason))
            {
                throw new PermissionGrantUnsupportedException("gemini", gapReason!);
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
            prompt.Append("\n\nInputs, in the order listed, are available at these absolute paths:\n");
            for (var i = 0; i < contract.RequiredInputs.Count; i++)
            {
                prompt.Append($"- {contract.RequiredInputs[i]}: {EnvironmentReference($"AER_INPUT_{i}", isWindows)}\n");
            }
        }

        if (contract.ProducedOutputs.Count > 0)
        {
            prompt.Append("\nWrite each of the following outputs to the exact absolute path shown, creating parent directories as needed:\n");
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

    private static readonly TimeSpan DiscoverySubcommandTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Shells out to <c>agy models</c>, <c>agy agent</c>, and <c>agy plugin list</c> — the real
    /// subcommands the installed CLI exposes (confirmed against <c>agy --help</c>'s "Available
    /// subcommands" list) — rather than reporting a hardcoded, driftable model/agent list. Best
    /// effort: a subcommand that errors, times out, or isn't installed contributes nothing rather
    /// than fabricated data.
    /// </summary>
    public async Task<WorkerCapabilities> DiscoverCapabilitiesAsync(string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        var modelsOutput = RunAgySubcommandAsync(["models"], workingDirectory, cancellationToken);
        var agentsOutput = RunAgySubcommandAsync(["agent"], workingDirectory, cancellationToken);
        var pluginsOutput = RunAgySubcommandAsync(["plugin", "list"], workingDirectory, cancellationToken);
        await Task.WhenAll(modelsOutput, agentsOutput, pluginsOutput).ConfigureAwait(false);

        var items = new List<WorkerCapabilityItem>
        {
            new("/compact", "command", "Summarize and compact session history"),
            new("default", "mode", "Default non-interactive mode"),
            new("accept-edits", "mode", "Auto-accept file editing permissions"),
            new("plan", "mode", "Read-only planning mode"),
        };
        items.AddRange(ParseAgentLines(agentsOutput.Result));
        items.AddRange(ParsePluginLines(pluginsOutput.Result));

        return new WorkerCapabilities("gemini", items, ParseModelLines(modelsOutput.Result));
    }

    private static IReadOnlyList<string> ParseModelLines(string? stdout) =>
        NonEmptyTrimmedLines(stdout).ToList();

    private static IEnumerable<WorkerCapabilityItem> ParseAgentLines(string? stdout) =>
        NonEmptyTrimmedLines(stdout)
            .Where(line => !line.EndsWith(':')) // skip the "Available agents:" header
            .Select(name => new WorkerCapabilityItem(name, "agent", $"agy agent: {name}"));

    private static IEnumerable<WorkerCapabilityItem> ParsePluginLines(string? stdout) =>
        NonEmptyTrimmedLines(stdout)
            .Where(line => !line.StartsWith("No imported plugins", StringComparison.OrdinalIgnoreCase))
            .Select(name => new WorkerCapabilityItem(name, "plugin", $"agy plugin: {name}"));

    private static IEnumerable<string> NonEmptyTrimmedLines(string? stdout) =>
        string.IsNullOrWhiteSpace(stdout)
            ? []
            : stdout.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0);

    private static async Task<string?> RunAgySubcommandAsync(IReadOnlyList<string> args, string? workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo("agy")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }
            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DiscoverySubcommandTimeout);

            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                return await stdoutTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort: process may have already exited between the cancel and the kill.
                }
                return null;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // agy isn't installed/on PATH, or couldn't be started — discovery degrades to nothing
            // for this subcommand rather than fabricating a result.
            return null;
        }
    }
}
