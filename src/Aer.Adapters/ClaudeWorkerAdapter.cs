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

        var commandLine = new StringBuilder("claude -p ")
            .Append(Quote(prompt))
            .Append(" --allowedTools ").Append(Quote(EscapeUserContent(permissionScope, isWindows)))
            .Append(" --output-format text");

        if (invocation.Model is not null)
        {
            commandLine.Append(" --model ").Append(Quote(EscapeUserContent(invocation.Model, isWindows)));
        }

        return isWindows
            ? new CoreDispatchTarget("cmd", ["/c", $"{commandLine} < NUL"])
            : new CoreDispatchTarget("sh", ["-c", $"{commandLine} < /dev/null"]);
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

        return prompt.ToString();
    }

    private static string EnvironmentReference(string name, bool isWindows) => isWindows ? $"%{name}%" : $"${name}";

    /// <summary>
    /// Defuses shell metacharacters in config-authored text (a prompt template, model name, or
    /// permission scope) before it is embedded in the generated command line, so it can never alter
    /// the command's structure or expand as a variable reference itself — unlike the
    /// <c>AER_INPUT_&lt;n&gt;</c>/<c>AER_OUTPUT_DIR</c> references this adapter generates afterward,
    /// which are deliberately left unescaped (see this type's remarks).
    /// </summary>
    private static string EscapeUserContent(string value, bool isWindows) => isWindows
        ? value.Replace("%", "%%").Replace("\"", "\"\"")
        : value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$");

    /// <summary>
    /// Wraps already-escaped content in double quotes for embedding as one shell argument.
    /// <paramref name="value"/> must already be escaped (via <see cref="EscapeUserContent"/>) for
    /// any config-authored portion it contains — this only adds the enclosing quotes, so it never
    /// touches (and cannot break) a deliberately unescaped <c>AER_OUTPUT_DIR</c>-style reference.
    /// Quoting itself is identical on both platforms; only <see cref="EscapeUserContent"/>'s
    /// escaping rules differ.
    /// </summary>
    private static string Quote(string value) => $"\"{value}\"";
}
