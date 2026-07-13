using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters.Tests;

/// <summary>
/// M11 Phase 2's deliverable (#85): the constructed command / args / env / prompt string a
/// <see cref="ClaudeWorkerAdapter"/> produces for a headless <c>claude</c> invocation, asserted
/// without spawning any real process — live runs are Phase 4's gate.
/// <para>
/// Windows and Unix assert against genuinely different shapes of <see cref="CoreDispatchTarget.Args"/>
/// (separate argv tokens vs. one pre-quoted <c>sh -c</c> string — see <c>ClaudeWorkerAdapter</c>'s
/// remarks), not just different escaping of the same shape, since a live repro found Windows
/// argument quoting happens exactly once only when each token is its own array element.
/// </para>
/// </summary>
public class ClaudeWorkerAdapterTests
{
    private static readonly WorkerContract ArchitectContract = new(
        "architect", ["goal"], [new ProducedOutput("plan.md")], []);

    /// <summary>
    /// Windows always puts the prompt at this fixed index (<c>/c claude -p &lt;prompt&gt;</c>),
    /// regardless of whether <c>--model</c> is present later, since <c>--model</c> is only ever
    /// appended after the fixed <c>--allowedTools</c>/<c>--output-format</c> pair.
    /// </summary>
    private const int WindowsPromptIndex = 3;

    private static string GetPrompt(CoreDispatchTarget target) =>
        OperatingSystem.IsWindows() ? target.Args[WindowsPromptIndex] : target.Args[1];

    [Fact]
    public void Resolves_to_a_shell_wrapper_so_stdin_can_be_redirected()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cmd", target.Program);
            Assert.Equal("/c", target.Args[0]);
            Assert.Equal("<", target.Args[^2]);
            Assert.Equal("NUL", target.Args[^1]);
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

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("claude", target.Args[1]);
            Assert.Equal("-p", target.Args[2]);
            Assert.Contains("Draft a plan.", target.Args[WindowsPromptIndex]);
            Assert.Contains("--allowedTools", target.Args);
            Assert.Contains("Write", target.Args);
            Assert.Contains("--output-format", target.Args);
            Assert.Contains("text", target.Args);
        }
        else
        {
            var commandLine = target.Args[1];
            Assert.Contains("claude -p ", commandLine);
            Assert.Contains("Draft a plan.", commandLine);
            Assert.Contains("--allowedTools \"Write\"", commandLine);
            Assert.Contains("--output-format text", commandLine);
        }
    }

    [Fact]
    public void An_explicit_permission_scope_overrides_the_default()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "Write,Bash(git:*)"), ArchitectContract);

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("Write,Bash(git:*)", target.Args);
            Assert.DoesNotContain("Write", target.Args);
        }
        else
        {
            Assert.Contains("--allowedTools \"Write,Bash(git:*)\"", target.Args[1]);
            Assert.DoesNotContain("\"Write\"", target.Args[1]);
        }
    }

    [Fact]
    public void A_model_is_passed_through_when_set()
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Model: "claude-opus-4-5"), ArchitectContract);

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("--model", target.Args);
            Assert.Contains("claude-opus-4-5", target.Args);
        }
        else
        {
            Assert.Contains("--model \"claude-opus-4-5\"", target.Args[1]);
        }
    }

    [Fact]
    public void No_model_flag_is_emitted_when_the_model_is_unset()
    {
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--model", OperatingSystem.IsWindows() ? target.Args : [target.Args[1]]);
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
    public void The_windows_command_line_never_contains_a_raw_newline_but_unix_does()
    {
        var contract = new WorkerContract("architect", ["goal"], [new ProducedOutput("plan.md")], []);
        var target = new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        if (OperatingSystem.IsWindows())
        {
            // cmd.exe's `/c` parser ends the current statement at an embedded newline even inside
            // a quoted argument, silently truncating the invocation before --allowedTools/
            // --output-format/--model and the output-path instructions ever reach claude.
            Assert.All(target.Args, arg => Assert.DoesNotContain('\n', arg));
        }
        else
        {
            // sh -c's quoting spans lines correctly, so newlines are kept for readability.
            Assert.Contains('\n', target.Args[1]);
        }
    }

    [Fact]
    public void A_prompt_templates_own_shell_metacharacters_are_defused_not_expanded()
    {
        var invocation = new WorkerInvocation("Quote this: \"$HOME\" and `whoami` and a\\backslash.");

        var target = new ClaudeWorkerAdapter().Resolve(invocation, ArchitectContract);

        var prompt = GetPrompt(target);
        if (OperatingSystem.IsWindows())
        {
            // Passed as its own argv token: aer-core's Windows spawn (`Command::args`) applies
            // correct Win32 argument quoting to it exactly once, so no manual escaping of a literal
            // quote/backtick/dollar/backslash is needed here -- confirmed live that also escaping
            // '"' here (on top of that automatic quoting) corrupted the command so badly claude
            // received no prompt at all.
            Assert.Contains("Quote this: \"$HOME\" and `whoami` and a\\backslash.", prompt);
        }
        else
        {
            // POSIX escaping: '\' -> '\\', '"' -> '\"', '`' -> '\`', '$' -> '\$', applied in that order.
            Assert.Contains("Quote this: \\\"\\$HOME\\\" and \\`whoami\\` and a\\\\backslash.", prompt);
        }

        // The adapter's own AER_OUTPUT_DIR reference must still appear unescaped, proving the
        // template's defusal didn't also neutralize the adapter's generated references.
        var outputVar = OperatingSystem.IsWindows() ? "%AER_OUTPUT_DIR%" : "$AER_OUTPUT_DIR";
        Assert.Contains(outputVar, prompt);
    }

    [Fact]
    public void A_percent_sign_in_the_prompt_is_defused_on_windows_so_cmd_cannot_expand_it()
    {
        var invocation = new WorkerInvocation("We hit 100% and referenced %PATH% directly.");

        var target = new ClaudeWorkerAdapter().Resolve(invocation, ArchitectContract);

        var prompt = GetPrompt(target);
        if (OperatingSystem.IsWindows())
        {
            // Confirmed live: an unescaped %PATH% here gets expanded by cmd.exe's own pass over its
            // /c tail -- independent of Rust's argv quoting -- leaking the host's real PATH value
            // into the prompt. Doubling defuses it the same way a batch file would.
            Assert.Contains("We hit 100%% and referenced %%PATH%% directly.", prompt);
        }
        else
        {
            Assert.Contains("We hit 100% and referenced %PATH% directly.", prompt);
        }
    }

    [Fact]
    public void Null_invocation_or_contract_throws()
    {
        var adapter = new ClaudeWorkerAdapter();

        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(null!, ArchitectContract));
        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(new WorkerInvocation("Draft a plan."), null!));
    }
}
