using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters.Tests;

/// <summary>
/// M20 Phase 4's deliverable: unit tests for the refactored, direct shell-less
/// <see cref="GeminiWorkerAdapter"/> resolving.
/// </summary>
public class GeminiWorkerAdapterTests
{
    private static readonly WorkerContract ArchitectContract = new(
        "architect", ["goal"], [new ProducedOutput("plan.md")], []);

    private static string GetPrompt(CoreDispatchTarget target) => target.Args[1];

    [Fact]
    public void Resolves_to_direct_agy_execution_without_shell_wrapper()
    {
        var target = new GeminiWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Equal("agy", target.Program);
        Assert.Equal("-p", target.Args[0]);
        Assert.Equal("--mode", target.Args[2]);
        Assert.Equal("accept-edits", target.Args[3]);
        Assert.Equal("--add-dir", target.Args[4]);

        var artifactsRootVar = OperatingSystem.IsWindows() ? "%AER_ARTIFACTS_ROOT%" : "$AER_ARTIFACTS_ROOT";
        Assert.Equal(artifactsRootVar, target.Args[5]);
    }

    /// <summary>
    /// M23 Phase 3 (#272): WorkingDirectory carries no vendor-specific meaning — every adapter forwards
    /// it into CoreDispatchTarget unchanged. For <c>agy</c> that is necessary and <b>not sufficient</b>;
    /// see <see cref="The_rooms_directory_is_bound_with_add_dir_because_agy_ignores_the_process_cwd"/>.
    /// </summary>
    [Fact]
    public void A_configured_WorkingDirectory_is_forwarded_into_the_resolved_target()
    {
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", WorkingDirectory: "/home/user/my-project"), ArchitectContract);

        Assert.Equal("/home/user/my-project", target.WorkingDirectory);
    }

    /// <summary>
    /// #491: <c>agy -p</c> <b>ignores the process working directory</b>, so setting it on the dispatch
    /// target does not point the worker at the room's folder. Measured in #472 and recorded in
    /// <c>docs/vendor-capabilities.md</c>: launched from a directory listed in the CLI's own
    /// <c>trustedWorkspaces</c>, the emitted command still carried the CLI's install path as
    /// <c>Cwd</c>; from an untrusted directory it used the CLI's scratch dir and began a recursive
    /// search of the home folder looking for a file in the launch directory.
    /// </summary>
    /// <remarks>
    /// The failure this guards is silent rather than loud — a worker that cannot see the project does
    /// not error, it answers confidently about the wrong directory — and J11 (two subscriptions in one
    /// room) is a human-attested journey, so nothing automated would have caught it.
    /// </remarks>
    [Fact]
    public void The_rooms_directory_is_bound_with_add_dir_because_agy_ignores_the_process_cwd()
    {
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", WorkingDirectory: "/home/user/my-project"), ArchitectContract);

        var addDirValues = target.Args
            .Select((arg, i) => (arg, i))
            .Where(pair => pair.arg == "--add-dir")
            .Select(pair => target.Args[pair.i + 1])
            .ToList();

        Assert.Contains("/home/user/my-project", addDirValues);

        // Composes with the artifacts root rather than replacing it — --add-dir is repeatable on agy,
        // and the worker needs both its outputs and the project it is reasoning about.
        var artifactsRootVar = OperatingSystem.IsWindows() ? "%AER_ARTIFACTS_ROOT%" : "$AER_ARTIFACTS_ROOT";
        Assert.Contains(artifactsRootVar, addDirValues);
    }

    /// <summary>A directory-less room (#407's neutral-scratch case) must not emit an empty --add-dir.</summary>
    /// <remarks>
    /// Rewritten by #554, which added a second, unconditional <c>--add-dir</c> for the gate
    /// workspace. Counting occurrences was a proxy for "no empty value was emitted" that only held
    /// while exactly one <c>--add-dir</c> existed; it now asserts the actual claim directly, so a
    /// future third <c>--add-dir</c> does not fail this test for the wrong reason.
    /// </remarks>
    [Fact]
    public void No_directory_add_dir_is_emitted_when_the_room_has_no_working_directory()
    {
        var target = new GeminiWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        var addDirValues = target.Args
            .Select((arg, i) => (arg, i))
            .Where(pair => pair.arg == "--add-dir")
            .Select(pair => target.Args[pair.i + 1])
            .ToList();

        Assert.NotEmpty(addDirValues);
        Assert.All(addDirValues, value => Assert.False(string.IsNullOrWhiteSpace(value)));
    }

