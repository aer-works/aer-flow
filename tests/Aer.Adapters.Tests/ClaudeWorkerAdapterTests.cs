using Aer.Flow.Domain;

namespace Aer.Adapters.Tests;

/// <summary>
/// M11 Phase 2's deliverable (#85): the constructed command / args / env / prompt string a
/// <see cref="ClaudeWorkerAdapter"/> produces for a headless <c>claude</c> invocation, asserted
/// without spawning any real process — live runs are Phase 4's gate.
/// </summary>
public class ClaudeWorkerAdapterTests
{
    private static readonly WorkerContract ArchitectContract = new(
        "architect", ["goal"], [new ProducedOutput("plan.md")], []);

    [Fact]
    public void Resolves_to_a_shell_wrapper_so_stdin_can_be_redirected()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cmd", target.Program);
            Assert.Equal("/c", target.Args[0]);
            Assert.EndsWith("< NUL", target.Args[1]);
        }
        else
        {
            Assert.Equal("sh", target.Program);
            Assert.Equal("-c", target.Args[0]);
            Assert.EndsWith("< /dev/null", target.Args[1]);
        }
    }

    [Fact]
    public void The_command_invokes_claude_with_the_prompt_and_default_permission_scope()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        var commandLine = target.Args[1];
        Assert.Contains("claude -p ", commandLine);
        Assert.Contains("Draft a plan.", commandLine);
        Assert.Contains("--allowedTools \"Write\"", commandLine);
        Assert.Contains("--output-format text", commandLine);
    }

    [Fact]
    public void An_explicit_permission_scope_overrides_the_default()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash(git:*)"), ArchitectContract);

        Assert.Contains("--allowedTools \"Write,Bash(git:*)\"", target.Args[1]);
        Assert.DoesNotContain("\"Write\"", target.Args[1]);
    }

    [Fact]
    public void A_model_is_passed_through_when_set()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Model: "claude-opus-4-5"), ArchitectContract);

        Assert.Contains("--model \"claude-opus-4-5\"", target.Args[1]);
    }

    [Fact]
    public void No_model_flag_is_emitted_when_the_model_is_unset()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--model", target.Args[1]);
    }

    [Fact]
    public void The_prompt_names_every_declared_output_and_its_env_var_path()
    {
        var contract = new WorkerContract(
            "architect", [], [new ProducedOutput("plan.md"), new ProducedOutput("summary.md")], []);

        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        var commandLine = target.Args[1];
        var outputVar = OperatingSystem.IsWindows() ? "%AER_OUTPUT_DIR%" : "$AER_OUTPUT_DIR";
        var separator = OperatingSystem.IsWindows() ? '\\' : '/';
        Assert.Contains($"plan.md: {outputVar}{separator}plan.md", commandLine);
        Assert.Contains($"summary.md: {outputVar}{separator}summary.md", commandLine);
    }

    [Fact]
    public void The_prompt_names_every_required_input_and_its_env_var_path()
    {
        var contract = new WorkerContract(
            "critic", ["plan", "guidelines"], [new ProducedOutput("review.md")], []);

        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Review the plan."), contract);

        var commandLine = target.Args[1];
        var inputVar0 = OperatingSystem.IsWindows() ? "%AER_INPUT_0%" : "$AER_INPUT_0";
        var inputVar1 = OperatingSystem.IsWindows() ? "%AER_INPUT_1%" : "$AER_INPUT_1";
        Assert.Contains($"plan: {inputVar0}", commandLine);
        Assert.Contains($"guidelines: {inputVar1}", commandLine);
    }

    [Fact]
    public void A_contract_with_no_inputs_omits_the_inputs_section()
    {
        var contract = new WorkerContract("architect", [], [new ProducedOutput("plan.md")], []);

        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        Assert.DoesNotContain("Inputs, in the order listed", target.Args[1]);
    }

    [Fact]
    public void A_prompt_templates_own_shell_metacharacters_are_defused_not_expanded()
    {
        var invocation = new WorkerInvocation("Quote this: \"$HOME\" and `whoami` and a\\backslash.");

        var target = new ClaudeWorkerAdapter().Resolve(invocation, ArchitectContract);

        var commandLine = target.Args[1];
        if (OperatingSystem.IsWindows())
        {
            // Windows escaping: '"' doubled, '%' doubled (none present here); '\' and '`' pass through untouched.
            Assert.Contains("Quote this: \"\"$HOME\"\" and `whoami` and a\\backslash.", commandLine);
        }
        else
        {
            // POSIX escaping: '\' -> '\\', '"' -> '\"', '`' -> '\`', '$' -> '\$', applied in that order.
            Assert.Contains("Quote this: \\\"\\$HOME\\\" and \\`whoami\\` and a\\\\backslash.", commandLine);
        }

        // The adapter's own AER_OUTPUT_DIR reference must still appear unescaped, proving the
        // template's defusal didn't also neutralize the adapter's generated references.
        var outputVar = OperatingSystem.IsWindows() ? "%AER_OUTPUT_DIR%" : "$AER_OUTPUT_DIR";
        Assert.Contains(outputVar, commandLine);
    }

    [Fact]
    public void Null_invocation_or_contract_throws()
    {
        var adapter = new ClaudeWorkerAdapter();

        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(null!, ArchitectContract));
        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(new WorkerInvocation("Draft a plan."), null!));
    }
}
