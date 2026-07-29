using System.Text.Json;
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
        var gatedConfigPath = GateParticipants(
            resolvedConfigPath, invocation.PermissionGrant, invocation.WorkingDirectory);
        var configPath = EscapeUserContent(gatedConfigPath, isWindows);

        return isWindows
            ? ResolveWindows(configPath, invocation.WorkingDirectory)
            : ResolveUnix(configPath, invocation.WorkingDirectory);
    }

    /// <summary>
    /// Vendor CLI names AER ships a <see cref="VendorGate"/> for, as they appear in a participant's
    /// <c>Command</c> — the check that stops a config reaching one of them ungated by labelling its
    /// <c>Vendor</c> as something AER does not recognise (#703).
    /// </summary>
    internal static readonly string[] GatedVendorCommands = ["claude", "agy"];

    /// <summary>
    /// Rewrites every participant AER can gate into its gated invocation, returning the path the
    /// worker should read (#703).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dialogue worker spawns vendor CLIs itself, through a bare <c>ProcessStartInfo</c> that
    /// never carried <c>--settings</c>, a denied-tools channel, or anything else 0029's mandatory
    /// hook needs. It cannot acquire them on its own: <c>Aer.Workers.Dialogue</c> declares no project
    /// references and <c>Aer.Adapters</c> references it, so vendor knowledge cannot travel in that
    /// direction — and Architecture Rule 2 says it should not. So the gating happens HERE, on the
    /// authored config, and the worker reads a config whose participants are already gated.
    /// </para>
    /// <para>
    /// Returns the original path untouched when nothing needed rewriting, so a config of stub
    /// participants (every test in this repo, and any local script) still runs with no AER-owned copy
    /// written for it.
    /// </para>
    /// </remarks>
    private static string GateParticipants(string configPath, PermissionGrant? grant, string? workspace)
    {
        if (!File.Exists(configPath))
        {
            // Not this method's error to report. The worker opens the same path moments later and
            // says so far better, naming the file and what it was resolved from.
            return configPath;
        }

        var config = DialogueWorkerConfigParser.Parse(File.ReadAllText(configPath));
        var gated = config.Participants.Select(participant => Gate(participant, grant, workspace)).ToList();
        if (gated.SequenceEqual(config.Participants))
        {
            return configPath;
        }

        var resolved = config with { Participants = gated };
        var json = JsonSerializer.Serialize(resolved);

        // Named from the content it holds, so two rooms gating the same config share one file and a
        // changed authored config never reads a stale gated one. Written through the same atomic
        // writer as the other launch configs, into the same AER-owned root -- the operator's own
        // directory is not somewhere AER writes (#533).
        var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];
        var path = Path.Combine(AerPaths.WorkerLaunchConfig, "dialogue-gated", $"{name}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicLaunchConfigWriter.Write(path, json);
        return path;
    }

    /// <summary>
    /// One participant's gated invocation, or a refusal when it names a vendor CLI AER cannot reach.
    /// </summary>
    private static DialogueParticipant Gate(DialogueParticipant participant, PermissionGrant? grant, string? workspace)
    {
        if (VendorGate.For(participant.Vendor, grant, workspace) is { } gate)
        {
            // The invocation is AER's to build, not the author's. A declared vendor that could still
            // run an arbitrary command would let AER install claude's gate onto a process that never
            // honours it — reporting an enforcement it does not have, which is worse than a known gap.
            var preset = DialogueParticipantPresets.For(
                participant.Vendor, participant.Role, participant.Preamble, participant.Model);

            if (!string.Equals(participant.Command, preset.Command, StringComparison.OrdinalIgnoreCase))
            {
                throw new DialogueWorkerConfigException(
                    $"Participant '{participant.Role}' declares Vendor '{participant.Vendor}' but runs "
                    + $"'{participant.Command}'. A declared vendor means AER builds the invocation, so the "
                    + $"only command it can run is '{preset.Command}' — otherwise AER would apply "
                    + $"{participant.Vendor}'s permission gate to a process that ignores it.\n\n"
                    + "Either drop Command so AER builds it, or, if this is a stub or a local script, "
                    + "give it a Vendor of its own rather than one of "
                    + $"{string.Join(", ", DialogueParticipantPresets.KnownVendors)} — an unrecognised "
                    + "vendor runs exactly as authored.");
            }

            // Args come from the preset too: an authored `--bare` or `--mode yolo` would sit alongside
            // the gate and undo it, and this worker is not the place that knows which flags do that.
            return preset with
            {
                Args = [.. preset.Args, .. gate.Args],
                Environment = gate.Environment,
            };
        }

        // An unrecognised Vendor is ordinarily a stub or a local script and is left alone. It stops
        // being ordinary when a real vendor CLI is named anywhere in the invocation — Command OR
        // Args, because `Command: "cmd", Args: ["/c", "claude", …]` reaches claude with no gate just
        // as cheaply as relabelling the vendor does, and scanning only Command missed it.
        if (NamesAGatedVendorCli(participant))
        {
            throw new DialogueWorkerConfigException(
                $"Participant '{participant.Role}' invokes a vendor CLI — '{participant.Command} "
                + $"{string.Join(' ', participant.Args)}' — but declares Vendor '{participant.Vendor}', "
                + "which AER has no permission gate for, so it would run with none of the tool "
                + "restrictions the room grants.\n\n"
                + $"Set Vendor to one of: {string.Join(", ", DialogueParticipantPresets.KnownVendors)}. "
                + "AER then builds the invocation, and the gate arrives with it — you do not need to "
                + "write the vendor's flags yourself.");
        }

        return participant;
    }

    /// <summary>
    /// Whether this participant names a vendor CLI AER ships a gate for, anywhere in its command line.
    /// </summary>
    /// <remarks>
    /// <b>THIS IS A MISTAKE-CATCHER, NOT A BOUNDARY, and the difference is the whole point.</b> An
    /// author who wants an ungated vendor CLI can always have one — a wrapper script named anything,
    /// a shell built from a variable, a symlink, a copy of the binary under another name. This
    /// refuses the shapes someone reaches for by accident or by one obvious indirection; it does not
    /// and cannot make the invariant hold against an author who is trying to defeat it.
    /// <para>
    /// What DOES hold structurally is narrower and worth not confusing with this: a participant
    /// declaring a known <c>Vendor</c> gets an invocation AER built, and every path through
    /// <see cref="CoreDispatcher"/> carries the gate. Named false negatives, in the spirit of
    /// <c>VendorSpawnGateTests</c> naming its own: a renamed or copied binary, a wrapper script, a
    /// command assembled at runtime by the shell, and any vendor CLI AER ships no gate for.
    /// </para>
    /// </remarks>
    private static bool NamesAGatedVendorCli(DialogueParticipant participant)
    {
        foreach (var candidate in CommandPositions(participant))
        {
            var name = Path.GetFileNameWithoutExtension(candidate.Trim('"', '\''));
            if (GatedVendorCommands.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Shell switches whose NEXT argument is a command line rather than an ordinary value.
    /// </summary>
    /// <remarks>
    /// POSIX short flags cluster, so the literal list cannot be the whole test — <c>sh -lc</c> and
    /// <c>bash -ec</c> are what people actually type, and a review found both walking straight past
    /// the first version of this. <see cref="IsShellCommandSwitch"/> is the real predicate;
    /// this array is only the non-clustering spellings.
    /// </remarks>
    private static readonly string[] ShellCommandSwitches = ["-c", "/c", "/k", "-Command", "-EncodedCommand"];

    /// <summary>Programs whose clustered short flags can carry a command string.</summary>
    private static readonly string[] PosixShells = ["sh", "bash", "zsh", "dash", "ksh", "ash", "busybox"];

    /// <summary>
    /// Whether <paramref name="arg"/> is a shell switch whose next argument is a command line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Beyond the literal spellings, any POSIX short-flag cluster ENDING in <c>c</c> — <c>-lc</c>,
    /// <c>-ec</c>, <c>-lic</c> — takes a command string, because <c>c</c> is the flag that consumes
    /// it and clustering puts it last.
    /// </para>
    /// <para>
    /// <b>The cluster rule applies only when the program is a POSIX shell</b>, and that restriction
    /// is not caution — the unrestricted version was written first and turned this test suite's own
    /// control red. <c>powershell -File s.ps1 -abc "claude is a model"</c> matched <c>-abc</c> as a
    /// cluster, scanned the following argument, found <c>claude</c> in its first position and refused
    /// a participant that only MENTIONS a vendor. A false positive here is not a safe direction to
    /// err in: it refuses a legitimate config with an error about permission gates that has nothing
    /// to do with what the author wrote.
    /// </para>
    /// </remarks>
    private static bool IsShellCommandSwitch(string command, string arg)
    {
        if (ShellCommandSwitches.Contains(arg, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var program = Path.GetFileNameWithoutExtension(command.Trim('"', '\''));
        return PosixShells.Contains(program, StringComparer.OrdinalIgnoreCase)
            && arg.Length > 2 && arg[0] == '-' && arg[1] != '-'
            && arg.EndsWith('c') && arg[1..^1].All(char.IsLetter);
    }

    /// <summary>
    /// Shell words that stand in FRONT of the command they run, so the executable is the next word
    /// rather than the first one.
    /// </summary>
    /// <remarks>
    /// <c>exec agy -p …</c> reaches agy exactly as <c>agy -p …</c> does, and scanning only a
    /// segment's first token stops at <c>exec</c>. Variable assignments (<c>FOO=1 claude …</c>) are
    /// the same shape and are handled by pattern rather than by list.
    /// </remarks>
    private static readonly string[] TransparentCommandPrefixes =
        ["exec", "env", "command", "nohup", "time", "sudo", "doas", "builtin", "eval"];

    /// <summary>Whether a token is a prefix word rather than the executable itself.</summary>
    private static bool IsTransparentPrefix(string token) =>
        TransparentCommandPrefixes.Contains(token, StringComparer.OrdinalIgnoreCase)
        || (token.IndexOf('=') > 0 && char.IsLetter(token[0])
            && token[..token.IndexOf('=')].All(c => char.IsLetterOrDigit(c) || c == '_'));

    /// <summary>Shell operators that end one command and begin another within a single string.</summary>
    private static readonly string[] CommandSeparators = ["&&", "||", ";", "|", "&", "\n", "\r"];

    /// <summary>
    /// The tokens in an invocation that could name an executable — deliberately not "every word".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Splitting every argument on whitespace would refuse a participant whose PROMPT merely mentions
    /// a vendor by name, which is both common and harmless. So a whole argument is a candidate, and
    /// the executable position of each COMMAND inside an argument is additionally considered when the
    /// preceding argument was a shell's command switch — the <c>sh -c "agy -p …"</c> shape.
    /// </para>
    /// <para>
    /// "Each command", not "the first token", because a review found three accidental shapes walking
    /// past the first version: <c>cd /repo &amp;&amp; claude …</c> (the vendor is not in the first
    /// segment), <c>exec agy …</c> (not the first token of its segment), and <c>sh -lc</c> (the
    /// switch spelling was matched literally). None of those is adversarial — they are what someone
    /// writes by habit, and the suite's own fixture was one character from the first.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> CommandPositions(DialogueParticipant participant)
    {
        yield return participant.Command;

        for (var i = 0; i < participant.Args.Count; i++)
        {
            var arg = participant.Args[i];
            yield return arg;

            if (i == 0 || !IsShellCommandSwitch(participant.Command, participant.Args[i - 1]))
            {
                continue;
            }

            foreach (var position in ExecutablePositionsIn(DecodeIfEncoded(participant.Args[i - 1], arg)))
            {
                yield return position;
            }
        }
    }

    /// <summary>
    /// The argument after <c>-EncodedCommand</c> is base64 UTF-16, so scanning it as text can never
    /// match a vendor name — it was listed as a handled switch while structurally doing nothing.
    /// </summary>
    /// <remarks>
    /// A malformed blob is returned unchanged rather than thrown on: this is a mistake-catcher, and
    /// refusing to resolve a config because its base64 does not decode would be a worse failure than
    /// the one being caught.
    /// </remarks>
    private static string DecodeIfEncoded(string precedingSwitch, string arg)
    {
        if (!precedingSwitch.Equals("-EncodedCommand", StringComparison.OrdinalIgnoreCase))
        {
            return arg;
        }

        try
        {
            return System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(arg));
        }
        catch (FormatException)
        {
            return arg;
        }
        catch (ArgumentException)
        {
            return arg;
        }
    }

    /// <summary>
    /// Every position within a shell command STRING that names an executable: the first non-prefix
    /// word of each <c>&amp;&amp;</c>/<c>||</c>/<c>;</c>/<c>|</c>-separated segment.
    /// </summary>
    private static IEnumerable<string> ExecutablePositionsIn(string commandLine)
    {
        foreach (var segment in commandLine.Split(CommandSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var words = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                if (IsTransparentPrefix(word))
                {
                    continue;
                }

                // The first word that is not a prefix IS the executable; anything after it is an
                // argument, and scanning those is what would refuse a prompt mentioning a vendor.
                yield return word;
                break;
            }
        }
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
