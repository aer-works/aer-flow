using System.Text;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Workers.Dialogue;

namespace Aer.Adapters;

/// <summary>
/// The third <see cref="IWorkerAdapter"/> (M17 Phase 4, #167): resolves a <see cref="WorkerInvocation"/>/
/// <see cref="WorkerContract"/> pair into an invocation of the <c>Aer.Workers.Dialogue</c> executable
/// (M17 Phases 2-3) rather than a vendor CLI — the milestone's Fact 1 confirmed dispatching it needs
/// only a registry entry here, because to Flow a dialogue execution is indistinguishable from any
/// other worker (spec §18.2). Registered under the capability name <c>"dialogue"</c>, generalizing
/// M12's "registry key names who you're talking to, not what you type to reach them" convention.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="WorkerInvocation.PromptTemplate"/> carries the dialogue-worker config file's static
/// path, not instructional text</b> — resolving Phase 4's named open question in favor of "existing
/// per-role fields" over "a required input." <c>Aer.Flow.Artifacts.ArtifactManager.ResolveInputPaths</c>
/// only ever resolves a step's declared <c>Inputs</c> from an ancestor step's declared <c>Outputs</c>
/// — never a static, authoring-time file — so treating the dialogue config as a required input would
/// force every workflow using this worker to add a step whose sole job is "produce this static file,"
/// for content that is exactly as static and per-role as <see cref="WorkerInvocation.Model"/> or
/// <see cref="WorkerInvocation.PermissionScope"/> already are. Reusing <c>PromptTemplate</c> — already
/// documented as "forwarded verbatim" — needs zero Flow or engine change, matching the milestone's
/// first fact directly. The dialogue worker's own "what to do" (seed prompt, per-side preambles, stop
/// condition) already lives inside that file, per <see cref="DialogueWorkerConfig"/>; nothing here
/// re-derives or duplicates it.
/// </para>
/// <para>
/// <b>The dialogue executable is located via a <c>ProjectReference</c>, not a hardcoded path or a
/// PATH lookup</b> — <c>Aer.Adapters.csproj</c> references <c>Aer.Workers.Dialogue.csproj</c> purely
/// so MSBuild copies its build output next to every consumer of this adapter (<c>Aer.Cli</c>,
/// <c>Aer.Ui</c>, and their test hosts), the identical mechanism
/// <c>tests/Aer.Flow.Tests/TestSupport/CrashTestHostLauncher</c> already proves for a different
/// Exe-output <c>ProjectReference</c>. Invoked via <c>dotnet exec &lt;dll&gt;</c> — a
/// framework-dependent invocation, not a self-contained/AOT publish — matching every other piece of
/// this stack's own toolchain assumption that <c>dotnet</c> is already on PATH (CLAUDE.md). Bundling
/// it into <c>aer</c>'s own packed <c>dotnet tool</c> nupkg (M13) falls out of this same reference for
/// free, resolving Phase 2's "how it ships" open question in favor of "riding aer's existing package,"
/// never a separate one.
/// </para>
/// <para>
/// <b>Leaves <see cref="Aer.Flow.Dispatch.CoreDispatchTarget.PromptText"/> unset (issue #292).</b> That
/// field exists to durably capture an ordinary step's resolved prompt for UI/audit display, mirroring
/// what dialogue's own <c>transcript.jsonl</c> already gives every turn's prompt (spec §10.1) — this
/// adapter's worker process already writes that transcript itself, so a second, adapter-level capture
/// here would be a redundant (and differently-shaped) duplicate, not a gap.
/// </para>
/// <para>
/// <b>Only <c>AER_OUTPUT_DIR</c> needs shell-expanded env-var interpolation.</b> The config path is
/// static per-role config (see above), so, unlike <see cref="ClaudeWorkerAdapter"/>/
/// <see cref="GeminiWorkerAdapter"/>, this adapter needs neither stdin redirection (the dialogue
/// executable never reads <c>Console.In</c> — its <c>Program.cs</c> is argument-driven only) nor
/// Windows' newline-collapsing (its two arguments are never multi-line). The shell wrap exists solely
/// to expand <c>$AER_OUTPUT_DIR</c>/<c>%AER_OUTPUT_DIR%</c> into the real, execution-specific
/// directory at spawn time — the same "resolved once per binding, expanded per dispatch" split every
/// other adapter in this file uses. Windows tokens are still never pre-quoted into one string, for the
/// identical reason <see cref="ClaudeWorkerAdapter"/>'s remarks record.
/// </para>
/// </remarks>
public sealed class DialogueWorkerAdapter : IWorkerAdapter
{
    /// <summary>
    /// Resolved via the dialogue worker's own <see cref="DialogueWorkerConfig"/> type rather than a
    /// hardcoded relative path: since this project references <c>Aer.Workers.Dialogue.csproj</c> as a
    /// <c>ProjectReference</c>, MSBuild has already copied its built assembly next to whatever
    /// consumes this adapter, and this is exactly the path it copied it to, in any build
    /// configuration.
    /// </summary>
    private static readonly string DialogueWorkerDllPath = typeof(DialogueWorkerConfig).Assembly.Location;

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(contract);

