using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
/// <c>--allowedTools</c>, <c>agy</c>'s permission flags consist of <c>--mode</c> (coarse settings:
/// <c>default</c>, <c>accept-edits</c>, <c>plan</c>) and <c>--dangerously-skip-permissions</c> (which
/// auto-approves all tool permission requests without prompting, including shell commands and network access).
/// Because <c>--dangerously-skip-permissions</c> is all-or-nothing, requesting only one of
/// <see cref="PermissionGrant.RunShellCommands"/> or <see cref="PermissionGrant.NetworkAccess"/> without
/// the other is refused to prevent over-granting unrequested capabilities. Requesting both
/// <see cref="PermissionGrant.RunShellCommands"/> and <see cref="PermissionGrant.NetworkAccess"/> together
/// matches <c>--dangerously-skip-permissions</c> exactly and is translated to that flag — see
/// <see cref="TryTranslatePermissionGrant"/>.
/// </para>
/// <para>
/// <b>Why no <c>--disallowedTools</c> mirror (unlike Claude, #331):</b> a shell-<em>withheld</em>
/// grant maps to a plain <c>--mode</c> here, and <c>agy</c> has no deny-list flag — but it does not
/// need one. Headless <c>agy</c> <em>auto-denies</em> a <b>shell command</b> it cannot prompt for
/// (<c>agy.fails-closed-headless</c>, measured with <c>node --version</c> across
/// <c>default</c>/<c>plan</c>/<c>accept-edits</c>; see <c>docs/runbooks/live-claude-smoke.md</c>'s J6
/// section) — the opposite of Claude Code's headless auto-<em>approve</em>, which is exactly what
/// made #331 possible there.
/// </para>
/// <para>
/// <b>That is one tool, and it does not generalise — #670.</b> This paragraph used to claim agy
/// auto-denies <em>any</em> tool needing a permission it cannot prompt for. Measured against the live
/// CLI: under <c>--mode plan</c>, agy <b>writes a file into an <c>--add-dir</c> path without a prompt
/// or a refusal</b>, and reports it as succeeded. So the fail-closed default covers the shell arm that
/// was measured and not the write arm that was assumed.
/// </para>
/// <para>
/// <b>That argument does not reach the <c>--dangerously-skip-permissions</c> branch, and #596 exists
/// because it reads as though it does.</b> Note which modes the paragraph above was verified across:
/// <c>default</c>/<c>plan</c>/<c>accept-edits</c> — every mode <em>except</em> the one that turns
/// auto-denial off. Under that flag <c>agy</c> stops refusing what it cannot prompt for, so a grant
/// of shell + network with <see cref="PermissionGrant.WriteFiles"/> withheld would hand the worker
/// the writes the operator declined, purely from the flag.
/// </para>
/// <para>
/// What actually withholds them there is the <c>PreToolUse</c> hook (#554), not the vendor's own
/// default: <see cref="BuildDeniedTools"/> derives denied tools from <b>all four boolean</b> grant categories
/// — reads and writes included, not only the two the flag encodes — and every invocation carries that
/// list in <see cref="DeniedToolsVariable"/>, this branch included. A hook deny blocking a call
/// <em>while running under <c>--dangerously-skip-permissions</c></em> is measured, not inferred from
/// the <c>--mode</c> case: <c>agy.hook-deny-honoured</c> spawns with that exact flag. So the flag
/// over-grants and the hook takes it back, which is a materially different safety story from
/// "the vendor is fail-closed" and is why it is written down separately.
/// </para>
/// <para>
/// <b>The consequence for anyone editing this class:</b> under that branch the tool-name lists
/// (<c>ReadTools</c>, <c>WriteTools</c>, <c>ShellTools</c>, <c>NetworkTools</c>) are the entire
/// enforcement boundary — a write-capable <c>agy</c> tool missing from <c>WriteTools</c> is simply
/// not denied. Whether those lists are complete against agy's real tool surface is unmeasured — #623,
/// which is the security property here rather than a tidiness question. Removing a category from
/// <see cref="BuildDeniedTools"/> as "redundant with the flag" is the specific edit that would make
/// #596's over-grant real.
/// </para>
/// <para>
/// <b>And the hook only takes it back while it runs.</b> On this vendor an absent or unparseable hook
/// response reads as an <em>allow</em> — see the fail-open note on <see cref="BuildHooksJson"/> below.
/// For writes there is no backstop under <c>--mode</c> either (#670), so a hook that cannot start is
/// a fully ungated worker on every branch of this method. Scoping shell patterns is a second gap in the same
/// direction, and it is why a grant narrowed by <see cref="PermissionGrant.ShellCommandPatterns"/> is
/// now refused rather than resolved to an unscoped shell (#624). Refused because AER's hook decides by
/// tool name alone, <em>not</em> because agy could not express it — the hook payload carries the tool's
/// arguments, so #659 is a route rather than a dead end.
/// </para>
/// </summary>
public sealed class GeminiWorkerAdapter : IWorkerAdapter, IPermissionGrantTranslator
{
    private const string DefaultPermissionScope = "accept-edits";

