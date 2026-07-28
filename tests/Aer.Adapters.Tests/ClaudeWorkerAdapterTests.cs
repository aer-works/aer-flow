using System.Diagnostics;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters.Tests;

/// <summary>
/// M20 Phase 4's deliverable: unit tests for the refactored, direct shell-less
/// <see cref="ClaudeWorkerAdapter"/> resolving.
/// </summary>
public class ClaudeWorkerAdapterTests
{
    private static readonly WorkerContract ArchitectContract = new(
        "architect", ["goal"], [new ProducedOutput("plan.md")], []);

    private static string GetPrompt(CoreDispatchTarget target) => target.Args[1];

    /// <summary>The value token immediately after <paramref name="flag"/> in the flat argv, or null.</summary>
    private static string? ArgValue(CoreDispatchTarget target, string flag)
    {
        for (var i = 0; i < target.Args.Count - 1; i++)
        {
            if (target.Args[i] == flag)
            {
                return target.Args[i + 1];
            }
        }

        return null;
    }

    [Fact]
    public void Resolves_to_direct_claude_execution_without_shell_wrapper()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Equal("claude", target.Program);
        Assert.Equal("-p", target.Args[0]);
        Assert.Equal("--allowedTools", target.Args[2]);
        Assert.Equal("Write", target.Args[3]);
        Assert.Equal("--add-dir", target.Args[4]);
        // #533 inserted --settings/--mcp-config after --add-dir's value; positional indices past
        // that point are no longer stable, so this uses the order-independent helper like every
        // newer test in this file already does.
        Assert.Equal("text", ArgValue(target, "--output-format"));
    }

    /// <summary>
    /// #289: Claude Code's own directory-trust sandbox (separate from --allowedTools) was found,
    /// via a live run against the real authenticated CLI, to non-deterministically refuse to write
    /// AER_OUTPUT_DIR when it falls outside the spawned process's cwd -- which it always does for a
    /// plain chat session with no WorkingDirectory. --add-dir AER_ARTIFACTS_ROOT (the same grant
    /// GeminiWorkerAdapter already carries for agy, per ArtifactManager.BuildEnvironment's own doc
    /// comment) eliminated the failure across every trial once added.
    /// </summary>
    [Fact]
    public void The_artifacts_root_is_granted_via_add_dir_so_output_writes_outside_cwd_are_trusted()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Equal("--add-dir", target.Args[4]);
        var artifactsRootVar = OperatingSystem.IsWindows() ? "%AER_ARTIFACTS_ROOT%" : "$AER_ARTIFACTS_ROOT";
        Assert.Equal(artifactsRootVar, target.Args[5]);
    }

    /// <summary>M23 Phase 3 (#272): WorkingDirectory carries no vendor-specific meaning — every adapter forwards it into CoreDispatchTarget unchanged.</summary>
    [Fact]
    public void A_configured_WorkingDirectory_is_forwarded_into_the_resolved_target()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", WorkingDirectory: "/home/user/my-project"), ArchitectContract);

        Assert.Equal("/home/user/my-project", target.WorkingDirectory);
    }

    [Fact]
    public void A_null_WorkingDirectory_leaves_the_resolved_target_with_no_explicit_cwd()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Null(target.WorkingDirectory);
    }

    [Fact]
    public void An_explicit_permission_scope_overrides_the_default()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash(git:*)"), ArchitectContract);

        Assert.Equal("Write,Bash(git:*)", target.Args[3]);
    }

    [Fact]
    public void A_model_is_passed_through_when_set()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Model: "claude-opus-4-5"), ArchitectContract);

        Assert.Equal("claude-opus-4-5", ArgValue(target, "--model"));
    }

    [Fact]
    public void No_model_flag_is_emitted_when_the_model_is_unset()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--model", target.Args);
    }

    [Fact]
    public void An_effort_is_passed_through_when_set()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Effort: "high"), ArchitectContract);

        Assert.Equal("high", ArgValue(target, "--effort"));
    }

    [Fact]
    public void No_effort_flag_is_emitted_when_the_effort_is_unset()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--effort", target.Args);
    }

    [Fact]
    public void The_prompt_names_every_declared_output_and_its_env_var_path()
    {
        var contract = new WorkerContract(
            "architect", [], [new ProducedOutput("plan.md"), new ProducedOutput("summary.md")], []);

        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        var prompt = GetPrompt(target);
        var outputVar = OperatingSystem.IsWindows() ? "%AER_OUTPUT_DIR%" : "$AER_OUTPUT_DIR";
        var separator = OperatingSystem.IsWindows() ? '\\' : '/';
        Assert.Contains($"plan.md: {outputVar}{separator}plan.md", prompt);
        Assert.Contains($"summary.md: {outputVar}{separator}summary.md", prompt);
    }

    [Fact]
    public void The_prompt_names_every_required_input_and_its_env_var_path()
    {
        var contract = new WorkerContract(
            "critic", ["plan", "guidelines"], [new ProducedOutput("review.md")], []);

        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Review the plan."), contract);

        var prompt = GetPrompt(target);
        var inputVar0 = OperatingSystem.IsWindows() ? "%AER_INPUT_0%" : "$AER_INPUT_0";
        var inputVar1 = OperatingSystem.IsWindows() ? "%AER_INPUT_1%" : "$AER_INPUT_1";
        Assert.Contains($"plan: {inputVar0}", prompt);
        Assert.Contains($"guidelines: {inputVar1}", prompt);
    }

    [Fact]
    public void A_contract_with_no_inputs_omits_the_inputs_section()
    {
        var contract = new WorkerContract("architect", [], [new ProducedOutput("plan.md")], []);

        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        Assert.DoesNotContain("Inputs, in the order listed", GetPrompt(target));
    }

    [Fact]
    public void Prompt_keeps_newlines_for_readability_on_all_platforms()
    {
        var contract = new WorkerContract("architect", ["goal"], [new ProducedOutput("plan.md")], []);
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        Assert.Contains('\n', GetPrompt(target));
    }

    [Fact]
    public void Shell_metacharacters_and_percent_signs_are_passed_raw_because_no_shell_evaluates_them()
    {
        var invocation = new WorkerInvocation("Quote this: \"$HOME\" and `whoami` and 100% path %PATH%.");

        var target = new ClaudeWorkerAdapter().Resolve(invocation, ArchitectContract);

        var prompt = GetPrompt(target);
        Assert.Contains("Quote this: \"$HOME\" and `whoami` and 100% path %PATH%.", prompt);
    }

    /// <summary>Issue #292: CoreDispatcher's durable prompt.txt capture reads this field, not target.Args -- it must carry the identical text the -p argument does.</summary>
    [Fact]
    public void PromptText_carries_the_same_resolved_prompt_as_the_p_argument()
    {
        var contract = new WorkerContract("architect", ["goal"], [new ProducedOutput("plan.md")], []);
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        Assert.Equal(GetPrompt(target), target.PromptText);
    }

    [Fact]
    public void Null_invocation_or_contract_throws()
    {
        var adapter = new ClaudeWorkerAdapter();

        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(null!, ArchitectContract));
        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(new WorkerInvocation("Draft a plan."), null!));
    }

    // M21 Phase 1: the structured PermissionGrant builder path. The tests above are untouched —
    // proving a hand-typed raw PermissionScope still resolves identically is exactly "don't touch
    // the existing cases."

    [Fact]
    public void A_permission_grant_composes_every_category_into_allowedTools_in_a_fixed_order()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.Equal("Read,Edit,Write,Bash,WebFetch,WebSearch", target.Args[3]);
    }

    [Fact]
    public void A_permission_grant_scopes_shell_commands_to_its_patterns_when_given()
    {
        var grant = new PermissionGrant(RunShellCommands: true, ShellCommandPatterns: ["git:*", "npm:*"]);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.Equal("Bash(git:*),Bash(npm:*)", target.Args[3]);
    }

    [Fact]
    public void A_permission_grant_takes_precedence_over_a_raw_permission_scope_when_both_are_set()
    {
        var grant = new PermissionGrant(ReadFiles: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash(git:*)", PermissionGrant: grant),
            ArchitectContract);

        Assert.Equal("Read", target.Args[3]);
    }

    [Fact]
    public void TryTranslatePermissionGrant_never_refuses_for_claude()
    {
        var adapter = new ClaudeWorkerAdapter();

        var succeeded = adapter.TryTranslatePermissionGrant(
            new PermissionGrant(RunShellCommands: true, NetworkAccess: true), out var resolved, out var gapReason);

        Assert.True(succeeded);
        Assert.Equal("Bash,WebFetch,WebSearch", resolved);
        Assert.Null(gapReason);
    }

    // #331: --allowedTools only *pre-approves*; a withheld category must be *actively* denied via
    // --disallowedTools or a subscription worker still reaches the tool (a shell-denied session ran
    // `hostname`). These assert the enforcing flag is emitted onto the argv — the default-CI guard for
    // this class of bug, which shape-only translation tests could not catch. That the CLI *honours*
    // the flag is a live-vendor smoke gate (docs/runbooks/live-claude-smoke.md), not a unit test.

    [Fact]
    public void A_withheld_shell_grant_actively_denies_Bash_not_merely_omits_it_from_the_allow_list()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.DoesNotContain("Bash", ArgValue(target, "--allowedTools")!); // omitted from the allow-list...
        Assert.Contains("Bash", ArgValue(target, "--disallowedTools")!);    // ...and actively denied.
    }

    [Fact]
    public void The_disallowed_list_is_the_exact_complement_of_the_withheld_categories()
    {
        // Read granted; write, shell and network all withheld. Every withheld category maps to its
        // denied tool(s) EXCEPT writes, which #649 moved to the hook: named here, the CLI would refuse
        // the write before the hook could allow the one landing in AER_OUTPUT_DIR. The hook's own list
        // still carries them — see Withheld_writes_leave_the_flag_and_move_to_the_hooks_list.
        var grant = new PermissionGrant(ReadFiles: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.Equal("Read", ArgValue(target, "--allowedTools"));
        Assert.Equal("Bash,WebFetch,WebSearch", ArgValue(target, "--disallowedTools"));
    }

    [Fact]
    public void A_fully_permissive_grant_withholds_nothing_and_emits_no_disallowed_list()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.DoesNotContain("--disallowedTools", target.Args);
    }

    [Fact]
    public void A_raw_permission_scope_with_no_structured_grant_emits_no_disallowed_list()
    {
        // The Advanced escape hatch carries no categories to deny — a hand-typed scope is taken as-is.
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Read,Edit"), ArchitectContract);

        Assert.DoesNotContain("--disallowedTools", target.Args);
    }

    /// <summary>
    /// #533 constraints 1-2: hooks and MCP config load only from cwd's own `.claude/`, with no
    /// parent-directory fallback, and `--add-dir` loads neither on claude -- so both are passed
    /// explicitly, at files AER owns rather than the room's own directory.
    /// </summary>
    [Fact]
    public void Settings_and_mcp_config_are_passed_at_AER_owned_paths_that_exist_and_are_valid_json()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        var settingsPath = ArgValue(target, "--settings");
        var mcpConfigPath = ArgValue(target, "--mcp-config");

        Assert.NotNull(settingsPath);
        Assert.NotNull(mcpConfigPath);
        Assert.StartsWith(AerPaths.WorkerLaunchConfig, settingsPath);
        Assert.StartsWith(AerPaths.WorkerLaunchConfig, mcpConfigPath);
        Assert.True(File.Exists(settingsPath), "the file --settings points at must already exist");
        Assert.True(File.Exists(mcpConfigPath), "the file --mcp-config points at must already exist");

        // Both must be valid, parseable JSON, or the CLI invocation this constructs fails outright.
        using var settingsDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
        using var mcpDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(mcpConfigPath));
        Assert.Equal(System.Text.Json.JsonValueKind.Object, settingsDoc.RootElement.ValueKind);
        Assert.True(mcpDoc.RootElement.TryGetProperty("mcpServers", out _));
    }

    /// <summary>
    /// #543 reverses #533's "never overwrite" for this one file: the settings file is entirely
    /// AER-owned (nothing an operator could have put there survives), and it now carries the
    /// mandatory `PreToolUse` hook, so leaving stale content in place would permanently disable the
    /// gate on any machine that ran a pre-#543 build even once. This inverts what
    /// `An_existing_settings_file_survives_a_second_Resolve_call_untouched` asserted before #543 --
    /// deliberately, not a silent edit; see `EnsureLaunchConfigFiles`'s own doc comment.
    /// </summary>
    [Fact]
    public void A_settings_file_with_stale_content_is_overwritten_with_the_canonical_hook_on_the_next_resolve()
    {
        new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);
        var settingsPath = Path.Combine(AerPaths.WorkerLaunchConfig, "claude-settings.json");
        Assert.True(File.Exists(settingsPath));

        const string stale = """{"hooks":{"PreToolUse":[{"stale":"pre-543-content"}]}}""";
        File.WriteAllText(settingsPath, stale);

        new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft another plan."), ArchitectContract);

        var rewritten = File.ReadAllText(settingsPath);
        Assert.NotEqual(stale, rewritten);
        Assert.DoesNotContain("stale", rewritten);
    }

    /// <summary>
    /// The actual hook payload #543 ships: one `PreToolUse` matcher group covering every tool,
    /// invoked as `dotnet &lt;Aer.Cli.dll path&gt; hook-check` in exec form (`args` present, so
    /// Claude Code spawns it with no shell) -- see `BuildSettingsJson`'s doc comment for why this
    /// names the managed dll via `dotnet` rather than a native apphost (the packed global tool has
    /// no apphost at all).
    /// </summary>
    [Fact]
    public void The_settings_file_carries_a_PreToolUse_hook_that_matches_every_tool_and_points_at_hook_check()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);
        var settingsPath = ArgValue(target, "--settings")!;

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
        var preToolUse = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse");
        Assert.Equal(1, preToolUse.GetArrayLength());

        var matcherGroup = preToolUse[0];
        Assert.Equal("*", matcherGroup.GetProperty("matcher").GetString());

        var handler = matcherGroup.GetProperty("hooks")[0];
        Assert.Equal("command", handler.GetProperty("type").GetString());
        Assert.Equal("dotnet", handler.GetProperty("command").GetString());

        var args = handler.GetProperty("args").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(2, args.Count);
        Assert.EndsWith("Aer.Cli.dll", args[0]);
        Assert.True(File.Exists(args[0]), "the hook's first arg must point at a real, existing Aer.Cli.dll");
        Assert.Equal("hook-check", args[1]);

        // `dotnet <dll>` needs the dll's own .runtimeconfig.json alongside it to run at all -- a
        // review pass on #543 pointed out that checking only the .dll's existence proves nothing
        // about whether `dotnet` can actually load it.
        var runtimeConfigPath = Path.ChangeExtension(args[0], null) + ".runtimeconfig.json";
        Assert.True(
            File.Exists(runtimeConfigPath),
            $"dotnet needs '{runtimeConfigPath}' alongside Aer.Cli.dll to run it at all");
    }

    /// <summary>
    /// #543: the settings file is one static, shared file across every spawn, so per-invocation
    /// data (what this specific worker was denied) has to reach hook-check another way -- the
    /// process environment, which a hook subprocess inherits from claude, which inherits it from
    /// AER's own spawn (confirmed in `.vendor-survey/corpus/claude__hooks.md`: "A hook process
    /// inherits the parent environment"). This is the same string `--disallowedTools` receives, not
    /// a separately-derived value, so the two mechanisms can never disagree about what was withheld.
    /// </summary>
    [Fact]
    public void The_denied_tools_environment_variable_is_the_flag_plus_the_write_tools()
    {
        var grant = new PermissionGrant(ReadFiles: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        // #649: the two channels deliberately differ, on writes and only on writes. The flag is what
        // the CLI enforces directly; the hook list is what it enforces with the target path in hand.
        Assert.NotNull(target.Environment);
        var hookList = target.Environment!.Single(v => v.Name == ClaudeWorkerAdapter.DeniedToolsVariable).Value;

        // #600's vendor tag and #649's differing contents, on the same value.
        Assert.Equal("Bash,WebFetch,WebSearch", ArgValue(target, "--disallowedTools"));
        Assert.Equal("claude:Edit,Write,NotebookEdit,Bash,WebFetch,WebSearch", hookList);
    }

    [Fact]
    public void The_denied_tools_environment_variable_is_set_even_when_nothing_is_withheld()
    {
        // hook-check must see an explicit "" rather than a missing variable it could confuse with
        // "not spawned by AER at all" -- Contains below also proves the variable is present at all.
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.NotNull(target.Environment);
        // #600: tagged, so "AER set this and nothing is withheld" is distinguishable from "the variable
        // never arrived". The empty list after the tag is the part that still means "nothing withheld".
        Assert.Contains((ClaudeWorkerAdapter.DeniedToolsVariable, "claude:"), target.Environment);
    }

    /// <summary>
    /// #543, from review: an inherited `CLAUDE_CODE_SIMPLE=1` disables hooks the same way `--bare`
    /// does (see the doc comment above `SimpleModeVariable`'s declaration), and `AerTask` inherits
    /// the full parent environment by default -- so this override has to actually be on the argv
    /// this method returns, not merely exist as an idea in a comment.
    /// </summary>
    [Fact]
    public void An_inherited_CLAUDE_CODE_SIMPLE_is_overridden_in_the_process_environment()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.NotNull(target.Environment);
        Assert.Contains((ClaudeWorkerAdapter.SimpleModeVariable, "0"), target.Environment);
    }

    /// <summary>
    /// #533 constraint 3, measured (not vendor-documented) default: `verify.py`'s
    /// `fanout.nesting-allowed-by-default` found a subagent CAN spawn its own subagent with nothing
    /// configured, so AER sets the cap explicitly rather than trusting the vendor's stated default.
    /// </summary>
    [Fact]
    public void The_subagent_spawn_depth_is_capped_to_one_via_the_process_environment()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.NotNull(target.Environment);
        Assert.Contains(
            (ClaudeWorkerAdapter.MaxSubagentSpawnDepthVariable, "1"),
            target.Environment);
    }

    // The tests above assert against the C# objects Resolve() builds -- they would pass equally
    // against a hook command that looks right on paper but fails the moment Claude Code actually
    // spawns it. These two spawn the exact command+args the settings file names, as a real child
    // process fed real stdin and the real environment variable, exactly as Claude Code's exec-form
    // hook dispatch does -- proving the wiring, not just the shape. `Aer.Adapters.Tests` has no
    // project reference to `Aer.Cli` (layering: the CLI depends on the adapters, never the
    // reverse), so this runs the built executable directly rather than calling HookCheckCommand
    // in-process; it needs `Aer.Cli` built into a sibling output directory, true for any normal
    // `pixi run test` / `pixi run build` run.

    [Fact]
    public void The_resolved_hook_command_actually_denies_a_withheld_tool_when_spawned_for_real()
    {
        var grant = new PermissionGrant(ReadFiles: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var (exitCode, stderr) = RunResolvedHookCommand(target, """{"tool_name": "Bash"}""");

        Assert.Equal(2, exitCode);
        Assert.Contains("Bash", stderr);
    }

    [Fact]
    public void The_resolved_hook_command_actually_allows_a_granted_tool_when_spawned_for_real()
    {
        var grant = new PermissionGrant(ReadFiles: true);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var (exitCode, stderr) = RunResolvedHookCommand(target, """{"tool_name": "Read"}""");

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
    }

    private static (int ExitCode, string Stderr) RunResolvedHookCommand(CoreDispatchTarget target, string stdin)
    {
        var settingsPath = ArgValue(target, "--settings")!;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
        var handler = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0].GetProperty("hooks")[0];
        var command = handler.GetProperty("command").GetString()!;
        var args = handler.GetProperty("args").EnumerateArray().Select(e => e.GetString()).ToList();

        var startInfo = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg!);
        }

        var deniedToolsVar = target.Environment!.First(e => e.Name == ClaudeWorkerAdapter.DeniedToolsVariable);
        startInfo.Environment[deniedToolsVar.Name] = deniedToolsVar.Value;

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEnd();
        var exited = process.WaitForExit(TimeSpan.FromSeconds(30));
        Assert.True(exited, "hook-check did not exit within 30s");

        return (process.ExitCode, stderr);
    }

    [Fact]
    public void Withheld_writes_leave_the_flag_and_move_to_the_hooks_list()
    {
        // #649's boundary change, asserted on both channels at once because the whole point is that
        // they now differ. A write named in --disallowedTools is refused by the CLI before the hook is
        // consulted, so leaving it there makes the outbox exemption unreachable and a read-only
        // reviewer unable to produce the artifact it was dispatched for. The hook keeps the names,
        // because it is what still denies a workspace write.
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: false);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var flag = ArgValue(target, "--disallowedTools") ?? string.Empty;
        var hookList = target.Environment!.Single(v => v.Name == ClaudeWorkerAdapter.DeniedToolsVariable).Value;

        Assert.DoesNotContain("Write", flag, StringComparison.Ordinal);
        Assert.DoesNotContain("Edit", flag, StringComparison.Ordinal);
        Assert.Contains("Write", hookList, StringComparison.Ordinal);
        Assert.Contains("Edit", hookList, StringComparison.Ordinal);
        Assert.Contains("NotebookEdit", hookList, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_other_withheld_category_still_appears_on_both_channels()
    {
        // The control on the change above. Only writes move; a change that dropped every category from
        // the flag would pass the first assertion and quietly remove the enforcement the flag provides
        // for the categories where the hook has no path to inspect.
        var grant = new PermissionGrant(
            ReadFiles: false, WriteFiles: true, RunShellCommands: false, NetworkAccess: false);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var flag = ArgValue(target, "--disallowedTools")!;
        var hookList = target.Environment!.Single(v => v.Name == ClaudeWorkerAdapter.DeniedToolsVariable).Value;

        foreach (var tool in new[] { "Read", "Bash", "WebFetch", "WebSearch" })
        {
            Assert.Contains(tool, flag, StringComparison.Ordinal);
            Assert.Contains(tool, hookList, StringComparison.Ordinal);
        }
    }
}