        var isWindows = OperatingSystem.IsWindows();
        var resolvedConfigPath = ResolveConfigPath(invocation.PromptTemplate, invocation.BindingsFileDirectory);
        var configPath = EscapeUserContent(resolvedConfigPath, isWindows);

        return isWindows
            ? ResolveWindows(configPath, invocation.WorkingDirectory)
            : ResolveUnix(configPath, invocation.WorkingDirectory);
    }

    /// <summary>
    /// M23 Phase 3's fix for the config sidecar's absolute-path portability bug (#272): a rooted
    /// <paramref name="promptTemplate"/> passes through unchanged (the pre-Phase-3 behavior, still
    /// legal), but a relative one — the shape the Template Editor's structured dialogue authoring
    /// (M23 Phase 1) writes by default — resolves against <paramref name="bindingsFileDirectory"/>,
    /// wherever the bindings file this invocation was resolved from currently lives. This is what
    /// makes a bindings.json + sidecar pair portable: copy both files anywhere (a different
    /// directory, a different machine) and this resolution still finds the sidecar, since it never
    /// depends on the absolute path the sidecar happened to live at when it was first authored.
    /// </summary>
    private static string ResolveConfigPath(string promptTemplate, string? bindingsFileDirectory) =>
        Path.IsPathRooted(promptTemplate) || string.IsNullOrEmpty(bindingsFileDirectory)
            ? promptTemplate
            : Path.GetFullPath(Path.Combine(bindingsFileDirectory, promptTemplate));

    private static CoreDispatchTarget ResolveWindows(string configPath, string? workingDirectory)
    {
        List<string> args = ["/c", "dotnet", "exec", DialogueWorkerDllPath, configPath, "%AER_OUTPUT_DIR%"];
        return new CoreDispatchTarget("cmd", args, workingDirectory);
    }

    private static CoreDispatchTarget ResolveUnix(string configPath, string? workingDirectory)
    {
        var commandLine = new StringBuilder("dotnet exec ")
            .Append(Quote(DialogueWorkerDllPath))
            .Append(' ').Append(Quote(configPath))
            .Append(" \"$AER_OUTPUT_DIR\"");

        return new CoreDispatchTarget("sh", ["-c", commandLine.ToString()], workingDirectory);
    }

    /// <summary>
    /// Defuses shell metacharacters in the config-authored path before it is embedded in the
    /// generated command — identical treatment to <see cref="ClaudeWorkerAdapter"/>'s escaping of
    /// authored text, since the shell-wrapping mechanism (and therefore what needs defusing) is the
    /// same regardless of which string is being carried. On Windows only <c>%</c> needs doubling; a
    /// literal quote/backtick/dollar/backslash does not (see <see cref="ClaudeWorkerAdapter"/>'s
    /// remarks for why).
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