    /// <summary>
    /// The environment variable carrying this invocation's denied-tool list to the
    /// <c>PreToolUse</c> hook's own process (#554) — the agy-side counterpart of
    /// <see cref="ClaudeWorkerAdapter.DeniedToolsVariable"/>, and deliberately the same variable
    /// name: a worker is only ever one vendor, so the values differ while the channel need not.
    /// Mirrored as a plain string on <c>AgyHookCheckCommand.DeniedToolsEnvironmentVariable</c>
    /// because <c>Aer.Adapters</c> cannot reference <c>Aer.Cli</c>; both sides assert the literal
    /// value in their own suite.
    /// </summary>
    /// <remarks>
    /// That an agy hook subprocess inherits this at all is <b>measured, not assumed</b>:
    /// <c>agy.hook-env-inherited</c> (a sentinel) confirms it. agy's own hook documentation says
    /// nothing about environment inheritance where claude's states it explicitly, so reusing
    /// claude's answer without measuring would have been the population-scope mistake gate `claim-scope` names.
    /// </remarks>
    /// <summary>
    /// The vendor tag prefixing <see cref="DeniedToolsVariable"/>'s value (#600). Deliberately not
    /// shared with claude's: the variable name is the same because a worker is only ever one vendor,
    /// but the tag is what says which one, so the two tags must differ or it says nothing.
    /// </summary>
    public const string DeniedToolsVendorTag = "agy";

    public const string DeniedToolsVariable = ClaudeWorkerAdapter.DeniedToolsVariable;

    /// <summary>
    /// The name of the workspace directory AER owns and points every agy worker at, holding the
    /// <c>.agents/hooks.json</c> carrying decision 0029's mandatory <c>PreToolUse</c> gate.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AerPaths.WorkerLaunchConfig"/>'s root rather than sharing it: agy
    /// discovers hooks only from a directory handed to <c>--add-dir</c>
    /// (<c>agy.hooks-load-from-add-dir-not-only-cwd</c>), and <c>--add-dir</c> also grants the
    /// worker <em>file access</em> to whatever it names. Pointing it at the launch-config root would
    /// hand every worker read/write access to AER's other launch files; a dedicated leaf directory
    /// keeps that blast radius to the hook file itself.
    /// <para>
    /// The worker can still write to that file, which sounds worse than it is:
    /// <c>agy.hooks-json-cached-at-startup</c> (a sentinel) measures that agy reads the file once at
    /// startup, so a worker cannot disable its own gate mid-run by deleting or rewriting it. Because
    /// <see cref="EnsureAgyWorkspace"/> rewrites the file on every resolve, a tampered file cannot
    /// survive into the next spawn either. What remains is untidiness, not a live bypass.
    /// </para>
    /// </remarks>
    public const string AgyWorkspaceDirectoryName = "agy-workspace";

    /// <summary>
    /// agy's own tool names for each permission category — an entirely separate vocabulary from
    /// claude's, not a renaming of it, which is why this cannot share
    /// <c>ClaudeWorkerAdapter.BuildDisallowedTools</c>.
    /// </summary>
    /// <remarks>
    /// Taken from the tool list in <c>.vendor-survey/corpus/agy__hooks.md</c>. Two entries exist
    /// because a narrower mapping leaks the category it withholds: <c>grep_search</c> returns file
    /// <em>contents</em>, so withholding only <c>view_file</c> leaves reads reachable; and
    /// <c>manage_task</c> sends stdin to and kills background shell processes, so withholding only
    /// <c>run_command</c> leaves shell control reachable. <c>list_dir</c> and <c>find_by_name</c>
    /// disclose directory structure rather than contents and are withheld with reads on the same
    /// reasoning 0004's "fail closed" applies elsewhere.
    /// </remarks>
    private static readonly IReadOnlyList<string> ReadTools =
        ["view_file", "list_dir", "find_by_name", "grep_search"];

