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
/// <b>M21 Phase 1's <see cref="IPermissionGrantTranslator"/>, corrected in #331:</b> Claude Code's
/// <c>--allowedTools</c> is tool-name-based (<c>Read</c>, <c>Edit</c>, <c>Write</c>,
/// <c>Bash</c>/<c>Bash(pattern)</c>, <c>WebFetch</c>, <c>WebSearch</c>) but only <em>pre-approves</em>
/// those tools so they do not prompt — it is not a sandbox and does not remove a withheld tool from
/// the model's reach. A grant therefore resolves to <em>both</em> lists: <c>--allowedTools</c> for what
/// it permits (this direction never refuses), and <c>--disallowedTools</c> for what it withholds
/// (<see cref="BuildDisallowedTools"/>), which is what actually enforces the denial — decision 0004's
/// "fail closed".
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

        // #533 constraints 1-2: hooks load only from the process's own cwd `.claude/`, with no
        // parent-directory fallback, and `--add-dir` (above) loads no configuration on claude --
        // measured, gate.add-dir-loads-no-config. So AER cannot rely on cwd-based discovery for
        // either the mandatory PreToolUse hook (0029) or MCP config; it passes both explicitly, at
        // a path AER owns rather than the room's own directory (`WorkingDirectory` may be a repo the
        // operator did not ask AER to write into). EnsureLaunchConfigFiles populates the real
        // PreToolUse hook (#543) -- see its own doc comment for why the settings file is always
        // rewritten rather than written once.
        var (settingsPath, mcpConfigPath) = EnsureLaunchConfigFiles();
        args.Add("--settings");
        args.Add(settingsPath);
        args.Add("--mcp-config");
        args.Add(mcpConfigPath);

        // #331: --allowedTools only *pre-approves* tools so they don't prompt; it is not a sandbox,
        // and omitting a tool leaves it in the model's reach (a shell-denied session ran `hostname`
        // and returned the real value). A withheld category must be *actively* denied. Verified
        // against the live CLI in a clean spawn env: the same invocation refuses `hostname` with
        // --disallowedTools Bash and runs it without. --disallowedTools takes precedence over
        // --allowedTools, so the two compose — allow what's granted, deny what's withheld (0004).
        var disallowed = BuildDisallowedTools(invocation.PermissionGrant);
        if (disallowed.Length > 0)
        {
            args.Add("--disallowedTools");
            args.Add(disallowed);
        }

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

        // Do not reintroduce `--bare` here, under any flag. It is not a latency optimisation this
        // product can take, for two independently sufficient reasons, both measured:
        //
        //   1. It skips "keychain reads" (its own --help says so) -- which is exactly where
        //      subscription OAuth login lives. A --bare dispatch against a real subscription login
        //      fails immediately with "Not logged in", even with valid, unexpired credentials, and
        //      AER works against subscriptions rather than API keys (Architecture Rule 4).
        //   2. It suppresses hooks and MCP servers EVEN WHEN PASSED EXPLICITLY via --settings
        //      (#521): `claude --bare --settings <PreToolUse hook>` does not fire the hook, while
        //      the same invocation without --bare does. 0029 makes that hook mandatory on every
        //      worker AER spawns, so --bare is the flag AER passed that removed the gate. It is
        //      not the only route to the same failure -- `--safe-mode` (a flag AER never passes,
        //      so nothing to neutralize) and CLAUDE_CODE_SIMPLE=1, documented as equivalent to
        //      --bare including its keychain-skip, disable hooks identically. Unlike --safe-mode,
        //      CLAUDE_CODE_SIMPLE is an *inherited* env var (#543: neutralized below, in
        //      CoreDispatchTarget.Environment -- AerTask inherits the full parent environment by
        //      default, so an operator's shell setting it would otherwise reach claude unopposed).
        //
        // Reason 2 is the load-bearing one: an auth failure is loud, and a missing hook is silent
        // for one of two independent reasons -- not loaded at all, or loaded but unable to execute
        // (#530 measures the second; the first traces to the discovery constraint, not to #530).
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

        return new CoreDispatchTarget(
            "claude", [.. args], invocation.WorkingDirectory, PromptText: prompt,
            Environment:
            [
                (MaxSubagentSpawnDepthVariable, "1"),
                (DeniedToolsVariable, disallowed),
                (SimpleModeVariable, ""),
            ]);
    }

    /// <summary>
    /// Overrides an inherited <c>CLAUDE_CODE_SIMPLE=1</c> (see the comment above on why that
    /// disables hooks the same way <c>--bare</c> does) so an operator's shell cannot silently reach
    /// the spawned <c>claude</c> process and remove the gate. <b>Best-effort, not a measured
    /// sentinel</b>: the vendor docs state what <c>1</c> triggers but never what a blank value does
    /// -- an empty string cannot equal the one documented trigger, which defeats both a strict
    /// string-equality check and a truthiness check, but no live run against the installed CLI has
    /// confirmed the vendor's own parsing treats it as "off" rather than "on, malformed." Filed as
    /// the honest scope of what this override actually proves, per 0029/#532's own discipline of
    /// stating what a check does not prove rather than only what it does.
    /// </summary>
    public const string SimpleModeVariable = "CLAUDE_CODE_SIMPLE";

    /// <summary>
    /// The environment variable carrying this invocation's denied-tool list to the <c>PreToolUse</c>
    /// hook's own process (#543) — the same comma-joined names <see cref="BuildDisallowedTools"/>
    /// emits for <c>--disallowedTools</c>. Set even when empty; <c>Aer.Cli</c>'s <c>hook-check</c>
    /// treats an empty/missing value as "nothing withheld" and always allows. A hook process
    /// inherits the spawning process's environment (confirmed in
    /// <c>.vendor-survey/corpus/claude__hooks.md</c>: "A hook process inherits the parent
    /// environment"), which is what makes this reach hook-check at all -- the settings file itself
    /// is one static, shared file across every spawn (see <see cref="EnsureLaunchConfigFiles"/>), so
    /// per-invocation data has to travel this way rather than through the file's content.
    /// <see cref="Aer.Adapters"/> cannot reference <c>Aer.Cli</c> (the CLI depends on the adapters,
    /// never the reverse), so this name is a plain string contract mirrored on
    /// <c>HookCheckCommand.DeniedToolsEnvironmentVariable</c> — both sides assert the literal value
    /// in their own test suite, and the two must agree.
    /// </summary>
    public const string DeniedToolsVariable = "AER_HOOK_DENIED_TOOLS";

    /// <summary>
    /// The environment variable name Claude Code reads for its subagent fan-out depth cap.
    /// </summary>
    /// <remarks>
    /// #533 constraint 3, measured rather than trusted from the vendor's own docs: the vendor
    /// documents this variable's default as <c>1</c> (no nesting), but two independent runs of
    /// <c>fanout.nesting-allowed-by-default</c> (<c>tools/vendor-verify/verify.py</c>) counted
    /// actual <c>SubagentStart</c> spawns and found the unset default produces <b>2</b> -- a
    /// subagent CAN spawn its own subagent with nothing configured. Set explicitly to <c>1</c> here
    /// so AER's own default matches what the vendor documents rather than what it measurably does.
    /// <para>
    /// #533 constraint 4 is why this is the only lever: a subagent inherits its parent's permission
    /// mode and cannot be given a stricter one, so the gate for a fan-out tree cannot be re-applied
    /// per level -- it has to hold for whatever depth this variable allows. Raising it later (e.g.
    /// for a legitimate multi-worker room, M27) is a deliberate widening, not a default to assume.
    /// </para>
    /// </remarks>
    public const string MaxSubagentSpawnDepthVariable = "CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH";

    /// <summary>
    /// Ensures the two files <see cref="AerPaths.WorkerLaunchConfig"/> needs exist. Called on every
    /// <see cref="Resolve"/> because there is no single daemon-lifecycle hook covering every entry
    /// point that resolves a claude invocation (the CLI's `aer run`/`aer decide`/etc. spawn a fresh
    /// process per command, with no daemon involved at all).
    /// </summary>
    /// <remarks>
    /// <b>The settings file is always rewritten with canonical content (#543), reversing #533's
    /// "never overwrite existing content."</b> That was correct while the file held only inert `{}`
    /// with nothing to lose; now it carries the mandatory `PreToolUse` hook (0029), and "never
    /// overwrite" would leave a pre-#543 `{}` -- or any other stale content -- permanently installed,
    /// silently disabling the gate for good on any machine that ran an earlier build even once. The
    /// file is entirely AER-owned (no operator content can live here, per
    /// <see cref="AerPaths.WorkerLaunchConfig"/>'s own doc comment), so there is nothing that
    /// overwriting could destroy. The MCP config file is untouched by #543 and keeps the old
    /// once-only semantics.
    /// </remarks>
    private static (string SettingsPath, string McpConfigPath) EnsureLaunchConfigFiles()
    {
        Directory.CreateDirectory(AerPaths.WorkerLaunchConfig);

        var settingsPath = Path.Combine(AerPaths.WorkerLaunchConfig, "claude-settings.json");
        WriteFileAtomically(settingsPath, BuildSettingsJson());

        // The standard empty MCP config shape -- declares no servers, so this adds nothing beyond
        // what claude would otherwise discover on its own.
        var mcpConfigPath = Path.Combine(AerPaths.WorkerLaunchConfig, "claude-mcp.json");
        EnsureFileExists(mcpConfigPath, "{\"mcpServers\":{}}");

        return (settingsPath, mcpConfigPath);
    }

    /// <summary>
    /// The `--settings` content #543 ships: one `PreToolUse` hook, matching every tool
    /// (<c>"matcher": "*"</c>), spawned in exec form (`args` set) so Claude Code invokes it directly
    /// with no shell -- no quoting concerns, matching this adapter's own "direct shell-less" design
    /// (see the type's own doc comment).
    /// </summary>
    /// <remarks>
    /// <b>Invoked as <c>dotnet &lt;Aer.Cli.dll path&gt;</c>, not the native apphost.</b> An earlier
    /// version of this method named <c>Aer.Cli.exe</c>/<c>Aer.Cli</c> directly, resolved via
    /// <see cref="AppContext.BaseDirectory"/>. That works for a raw build output (confirmed for both
    /// `Aer.Cli.exe` standalone and `Aer.Daemon.exe`, which references `Aer.Cli` through
    /// `Aer.Ui.Core` and so carries a copy in its own output directory) but is wrong for `aer`'s
    /// other real, exercised deployment shape: <c>Aer.Cli.csproj</c> sets <c>PackAsTool</c>, and a
    /// packed global tool's <c>DotnetToolSettings.xml</c> runs <c>Aer.Cli.dll</c> via the <c>dotnet</c>
    /// muxer with **no apphost at all** (confirmed by packing the tool and inspecting the nupkg) --
    /// naming the apphost there would silently write a dangling command into every worker's hook,
    /// exactly the fail-open-and-silent failure #530 measured. `dotnet &lt;dll&gt;` works in both
    /// shapes: the managed dll and its `.runtimeconfig.json`/`.deps.json` sit next to
    /// <see cref="AppContext.BaseDirectory"/> either way (a raw build's own output directory, or a
    /// global tool's own store directory -- it is, after all, the same dll this process is currently
    /// running from), and `dotnet` itself is a hard prerequisite for this whole product already
    /// (`CLAUDE.md`: ".NET 10 SDK is required"). The explicit <see cref="File.Exists"/> guard below
    /// turns any future deployment shape this reasoning missed into a loud failure at dispatch time
    /// rather than a silent one at hook-invocation time.
    /// </remarks>
    private static string BuildSettingsJson()
    {
        var hookAssemblyPath = Path.Combine(AppContext.BaseDirectory, "Aer.Cli.dll");
        if (!File.Exists(hookAssemblyPath))
        {
            throw new InvalidOperationException(
                $"Cannot write the mandatory PreToolUse hook (decision 0029): '{hookAssemblyPath}' " +
                "does not exist. Every deployment of aer/Aer.Daemon must carry Aer.Cli.dll alongside " +
                "its own binary -- a hook naming a path that does not exist fails open and silently " +
                "(#530), so this fails loudly here instead, before any worker is dispatched.");
        }

        var settings = new
        {
            hooks = new
            {
                PreToolUse = new[]
                {
                    new
                    {
                        matcher = "*",
                        hooks = new[]
                        {
                            new
                            {
                                type = "command",
                                command = "dotnet",
                                args = new[] { hookAssemblyPath, "hook-check" },
                            },
                        },
                    },
                },
            },
        };

        return JsonSerializer.Serialize(settings);
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> via a temp file plus
    /// <see cref="File.Move(string, string, bool)"/>, whose overwrite is a same-volume rename and
    /// therefore atomic on both Windows and POSIX -- a reader never observes a torn write, which a
    /// direct <see cref="File.WriteAllText(string, string)"/> onto the final path does not guarantee
    /// when two callers race to rewrite it (see <see cref="BuildSettingsJson"/>'s remarks on why
    /// that race is otherwise benign; this is what keeps it benign at the byte level too).
    /// </summary>
    /// <remarks>
    /// The rename itself can still collide: #533's own comment already names two chat sessions
    /// starting their first turn from the same daemon process as a genuine, expected race, and
    /// unlike #533's existence-only write this one repeats on every <see cref="Resolve"/>, not once
    /// per fresh <c>~/.aer</c>. Measured under this PR's own parallel test run: a concurrent
    /// <see cref="File.Move(string, string, bool)"/> onto the same destination throws
    /// <see cref="UnauthorizedAccessException"/> on Windows (a transient sharing violation, not a
    /// real permissions problem) while another thread's move or read briefly holds the destination
    /// open. Every racing writer in one process produces byte-identical content (a deterministic
    /// function of <see cref="AppContext.BaseDirectory"/>, constant for the process's lifetime), so
    /// retrying is correct rather than papering over a real disagreement: whichever attempt
    /// eventually wins, the file ends up holding the one content every writer wanted anyway.
    /// </remarks>
    private static void WriteFileAtomically(string path, string content)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, content);
            try
            {
                File.Move(tempPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && (ex is IOException or UnauthorizedAccessException))
            {
                File.Delete(tempPath);
                Thread.Sleep(TimeSpan.FromMilliseconds(10 * attempt));
            }
            catch
            {
                File.Delete(tempPath);
                throw;
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> only if it does not already
    /// exist, without silently swallowing a genuine write failure.
    /// </summary>
    /// <remarks>
    /// Two turns can genuinely race here -- two chat sessions both starting their first-ever turn
    /// against a fresh <c>~/.aer</c>, both hitting this before either file exists, from the SAME
    /// daemon process, not just two separate `aer run` processes. That is a real TOCTOU: `File.Exists`
    /// then `File.WriteAllText` opens write-exclusive, so the loser of the race gets an
    /// <see cref="IOException"/>, not a second identical write as an earlier version of this comment
    /// claimed. The content this writes is fixed and identical regardless of who wins, so the correct
    /// response to that specific exception is "someone else just created it" -- verified by re-checking
    /// existence, not assumed. Any other failure (permissions, disk full, a genuinely corrupt partial
    /// write) still throws, per CLAUDE.md's rule against silently swallowing exceptions.
    /// </remarks>
    private static void EnsureFileExists(string path, string content)
    {
        if (File.Exists(path))
        {
            return;
        }

        try
        {
            File.WriteAllText(path, content);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another spawn's write won the race and the file is now there -- not our problem to fix.
        }
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

    /// <summary>
    /// The deny-list mirror of <see cref="TryTranslatePermissionGrant"/> (#331): every category the
    /// grant <em>withholds</em> maps to the Claude Code tool(s) that would otherwise reach it, emitted
    /// as <c>--disallowedTools</c>. This is what makes a withheld checkbox true — <c>--allowedTools</c>
    /// only auto-approves, it does not remove an unlisted tool from the model's reach. <c>NotebookEdit</c>
    /// is denied alongside <c>Edit</c>/<c>Write</c> because it is also a file-write path.
    /// <para>
    /// <b>Boundary:</b> denial here is by <em>enumeration</em>, not default-deny. It covers the tools a
    /// grant category names; it does not cover tools outside the grant's four categories (<c>Task</c>,
    /// MCP server tools, or a tool a future CLI adds). Genuine fail-closed across the whole tool surface
    /// is the broader change decision 0004 tracks (the project ceiling); this closes the reported,
    /// category-mapped holes. Returns <see cref="string.Empty"/> when there is no structured grant (the
    /// raw <see cref="WorkerInvocation.PermissionScope"/> escape hatch carries no category to deny) or
    /// when nothing is withheld.
    /// </para>
    /// <para>
    /// <b>WHAT THIS DOES NOT GUARANTEE — read before relying on it (#529, measured 2026-07-25).</b>
    /// This method bounds <em>which tool runs</em>. It does <em>not</em> bound what the worker can
    /// achieve, because <b>the model substitutes another tool and reaches the same goal</b>. Measured
    /// with the exact string this method emits for a withheld-write grant,
    /// <c>--disallowedTools Edit,Write,NotebookEdit</c>: the file was created anyway, by <c>Bash</c>.
    /// Because the four categories are independent, <c>Bash</c> stays available whenever
    /// <see cref="PermissionGrant.RunShellCommands"/> is granted — and <c>Bash</c> alone defeats
    /// withheld <em>writes</em>, withheld <em>reads</em> (<c>cat</c>) and withheld <em>network</em>
    /// (<c>curl</c>). The caveat in the previous paragraph is about tools outside the four categories;
    /// this hole is <em>inside</em> them, and write-withheld-plus-shell-granted is a common grant
    /// shape rather than an exotic one.
    /// </para>
    /// <para>
    /// Treat the result as <b>pre-approval and routing, never as a security boundary</b>. The
    /// mechanisms measured to stop an <em>operation</em> gate on the operation rather than the tool
    /// (a <c>PreToolUse</c> hook exiting 2, an explicit <c>ask</c> rule, a hook returning
    /// <c>permissionDecision: "ask"</c>, and <c>requiresUserInteraction</c> on MCP tools), which is
    /// exactly why substitution does not defeat them. See <c>docs/vendor-doc-audit.md</c>; re-runnable
    /// via <c>pixi run vendor-verify -- --only gate.allowedtools-is-preapproval-not-ceiling</c>.
    /// </para>
    /// </summary>
    private static string BuildDisallowedTools(PermissionGrant? grant)
    {
        if (grant is null)
        {
            return string.Empty;
        }

        List<string> denied = [];
        if (!grant.ReadFiles)
        {
            denied.Add("Read");
        }

        if (!grant.WriteFiles)
        {
            denied.Add("Edit");
            denied.Add("Write");
            denied.Add("NotebookEdit");
        }

        if (!grant.RunShellCommands)
        {
            denied.Add("Bash");
        }

        if (!grant.NetworkAccess)
        {
            denied.Add("WebFetch");
            denied.Add("WebSearch");
        }

        return string.Join(',', denied);
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