    [Fact]
    public void An_explicit_permission_scope_overrides_the_default()
    {
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "yolo"), ArchitectContract);

        Assert.Equal("yolo", target.Args[3]);
    }

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

    /// <remarks>
    /// De-positioned by #554: this asserted <c>Args[6]</c>/<c>Args[7]</c>, which shifted when the
    /// gate workspace added a second <c>--add-dir</c> pair. The claim was always "the model is
    /// passed through", never "it sits at index 6" — <see cref="ArgValue"/> already existed for
    /// exactly this and is what the neighbouring effort test uses.
    /// </remarks>
    [Fact]
    public void A_model_is_passed_through_when_set()
    {
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Model: "gemini-3-pro"), ArchitectContract);

        Assert.Equal("gemini-3-pro", ArgValue(target, "--model"));
    }

    [Fact]
    public void No_model_flag_is_emitted_when_the_model_is_unset()
    {
        var target = new GeminiWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--model", target.Args);
    }

    [Fact]
    public void An_effort_is_passed_through_when_set()
    {
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Effort: "high"), ArchitectContract);

        Assert.Equal("high", ArgValue(target, "--effort"));
    }

    [Fact]
    public void No_effort_flag_is_emitted_when_the_effort_is_unset()
    {
        var target = new GeminiWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--effort", target.Args);
    }

    [Fact]
    public void The_prompt_names_every_declared_output_and_its_env_var_path()
    {
        var contract = new WorkerContract(
            "architect", [], [new ProducedOutput("plan.md"), new ProducedOutput("summary.md")], []);

        var target = new GeminiWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

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

        var target = new GeminiWorkerAdapter().Resolve(new WorkerInvocation("Review the plan."), contract);

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

        var target = new GeminiWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        Assert.DoesNotContain("Inputs, in the order listed", GetPrompt(target));
    }

    [Fact]
    public void Prompt_keeps_newlines_for_readability_on_all_platforms()
    {
        var contract = new WorkerContract("architect", ["goal"], [new ProducedOutput("plan.md")], []);
        var target = new GeminiWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        Assert.Contains('\n', GetPrompt(target));
    }

    [Fact]
    public void Shell_metacharacters_and_percent_signs_are_passed_raw_because_no_shell_evaluates_them()
    {
        var invocation = new WorkerInvocation("Quote this: \"$HOME\" and `whoami` and 100% path %PATH%.");

        var target = new GeminiWorkerAdapter().Resolve(invocation, ArchitectContract);

        var prompt = GetPrompt(target);
        Assert.Contains("Quote this: \"$HOME\" and `whoami` and 100% path %PATH%.", prompt);
    }

    /// <summary>Issue #292: CoreDispatcher's durable prompt.txt capture reads this field, not target.Args -- it must carry the identical text the -p argument does.</summary>
    [Fact]
    public void PromptText_carries_the_same_resolved_prompt_as_the_p_argument()
    {
        var target = new GeminiWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Equal(GetPrompt(target), target.PromptText);
    }

    [Fact]
    public void Null_invocation_or_contract_throws()
    {
        var adapter = new GeminiWorkerAdapter();

        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(null!, ArchitectContract));
        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(new WorkerInvocation("Draft a plan."), null!));
    }

    // M21 Phase 1: the structured PermissionGrant builder path. The tests above are untouched —
    // proving a hand-typed raw PermissionScope (including "yolo", a value outside the --mode
    // vocabulary the structured translator emits) still resolves identically.

    [Theory]
    [InlineData(false, false, "default")]
    [InlineData(true, false, "plan")]
    [InlineData(true, true, "accept-edits")]
    [InlineData(false, true, "accept-edits")]
    public void A_permission_grant_maps_read_write_combinations_to_the_matching_mode(
        bool readFiles, bool writeFiles, string expectedMode)
    {
        var grant = new PermissionGrant(ReadFiles: readFiles, WriteFiles: writeFiles);
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.Equal(expectedMode, target.Args[3]);
    }

    [Fact]
    public void A_permission_grant_takes_precedence_over_a_raw_permission_scope_when_both_are_set()
    {
        var grant = new PermissionGrant(WriteFiles: true);
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "yolo", PermissionGrant: grant), ArchitectContract);

        Assert.Equal("accept-edits", target.Args[3]);
    }

    [Fact]
    public void Requesting_shell_commands_is_refused_rather_than_approximated()
    {
        var grant = new PermissionGrant(RunShellCommands: true);

        var ex = Assert.Throws<PermissionGrantUnsupportedException>(() => new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract));

        Assert.Equal("gemini", ex.AdapterName);
    }

    [Fact]
    public void Requesting_network_access_is_refused_rather_than_approximated()
    {
        var grant = new PermissionGrant(NetworkAccess: true);

        var ex = Assert.Throws<PermissionGrantUnsupportedException>(() => new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract));

        Assert.Equal("gemini", ex.AdapterName);
    }

    [Fact]
    public void TryTranslatePermissionGrant_refuses_shell_commands_without_throwing()
    {
        var adapter = new GeminiWorkerAdapter();

        var succeeded = adapter.TryTranslatePermissionGrant(
            new PermissionGrant(RunShellCommands: true), out var resolved, out var gapReason);

        Assert.False(succeeded);
        Assert.Null(resolved);
        Assert.NotNull(gapReason);
    }

    [Fact]
    public void Requesting_shell_and_network_access_together_translates_to_dangerously_skip_permissions()
    {
        var adapter = new GeminiWorkerAdapter();
        var grant = new PermissionGrant(RunShellCommands: true, NetworkAccess: true);

        var succeeded = adapter.TryTranslatePermissionGrant(grant, out var resolved, out var gapReason);

        Assert.True(succeeded);
        Assert.Equal("--dangerously-skip-permissions", resolved);
        Assert.Null(gapReason);
    }

    [Fact]
    public void Resolving_with_shell_and_network_access_emits_dangerously_skip_permissions_as_standalone_argument()
    {
        var grant = new PermissionGrant(RunShellCommands: true, NetworkAccess: true);
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        Assert.Equal("agy", target.Program);
        Assert.Equal("-p", target.Args[0]);
        Assert.Equal("--dangerously-skip-permissions", target.Args[2]);
        Assert.DoesNotContain("--mode", target.Args);
        Assert.Equal("--add-dir", target.Args[3]);
    }

    // ---------------------------------------------------------------- #554: the PreToolUse gate
    //
    // Decision 0029 makes the hook mandatory on every spawned worker. The tests below assert the
    // three things that have to hold for it to actually gate anything: the workspace is handed to
    // --add-dir (agy loads hooks from nowhere else, #538), the denied-tool list reaches the hook
    // process (via the environment -- measured by the `agy.hook-env-inherited` sentinel), and the
    // mapping covers the tools that would otherwise leak the withheld category.

    private static string EnvValue(CoreDispatchTarget target, string name) =>
        target.Environment!.Single(pair => pair.Name == name).Value;

    [Fact]
    public void Every_invocation_carries_the_agy_workspace_on_add_dir_so_the_gate_is_loaded()
    {
        // Unconditional, like #543's claude side: not only when a flow declares a gate. A hook
        // installed only sometimes cannot be relied on by anything downstream.
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan."), ArchitectContract);

        var addDirValues = target.Args
            .Select((arg, i) => (arg, i))
            .Where(pair => pair.arg == "--add-dir")
            .Select(pair => target.Args[pair.i + 1])
            .ToList();

        Assert.Contains(addDirValues, dir =>
            dir.EndsWith(GeminiWorkerAdapter.AgyWorkspaceDirectoryName, StringComparison.Ordinal));
    }

    [Fact]
    public void The_gate_workspace_holds_a_hooks_file_naming_the_agy_hook_check_command()
    {
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan."), ArchitectContract);

        var workspace = target.Args
            .Select((arg, i) => (arg, i))
            .Where(pair => pair.arg == "--add-dir")
            .Select(pair => target.Args[pair.i + 1])
            .Single(dir => dir.EndsWith(GeminiWorkerAdapter.AgyWorkspaceDirectoryName, StringComparison.Ordinal));

        var hooksPath = Path.Combine(workspace, ".agents", "hooks.json");
        Assert.True(File.Exists(hooksPath), $"no hooks.json was written to '{hooksPath}'");

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(hooksPath));
        var handler = doc.RootElement
            .EnumerateObject().Single().Value      // hooks are keyed by an arbitrary NAME at the root
            .GetProperty("PreToolUse")[0];
        Assert.Equal("*", handler.GetProperty("matcher").GetString());

        var command = handler.GetProperty("hooks")[0].GetProperty("command").GetString()!;
        Assert.Contains("agy-hook-check", command, StringComparison.Ordinal);
        // Shell-parsed, with no exec form available on this vendor: a raw Windows path's \U and \t
        // would be read as escapes, so the path must be forward-slashed inside its quotes.
        Assert.DoesNotContain('\\', command);
    }

    [Fact]
    public void A_withheld_category_reaches_the_hook_through_the_environment()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: false,
                                        RunShellCommands: true, NetworkAccess: true);
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var denied = EnvValue(target, GeminiWorkerAdapter.DeniedToolsVariable).Split(',');

        Assert.Contains("write_to_file", denied);
        Assert.Contains("replace_file_content", denied);
        Assert.Contains("multi_replace_file_content", denied);
        // Polarity: the granted categories must NOT be withheld, or a gate that denies everything
        // would pass the assertions above while breaking every worker.
        Assert.DoesNotContain("view_file", denied);
        Assert.DoesNotContain("run_command", denied);
        Assert.DoesNotContain("search_web", denied);
    }

    [Fact]
    public void Withholding_reads_also_withholds_the_tools_that_return_file_contents()
    {
        // grep_search returns file CONTENT, and list_dir/find_by_name disclose structure -- mapping
        // ReadFiles to view_file alone leaves the withheld category reachable. Found by the
        // implementation advisor reading agy's tool list against the first draft of this mapping.
        var grant = new PermissionGrant(ReadFiles: false, WriteFiles: true,
                                        RunShellCommands: true, NetworkAccess: true);
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var denied = EnvValue(target, GeminiWorkerAdapter.DeniedToolsVariable).Split(',');

        Assert.Contains("view_file", denied);
        Assert.Contains("grep_search", denied);
        Assert.Contains("list_dir", denied);
        Assert.Contains("find_by_name", denied);
        Assert.DoesNotContain("write_to_file", denied);
    }

    [Fact]
    public void Withholding_the_shell_also_withholds_control_of_background_shell_processes()
    {
        // manage_task sends stdin to and kills background commands, so withholding run_command
        // alone leaves shell control reachable.
        //
        // Network is withheld here too, and not by choice: TryTranslatePermissionGrant refuses
        // shell-without-network and network-without-shell outright, because the only agy flag that
        // grants either grants both. So the two categories are expressible only together, and a
        // shell-withheld grant is always also a network-withheld one on this vendor.
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true,
                                        RunShellCommands: false, NetworkAccess: false);
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionGrant: grant), ArchitectContract);

        var denied = EnvValue(target, GeminiWorkerAdapter.DeniedToolsVariable).Split(',');

        Assert.Contains("run_command", denied);
        Assert.Contains("manage_task", denied);
        Assert.DoesNotContain("view_file", denied);
    }

    [Fact]
    public void An_invocation_with_no_grant_sets_the_variable_to_empty_rather_than_omitting_it()
    {
        // agy-hook-check reads an absent/empty value as "nothing withheld" and allows; it denies
        // when it cannot determine what is withheld. Those must stay distinguishable, so the
        // variable is always present.
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Equal(string.Empty, EnvValue(target, GeminiWorkerAdapter.DeniedToolsVariable));
    }

    [Fact]
    public void The_denied_tools_variable_matches_the_cli_side_contract()
    {
        // Aer.Adapters cannot reference Aer.Cli, so this name is a plain string contract asserted
        // on both sides. If they drift the hook reads an empty list and allows everything.
        Assert.Equal("AER_HOOK_DENIED_TOOLS", GeminiWorkerAdapter.DeniedToolsVariable);
    }
}
