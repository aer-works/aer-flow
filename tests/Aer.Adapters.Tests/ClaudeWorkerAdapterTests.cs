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

    [Fact]
    public void Resolves_to_direct_claude_execution_without_shell_wrapper()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.Equal("claude", target.Program);
        Assert.Equal("-p", target.Args[0]);
        Assert.Equal("--allowedTools", target.Args[2]);
        Assert.Equal("Write", target.Args[3]);
        Assert.Equal("--output-format", target.Args[4]);
        Assert.Equal("text", target.Args[5]);
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

        Assert.Equal("--model", target.Args[6]);
        Assert.Equal("claude-opus-4-5", target.Args[7]);
    }

    [Fact]
    public void No_model_flag_is_emitted_when_the_model_is_unset()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--model", target.Args);
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

    [Fact]
    public void Null_invocation_or_contract_throws()
    {
        var adapter = new ClaudeWorkerAdapter();

        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(null!, ArchitectContract));
        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(new WorkerInvocation("Draft a plan."), null!));
    }
}
