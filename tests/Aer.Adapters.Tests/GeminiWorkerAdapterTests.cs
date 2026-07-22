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

    /// <summary>M23 Phase 3 (#272): WorkingDirectory carries no vendor-specific meaning — every adapter forwards it into CoreDispatchTarget unchanged.</summary>
    [Fact]
    public void A_configured_WorkingDirectory_is_forwarded_into_the_resolved_target()
    {
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", WorkingDirectory: "/home/user/my-project"), ArchitectContract);

        Assert.Equal("/home/user/my-project", target.WorkingDirectory);
    }

    [Fact]
    public void An_explicit_permission_scope_overrides_the_default()
    {
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "yolo"), ArchitectContract);

        Assert.Equal("yolo", target.Args[3]);
    }

    [Fact]
    public void A_model_is_passed_through_when_set()
    {
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Model: "gemini-3-pro"), ArchitectContract);

        Assert.Equal("--model", target.Args[6]);
        Assert.Equal("gemini-3-pro", target.Args[7]);
    }

    [Fact]
    public void No_model_flag_is_emitted_when_the_model_is_unset()
    {
        var target = new GeminiWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--model", target.Args);
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
}
