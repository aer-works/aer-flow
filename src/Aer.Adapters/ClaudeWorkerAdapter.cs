using System.Text;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters;

/// <summary>
/// The first real <see cref="IWorkerAdapter"/> (M11 Phase 2, #85): resolves a
/// <see cref="WorkerInvocation"/>/<see cref="WorkerContract"/> pair into a headless <c>claude</c>
/// CLI invocation, honoring the facts closed spike #21 recorded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stdin is always redirected.</b> #21 observed a per-call stall ("Warning: no stdin data
/// received in 3s") without it — aer-core's own process spawn (<c>Command</c>, unix.rs) never sets
/// stdin itself, so a bare <c>claude</c> invocation would inherit whatever stdin the host process
/// has. Every invocation is therefore wrapped in a shell one-liner redirected from the platform's
/// null device, never spawned as a bare <c>claude</c> process.
/// </para>
/// <para>
/// <b>Paths reach the prompt via shell-expanded environment references, not cwd.</b> #21's raw
/// spike script happened to work by relying on the invoking process's cwd, but that finding
/// validates spec §16's actual design, not a shortcut this adapter can take: paths reach workers
/// exclusively via <c>AER_INPUT_&lt;n&gt;</c>/<c>AER_OUTPUT_DIR</c> environment variables (M11
/// Phase 1's <see cref="WorkerInvocation"/> decision of record — the resolved
/// <see cref="CoreDispatchTarget"/> is reused across every dispatch of this worker role, so nothing
/// here can embed a resolved, execution-specific path). The same shell wrapping needed for stdin
/// redirection is reused to interpolate the real per-execution values into the prompt text at spawn
/// time — the same convention the shell-stub test workers already use, and the one M12's
/// <c>agy</c> adapter will need for the same reason.
/// </para>
/// <para>
/// <b>Permission scope</b> defaults to <c>"Write"</c> — the exact <c>--allowedTools</c> value #21
/// confirmed pre-authorizes file writes without disabling every other approval gate — when
/// <see cref="WorkerInvocation.PermissionScope"/> is not set.
/// </para>
/// <para>
/// <b>Windows tokens are never pre-quoted into one string.</b> aer-core's Windows spawn
/// (<c>Command::args</c>) applies Win32 argument quoting/escaping to every
/// <see cref="CoreDispatchTarget.Args"/> element itself. Handing it one already hand-quoted
/// command-line string (as the Unix branch correctly does for <c>sh -c</c>, where <c>execve</c>
/// passes argv through verbatim with no re-quoting) makes Rust escape this adapter's own quotes a
/// second time, corrupting the command before <c>cmd.exe</c> or <c>claude</c> ever see a valid one
/// — confirmed live: <c>claude</c> received no prompt at all, only "What would you like me to
/// write?". Passing each token as its own array element lets that one layer of quoting happen
/// exactly once, correctly, on both a literal quote/backtick/dollar/backslash in a prompt and a
/// bare <c>%AER_OUTPUT_DIR%</c> reference (confirmed live via a capture-enabled repro of both).
/// </para>
/// </remarks>
public sealed class ClaudeWorkerAdapter : IWorkerAdapter
{
    private const string DefaultPermissionScope = "Write";

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(contract);

        var isWindows = OperatingSystem.IsWindows();
        var prompt = BuildPrompt(invocation.PromptTemplate, contract, isWindows);
        var permissionScope = invocation.PermissionScope ?? DefaultPermissionScope;

        return isWindows
            ? ResolveWindows(prompt, permissionScope, invocation.Model)
            : ResolveUnix(prompt, permissionScope, invocation.Model);
    }

    private static CoreDispatchTarget ResolveWindows(string prompt, string permissionScope, string? model)
    {
        List<string> args =
        [
            "/c", "claude", "-p", prompt,
            "--allowedTools", EscapeUserContent(permissionScope, isWindows: true),
            "--output-format", "text",
        ];

        if (model is not null)
        {
            args.Add("--model");
            args.Add(EscapeUserContent(model, isWindows: true));
        }

        // Stdin is always redirected (see this type's remarks) -- as separate tokens so cmd's own
        // tail parser, not Rust's argument quoting, is what interprets the redirection operator.
        args.Add("<");
        args.Add("NUL");

        return new CoreDispatchTarget("cmd", args);
    }

    private static CoreDispatchTarget ResolveUnix(string prompt, string permissionScope, string? model)
    {
        var commandLine = new StringBuilder("claude -p ")
            .Append(Quote(prompt))
            .Append(" --allowedTools ").Append(Quote(EscapeUserContent(permissionScope, isWindows: false)))
            .Append(" --output-format text");

        if (model is not null)
        {
            commandLine.Append(" --model ").Append(Quote(EscapeUserContent(model, isWindows: false)));
        }

        return new CoreDispatchTarget("sh", ["-c", $"{commandLine} < /dev/null"]);
    }

    /// <summary>
    /// The human-authored <paramref name="promptTemplate"/> (escaped so it cannot itself inject
    /// shell syntax) followed by generated, unescaped <c>AER_INPUT_&lt;n&gt;</c>/<c>AER_OUTPUT_DIR</c>
    /// references — live shell expansion, on purpose, is exactly what turns those into real
    /// per-execution absolute paths at spawn time (see this type's remarks).
    /// </summary>
    private static string BuildPrompt(string promptTemplate, WorkerContract contract, bool isWindows)
    {
        var prompt = new StringBuilder(EscapeUserContent(promptTemplate, isWindows));

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

        var built = prompt.ToString();

        // cmd.exe's `/c` parser ends the current statement at an embedded newline even inside a
        // quoted argument (unlike `sh -c`, whose quoting correctly spans lines) -- so a multi-line
        // prompt would otherwise silently truncate the invocation, dropping --allowedTools/
        // --output-format/--model and the output-path instructions that follow it. Collapsing to
        // single-line here is Windows-only; Unix keeps the newlines for readability.
        return isWindows ? built.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ') : built;
    }

    private static string EnvironmentReference(string name, bool isWindows) => isWindows ? $"%{name}%" : $"${name}";

    /// <summary>
    /// Defuses shell metacharacters in config-authored text (a prompt template, model name, or
    /// permission scope) before it is embedded in the generated command, so it can never alter the
    /// command's structure or expand as a variable reference itself — unlike the
    /// <c>AER_INPUT_&lt;n&gt;</c>/<c>AER_OUTPUT_DIR</c> references this adapter generates afterward,
    /// which are deliberately left unescaped (see this type's remarks).
    /// <para>
    /// On Windows only <c>%</c> needs doubling: confirmed live that an unescaped <c>%PATH%</c> (or
    /// any other real variable name) in a prompt gets expanded by <c>cmd.exe</c>'s own pass over its
    /// <c>/c</c> tail — independent of, and unaffected by, whether the surrounding text is one of
    /// Rust's quoted argv tokens — leaking the host's actual environment variable value into the
    /// prompt. A literal quote/backtick/dollar/backslash does not need escaping here: passing each
    /// token as its own array element (see <see cref="ResolveWindows"/>) lets Rust's own Win32
    /// argument quoting handle those correctly and exactly once; escaping them here too, as this
    /// method used to for <c>"</c>, made <c>claude</c> receive no prompt at all (confirmed live).
    /// </para>
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
