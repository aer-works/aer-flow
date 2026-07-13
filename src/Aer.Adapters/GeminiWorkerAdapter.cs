using System.Text;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters;

/// <summary>
/// The second real <see cref="IWorkerAdapter"/> (M12 Phase 1, #95): resolves a
/// <see cref="WorkerInvocation"/>/<see cref="WorkerContract"/> pair into a headless <c>agy</c>
/// (antigravity, Google Gemini's CLI) invocation, honoring the facts closed spike #21 recorded.
/// Exists to prove M11 Phase 1's claim that a second vendor changes no engine or protocol code —
/// every accommodation below lives entirely in this adapter.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>agy</c> ignores the invoking process's cwd</b> — #21 found it operates from its own fixed
/// scratch workspace regardless of what directory spawned it, so (unlike <see cref="ClaudeWorkerAdapter"/>,
/// which can lean on <c>aer-core</c>'s cwd handling) every directory it touches needs an explicit
/// <c>--add-dir</c> grant, and every path in the prompt must be absolute. Both needs are met the
/// same way: this invocation is shell-wrapped exactly like Claude's, so the shell expands
/// <c>$AER_ARTIFACTS_ROOT</c> (<see cref="Aer.Flow.Artifacts.ArtifactManager.BuildEnvironment"/>)
/// into a real absolute path at spawn time, before <c>agy</c> ever sees the command. One grant
/// covers every input this adapter might need to read <i>and</i> the output directory it writes to,
/// because both are sibling <c>execution_{id}</c> directories under that same root — no per-input
/// <c>dirname</c> derivation (and no separate Windows answer for it) is needed.
/// </para>
/// <para>
/// <b>Permission scope</b> defaults to <c>"accept-edits"</c> — the <c>--mode</c> value #21 confirmed
/// pre-authorizes file edits (v1.1.1+) — when <see cref="WorkerInvocation.PermissionScope"/> is not
/// set. Coarser than Claude's per-tool <c>--allowedTools</c> (no shared vocabulary between vendors),
/// but <see cref="WorkerInvocation.PermissionScope"/> is deliberately an opaque, adapter-interpreted
/// string for exactly this reason.
/// </para>
/// <para>
/// <b>Exit 0 with no output is <c>agy</c>'s observed failure mode</b> (#21: a misconfigured run
/// returned a clarifying question and wrote nothing, still exiting 0) — this adapter does nothing
/// special for it. <c>ContractValidator</c> already reads a missing declared output as a retryable
/// contract failure (§8 → §10), the same mechanism that already covers Claude.
/// </para>
/// <para>
/// <b>Stdin is redirected</b> the same way Claude's is, even though #21 recorded no stall for
/// <c>agy</c>: the shell wrapper already exists here for <c>--add-dir</c>/prompt-path expansion, so
/// redirecting is free insurance against the same class of stall Claude hit, not a proven necessity.
/// </para>
/// <para>
/// <b>Windows tokens are never pre-quoted into one string</b> — identical fix and identical reason
/// as <see cref="ClaudeWorkerAdapter"/>'s remarks: aer-core's Windows spawn (<c>Command::args</c>)
/// quotes/escapes every <see cref="CoreDispatchTarget.Args"/> element itself, so one pre-quoted
/// string here would be escaped a second time and corrupted before <c>cmd.exe</c> or <c>agy</c> ever
/// saw a valid command, exactly as confirmed live for <c>claude</c>.
/// </para>
/// </remarks>
public sealed class GeminiWorkerAdapter : IWorkerAdapter
{
    private const string DefaultPermissionScope = "accept-edits";

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(contract);

        var isWindows = OperatingSystem.IsWindows();
        var prompt = BuildPrompt(invocation.PromptTemplate, contract, isWindows);
        var permissionScope = invocation.PermissionScope ?? DefaultPermissionScope;
        var artifactsRoot = EnvironmentReference("AER_ARTIFACTS_ROOT", isWindows);

        return isWindows
            ? ResolveWindows(prompt, permissionScope, artifactsRoot, invocation.Model)
            : ResolveUnix(prompt, permissionScope, artifactsRoot, invocation.Model);
    }

    private static CoreDispatchTarget ResolveWindows(
        string prompt, string permissionScope, string artifactsRoot, string? model)
    {
        List<string> args =
        [
            "/c", "agy", "-p", prompt,
            "--mode", EscapeUserContent(permissionScope, isWindows: true),
            "--add-dir", artifactsRoot,
        ];

        if (model is not null)
        {
            args.Add("--model");
            args.Add(EscapeUserContent(model, isWindows: true));
        }

        args.Add("<");
        args.Add("NUL");

        return new CoreDispatchTarget("cmd", args);
    }

    private static CoreDispatchTarget ResolveUnix(
        string prompt, string permissionScope, string artifactsRoot, string? model)
    {
        var commandLine = new StringBuilder("agy -p ")
            .Append(Quote(prompt))
            .Append(" --mode ").Append(Quote(EscapeUserContent(permissionScope, isWindows: false)))
            .Append(" --add-dir ").Append(Quote(artifactsRoot));

        if (model is not null)
        {
            commandLine.Append(" --model ").Append(Quote(EscapeUserContent(model, isWindows: false)));
        }

        return new CoreDispatchTarget("sh", ["-c", $"{commandLine} < /dev/null"]);
    }

    /// <summary>
    /// The human-authored <paramref name="promptTemplate"/> (escaped so it cannot itself inject
    /// shell syntax) followed by generated, unescaped <c>AER_INPUT_&lt;n&gt;</c>/<c>AER_OUTPUT_DIR</c>
    /// references — live shell expansion into real absolute paths at spawn time, which #21 found
    /// <c>agy</c> requires (it never infers a path from cwd the way Claude tolerated).
    /// </summary>
    private static string BuildPrompt(string promptTemplate, WorkerContract contract, bool isWindows)
    {
        var prompt = new StringBuilder(EscapeUserContent(promptTemplate, isWindows));

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

        var built = prompt.ToString();

        // cmd.exe's `/c` parser ends the current statement at an embedded newline even inside a
        // quoted argument (unlike `sh -c`, whose quoting correctly spans lines) -- so a multi-line
        // prompt would otherwise silently truncate the invocation, dropping --mode/--add-dir/
        // --model and the output-path instructions that follow it. Collapsing to single-line here
        // is Windows-only; Unix keeps the newlines for readability.
        return isWindows ? built.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ') : built;
    }

    private static string EnvironmentReference(string name, bool isWindows) => isWindows ? $"%{name}%" : $"${name}";

    /// <summary>
    /// Defuses shell metacharacters in config-authored text (a prompt template, model name, or
    /// permission scope) before it is embedded in the generated command — identical to
    /// <see cref="ClaudeWorkerAdapter"/>'s escaping, since the shell-wrapping mechanism (and
    /// therefore what needs defusing) is the same regardless of vendor. On Windows only <c>%</c>
    /// needs doubling; a literal quote/backtick/dollar/backslash does not (see
    /// <see cref="ClaudeWorkerAdapter"/>'s remarks for why).
    /// </summary>
    private static string EscapeUserContent(string value, bool isWindows) => isWindows
        ? value.Replace("%", "%%")
        : value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$");

    /// <summary>
    /// Wraps already-escaped content in double quotes for embedding as one shell argument in the
    /// Unix <c>sh -c</c> command line, which <c>execve</c> passes through verbatim with no further
    /// re-quoting. Windows never builds a command line this way (see <see cref="ResolveWindows"/>).
    /// </summary>
    private static string Quote(string value) => $"\"{value}\"";
}