    /// <remarks>
    /// <c>generate_image</c> is here because the corpus describes it as "Create or edit images" with
    /// an <c>ImageName</c> and <c>ImagePaths</c> — a file creation and modification path, not a
    /// rendering-only one.
    /// </remarks>
    private static readonly IReadOnlyList<string> WriteTools =
        ["write_to_file", "replace_file_content", "multi_replace_file_content", "generate_image"];

    /// <remarks>
    /// <para>
    /// The subagent trio is withheld with the shell because it is agy's closest analogue to claude's
    /// <c>Task</c>, and because of a bypass an independent reviewer found in the first draft:
    /// <c>define_subagent</c> takes <c>enable_write_tools</c> as an argument and
    /// <c>invoke_subagent</c> takes an optional <c>Workspace</c>. A write-withheld worker could
    /// therefore define a subagent with write tools enabled and invoke it — possibly under a
    /// different workspace root than the one this hook was loaded from.
    /// </para>
    /// <para>
    /// <b>Whether a subagent's own tool calls re-enter this hook is unmeasured on agy</b>, so this
    /// withholds the spawn rather than relying on the gate reaching the child. Decision 0029 requires
    /// exactly that posture — "never assume a subagent is more constrained than the session that
    /// spawned it" — and agy exposes no depth-cap equivalent to
    /// <see cref="ClaudeWorkerAdapter.MaxSubagentSpawnDepthVariable"/>, so withholding is the only
    /// lever available here. Tracked in #601.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyList<string> ShellTools =
        ["run_command", "manage_task", "invoke_subagent", "define_subagent", "manage_subagents"];

    /// <remarks>
    /// <c>browser_*</c> is a prefix entry (see <c>AgyHookCheckCommand</c>'s prefix support). The
    /// corpus's matcher section offers <c>"browser_.*"</c> as an example — "Match any tool starting
    /// with <c>browser_</c>" — while its Supported Tools list enumerates none of them, so the exact
    /// names cannot be written down. A browser tool reaches the network, and the corpus contradicting
    /// itself is not a reason to withhold nothing.
    /// </remarks>
    private static readonly IReadOnlyList<string> NetworkTools =
        ["search_web", "read_url_content", "browser_*"];

    /// <summary>
    /// The agy tool names this invocation's grant withholds, comma-joined for
    /// <see cref="DeniedToolsVariable"/>. Empty when nothing is withheld, which
    /// <c>AgyHookCheckCommand</c> reads as "allow everything" — a known-empty grant, distinct from
    /// the failure paths it denies on.
    /// </summary>
    internal static string BuildDeniedTools(PermissionGrant? grant)
    {
        if (grant is null)
        {
            return string.Empty;
        }

        List<string> denied = [];
        if (!grant.ReadFiles)
        {
            denied.AddRange(ReadTools);
        }

        if (!grant.WriteFiles)
        {
            denied.AddRange(WriteTools);
        }

        if (!grant.RunShellCommands)
        {
            denied.AddRange(ShellTools);
        }

        if (!grant.NetworkAccess)
        {
            denied.AddRange(NetworkTools);
        }

        return string.Join(',', denied);
    }

