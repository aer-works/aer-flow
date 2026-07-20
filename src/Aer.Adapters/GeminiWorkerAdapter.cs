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

        if (invocation.Model is not null)
        {
            args.Add("--model");
            args.Add(invocation.Model);
        }

        return new CoreDispatchTarget("agy", [.. args], invocation.WorkingDirectory);
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
}