    public bool TryTranslatePermissionGrant(PermissionGrant grant, out string? resolvedValue, out string? gapReason)
    {
        ArgumentNullException.ThrowIfNull(grant);

        // #624, checked before the skip-permissions arm below, which is the one that would otherwise
        // grant every command. Refusing rather than approximating is what IPermissionGrantTranslator
        // requires: granting more than requested is as much a bug as granting less.
        //
        // Not implemented, rather than impossible — the distinction matters because the first wording
        // here claimed the second, in text an operator reads. agy's PreToolUse payload carries
        // `toolCall.args` (agy__hooks.md, whose worked example is run_command with a CommandLine), and
        // a hook deny is measured to hold under --dangerously-skip-permissions (agy.hook-deny-honoured,
        // and see this class's own docs above). An argument-inspecting hook could therefore express
        // this; AER's reads only toolCall.name and says so. #659 carries the route and its real cost:
        // `git:*` is claude's Bash(...) grammar, agy's is command(prefix|regex) with per-token anchored
        // regex, and no mapping between the two exists yet.
        if (grant.RunShellCommands && grant.ShellCommandPatterns is { Count: > 0 })
        {
            resolvedValue = null;
            gapReason = "AER cannot yet scope an agy shell grant to ShellCommandPatterns. agy's only " +
                "auto-approving shell flag is --dangerously-skip-permissions, which approves every " +
                "command, and AER's PreToolUse hook decides by tool name alone — so the patterns would " +
                "be dropped and the worker would receive an unscoped shell in answer to a request to " +
                "narrow it. Withhold the shell, or clear the patterns and accept an unscoped one " +
                "deliberately. The Advanced raw permission-scope field is not a way round this: on agy " +
                "it sets --mode, which cannot express a scoped shell either. Tracked as #659.";
            return false;
        }

        if (grant.RunShellCommands && grant.NetworkAccess)
        {
            resolvedValue = "--dangerously-skip-permissions";
            gapReason = null;
            return true;
        }

        if (grant.RunShellCommands)
        {
            resolvedValue = null;
            gapReason = "agy only supports auto-approving shell command execution via " +
                "--dangerously-skip-permissions, which also grants network access. Granting unrequested " +
                "network access would over-grant permissions. Use the Advanced raw permission-scope field instead.";
            return false;
        }

        if (grant.NetworkAccess)
        {
            resolvedValue = null;
            gapReason = "agy only supports auto-approving network access via " +
                "--dangerously-skip-permissions, which also grants shell command execution. Granting " +
                "unrequested shell execution would over-grant permissions. Use the Advanced raw permission-scope field instead.";
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
        var agyWorkspace = EnsureAgyWorkspace();

        List<string> args = ["-p", prompt];

        if (permissionScope == "--dangerously-skip-permissions")
        {
            args.Add("--dangerously-skip-permissions");
        }
        else
        {
            args.Add("--mode");
            args.Add(permissionScope);
        }

        args.Add("--add-dir");
        args.Add(artifactsRoot);

        // #554: decision 0029's mandatory PreToolUse gate. agy discovers hooks ONLY from a directory
        // named by --add-dir -- measured by `agy.hooks-load-from-add-dir-not-only-cwd` in the
        // arrangement AER actually ships (hook directory != cwd), where the cwd arm loaded
        // NOTHING. So this flag is what
        // loads the gate -- not a convenience. Unconditional, matching #543's claude side: the hook
        // ships on every worker, not only on workers whose flows declare a gate, because a gate that
        // is only sometimes installed cannot be relied upon by anything.
        args.Add("--add-dir");
        args.Add(agyWorkspace);

        // #491: bind the room's own directory explicitly. `agy -p` **ignores the process working
        // directory** — measured in #472 and recorded in docs/vendor-capabilities.md: launched from
        // this repo, which is listed in the CLI's own `trustedWorkspaces`, the emitted command still
        // carried `"Cwd":"C:\\Users\\...\\.gemini\\antigravity-cli"`. From an untrusted directory it
        // used the CLI's scratch dir and, unable to find a file sitting in the launch directory,
        // began a recursive search of the entire home folder. Workspace trust does not change it.
        //
        // So passing `invocation.WorkingDirectory` to CoreDispatchTarget below is necessary and NOT
        // sufficient — that sets the process cwd, which this vendor disregards. Without this the
        // worker cannot see the project at all, and the failure is silent: it answers confidently
        // about a directory that is not yours. `--add-dir` is repeatable on `agy`, so this composes
        // with the artifacts root above rather than replacing it.
        if (!string.IsNullOrWhiteSpace(invocation.WorkingDirectory))
        {
            args.Add("--add-dir");
            args.Add(invocation.WorkingDirectory);
        }

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

        if (invocation.Effort is not null)
        {
            args.Add("--effort");
            args.Add(invocation.Effort);
        }

        if (invocation.Timeout is { } timeout)
        {
            args.Add("--print-timeout");
            args.Add(FormatPrintTimeout(timeout));
        }

        return new CoreDispatchTarget(
            "agy", [.. args], invocation.WorkingDirectory, PromptText: prompt,
            Environment:
            [
                // Read by `aer agy-hook-check` inside the hook subprocess. Always set, even when
                // empty, so the value is AER's rather than whatever the operator's environment
                // happened to carry. It does NOT currently make "nothing withheld" distinguishable
                // from "the list never arrived" -- the command collapses absent and empty to the
                // same allow. See #600.
                (DeniedToolsVariable, $"{DeniedToolsVendorTag}:{BuildDeniedTools(invocation.PermissionGrant)}"),
            ]);
    }

    /// <summary>
    /// Creates the AER-owned agy workspace and rewrites its <c>.agents/hooks.json</c> with canonical
    /// content, returning the directory to hand to <c>--add-dir</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Left holding canonical content on every resolve, never merely created-if-absent</b>, for the
    /// reason #543 gives on the claude side: a stale file left by an earlier build would silently
    /// disable the gate for good on any machine that ran that build once. It also means a worker that
    /// tampered with the file cannot carry that into the next spawn — #667 skips the write when the
    /// file already matches, which does not weaken that, because a tampered file differs and is
    /// therefore still rewritten. The directory is entirely AER-owned, so there is no operator content
    /// for the rewrite to destroy.
    /// </para>
    /// <para>
    /// <b>Never the operator's own <c>~/.gemini/config/</c></b>, which is agy's other documented
    /// hooks location. Writing there would put AER's configuration inside the user's own vendor
    /// config — the boundary CLAUDE.md's Credential Isolation rule draws, and the same reason
    /// <c>agy.permissions-are-global-only</c> is recorded as a limitation rather than used as a
    /// mechanism.
    /// </para>
    /// </remarks>
    private static string EnsureAgyWorkspace()
    {
        var workspace = Path.Combine(AerPaths.WorkerLaunchConfig, AgyWorkspaceDirectoryName);
        Directory.CreateDirectory(Path.Combine(workspace, ".agents"));
        AtomicLaunchConfigWriter.Write(Path.Combine(workspace, ".agents", "hooks.json"), BuildHooksJson());
        return workspace;
    }

    /// <summary>
    /// The <c>.agents/hooks.json</c> content #554 ships: one <c>PreToolUse</c> handler matching
    /// every tool, invoking <c>aer agy-hook-check</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three shape details that are each a measured or documented constraint rather than a style
    /// choice. <b>Hooks are keyed by an arbitrary name at the root</b> — unlike claude's settings
    /// file, which nests them under a <c>hooks</c> key. <b>The matcher is a regex over agy's own
    /// tool names</b>, so <c>"*"</c> here means every tool, and a claude tool name would match
    /// nothing. <b>There is no exec form</b>: agy documents only a single <c>command</c> string
    /// (<c>.vendor-survey/corpus/agy__hooks.md</c>), where claude's handler accepts an <c>args</c>
    /// array that bypasses shell parsing entirely. That last one is why the path is quoted and
    /// forward-slashed below.
    /// </para>
    /// <para>
    /// <b>Backslashes are normalised to forward slashes</b> because the command string is
    /// shell-parsed and a Windows path's <c>\U</c>, <c>\t</c> and friends are escape sequences to a
    /// shell — the same reason <c>tools/vendor-verify/verify.py</c> normalises every hook path it
    /// writes. Forward slashes were confirmed working on Windows by
    /// <c>agy.hook-env-inherited</c>, which spawns its hook this way.
    /// </para>
    /// <para>
    /// Invoked as <c>dotnet &lt;Aer.Cli.dll&gt;</c> rather than a native apphost, for the deployment
    /// reason <see cref="ClaudeWorkerAdapter"/> documents at length: a packed <c>dotnet tool</c> has
    /// no apphost, and naming one would write a dangling command into every worker's hook. On agy
    /// that failure is worse than on claude — an unparseable or absent hook response is read as an
    /// <em>allow</em> (<c>agy.hook-malformed-stdout-fails-open</c>), so a hook that cannot start
    /// does not fail loudly, it fails open. Hence the explicit existence guard.
    /// </para>
    /// </remarks>
    private static string BuildHooksJson()
    {
        var hookAssemblyPath = Path.Combine(AppContext.BaseDirectory, "Aer.Cli.dll");
        if (!File.Exists(hookAssemblyPath))
        {
            throw new InvalidOperationException(
                $"Cannot write the mandatory PreToolUse hook (decision 0029): '{hookAssemblyPath}' " +
                "does not exist. Every deployment of aer/Aer.Daemon must carry Aer.Cli.dll alongside " +
                "its own binary -- on agy a hook that cannot start is read as an ALLOW rather than " +
                "an error (agy.hook-malformed-stdout-fails-open), so this fails loudly here instead, " +
                "before any worker is dispatched.");
        }

        var command = $"dotnet \"{hookAssemblyPath.Replace('\\', '/')}\" agy-hook-check";
        var hooks = new Dictionary<string, object>
        {
            ["aer-permission-gate"] = new
            {
                PreToolUse = new[]
                {
                    new
                    {
                        matcher = "*",
                        hooks = new[]
                        {
                            new { type = "command", command, timeout = HookTimeoutSeconds },
                        },
                    },
                },
            },
        };

        return JsonSerializer.Serialize(hooks);
    }

    /// <summary>
    /// Seconds agy waits for the hook before giving up. agy's documented default is 30; this is set
    /// explicitly rather than inherited so the value is visible next to the reasoning. Generous for
    /// what the command does (parse stdin, compare a name, print an object) because the cost of
    /// overrunning is asymmetric: a timeout produces no stdout, and no stdout is an
    /// <em>allow</em> on this vendor.
    /// </summary>
    private const int HookTimeoutSeconds = 30;


    /// <summary>
    /// A structured <see cref="WorkerInvocation.PermissionGrant"/> always wins over the raw
    /// <see cref="WorkerInvocation.PermissionScope"/> string (<see cref="PermissionGrant"/>'s own
    /// docs record this precedence).
    /// </summary>
    /// <exception cref="PermissionGrantUnsupportedException">
    /// <paramref name="invocation"/> carries a <see cref="WorkerInvocation.PermissionGrant"/> that
    /// <see cref="TryTranslatePermissionGrant"/> refuses (e.g. requesting shell commands without network access, or vice versa).
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

    private static string EnvironmentReference(string name, bool isWindows) =>
        WorkerEnvironmentReference.For(name, isWindows);

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

    /// <summary>
    /// How far past AER's own timeout <c>--print-timeout</c> is set (#588).
    /// </summary>
    /// <remarks>
    /// The point of the flag is not to impose a limit — it is to stop <c>agy</c> imposing <i>its</i>
    /// default one first. Whichever limit expires first decides the failure mode, and the two are not
    /// equally good: AER's produces <c>CoreExitReason.TimedOut</c> and the reason
    /// <c>"Execution timed out."</c>, whereas agy's print-mode wait expiring produces a clean exit 0
    /// with no output file — the silent failure #588 was filed for. So agy's limit is pushed strictly
    /// beyond AER's and left as a backstop that should never fire.
    /// <para>
    /// Fixed rather than proportional. A proportional margin is dangerously tight at the short end —
    /// 25% of a 30-second timeout is under 8 seconds, well inside process-teardown jitter on a loaded
    /// machine — while at the long end the size of the backstop is irrelevant, because AER terminates
    /// the tree at its own deadline regardless. A margin too small does not fail loudly; it
    /// reintroduces the original silent exit-0 as a race.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan PrintTimeoutMargin = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Renders a timeout as a Go duration literal, which is what <c>agy</c>'s flag parser accepts.
    /// </summary>
    /// <remarks>
    /// Total seconds, never <see cref="TimeSpan.ToString()"/>. Measured on this host: <c>1200s</c>,
    /// <c>20m0s</c> and <c>20m</c> are all accepted, while <c>00:20:00</c> — precisely what
    /// <c>TimeSpan.ToString()</c> produces — is rejected with
    /// <c>invalid value "00:20:00" for flag -print-timeout: time: unknown unit ":" in duration</c> and
    /// exit code 2. A default interpolation of the TimeSpan would therefore have broken every gemini
    /// dispatch outright rather than degrading quietly.
    /// <para>
    /// Rounded up, so the emitted backstop is never a fraction of a second tighter than intended, and
    /// floored at one second because a zero or negative duration is not a value the flag accepts.
    /// </para>
    /// </remarks>
    private static string FormatPrintTimeout(TimeSpan timeout)
    {
        // Saturate rather than add blindly: TimeSpan addition *throws* on overflow instead of
        // clamping, and a binding's Timeout is operator-authored — any parseable TimeSpan is accepted,
        // including ones within a minute of TimeSpan.MaxValue. That throw would escape binding
        // resolution, so one absurd value in a bindings file would take down every worker in it
        // rather than only its own.
        var withMargin = timeout > TimeSpan.MaxValue - PrintTimeoutMargin
            ? TimeSpan.MaxValue
            : timeout + PrintTimeoutMargin;

        var seconds = (long)Math.Ceiling(withMargin.TotalSeconds);
        return $"{Math.Max(seconds, 1)}s";
    }
}
