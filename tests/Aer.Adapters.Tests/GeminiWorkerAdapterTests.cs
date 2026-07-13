using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters.Tests;

/// <summary>
/// M12 Phase 1's deliverable (#95): the constructed command / args / prompt string a
/// <see cref="GeminiWorkerAdapter"/> produces for a headless <c>agy</c> invocation, asserted
/// without spawning any real process — live runs are Phase 4's gate. Mirrors
/// <see cref="ClaudeWorkerAdapterTests"/>, plus the <c>--add-dir</c> grant #21 found <c>agy</c>
/// needs that Claude does not.
/// </summary>
public class GeminiWorkerAdapterTests
{
    private static readonly WorkerContract ArchitectContract = new(
        "architect", ["goal"], [new ProducedOutput("plan.md")], []);

    /// <summary>
    /// Windows always puts the prompt at this fixed index (<c>/c agy -p &lt;prompt&gt;</c>),
    /// regardless of whether <c>--model</c> is present later, since <c>--model</c> is only ever
    /// appended after the fixed <c>--mode</c>/<c>--add-dir</c> pair.
    /// </summary>
    private const int WindowsPromptIndex = 3;

    private static string GetPrompt(CoreDispatchTarget target) =>
        OperatingSystem.IsWindows() ? target.Args[WindowsPromptIndex] : target.Args[1];

    [Fact]
    public void Resolves_to_a_shell_wrapper_so_stdin_can_be_redirected()
    {
        var target = new GeminiWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

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
    public void The_command_invokes_agy_with_the_prompt_and_default_permission_scope()
    {
        var target = new GeminiWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("agy", target.Args[1]);
            Assert.Equal("-p", target.Args[2]);
            Assert.Contains("Draft a plan.", target.Args[WindowsPromptIndex]);
            Assert.Contains("--mode", target.Args);
            Assert.Contains("accept-edits", target.Args);
        }
        else
        {
            var commandLine = target.Args[1];
            Assert.Contains("agy -p ", commandLine);
            Assert.Contains("Draft a plan.", commandLine);
            Assert.Contains("--mode \"accept-edits\"", commandLine);
        }
    }

    [Fact]
    public void An_explicit_permission_scope_overrides_the_default()
    {
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", PermissionScope: "yolo"), ArchitectContract);

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("yolo", target.Args);
            Assert.DoesNotContain("accept-edits", target.Args);
        }
        else
        {
            Assert.Contains("--mode \"yolo\"", target.Args[1]);
            Assert.DoesNotContain("\"accept-edits\"", target.Args[1]);
        }
    }

    [Fact]
    public void The_command_grants_the_artifacts_root_directory_since_agy_ignores_cwd()
    {
        var target = new GeminiWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        var artifactsRootVar = OperatingSystem.IsWindows() ? "%AER_ARTIFACTS_ROOT%" : "$AER_ARTIFACTS_ROOT";
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("--add-dir", target.Args);
            Assert.Contains(artifactsRootVar, target.Args);
        }
        else
        {
            Assert.Contains($"--add-dir \"{artifactsRootVar}\"", target.Args[1]);
        }
    }

    [Fact]
    public void A_model_is_passed_through_when_set()
    {
        var target = new GeminiWorkerAdapter().Resolve(
            new WorkerInvocation("Draft a plan.", Model: "gemini-3-pro"), ArchitectContract);

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("--model", target.Args);
            Assert.Contains("gemini-3-pro", target.Args);
        }
        else
        {
            Assert.Contains("--model \"gemini-3-pro\"", target.Args[1]);
        }
    }

    [Fact]
    public void No_model_flag_is_emitted_when_the_model_is_unset()
    {
        var target = new GeminiWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.DoesNotContain("--model", OperatingSystem.IsWindows() ? target.Args : [target.Args[1]]);
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
    public void The_windows_command_line_never_contains_a_raw_newline_but_unix_does()
    {
        var contract = new WorkerContract("architect", ["goal"], [new ProducedOutput("plan.md")], []);
        var target = new GeminiWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), contract);

        if (OperatingSystem.IsWindows())
        {
            Assert.All(target.Args, arg => Assert.DoesNotContain('\n', arg));
        }
        else
        {
            Assert.Contains('\n', target.Args[1]);
        }
    }

    [Fact]
    public void A_prompt_templates_own_shell_metacharacters_are_defused_not_expanded()
    {
        var invocation = new WorkerInvocation("Quote this: \"$HOME\" and `whoami` and a\\backslash.");

        var target = new GeminiWorkerAdapter().Resolve(invocation, ArchitectContract);

        var prompt = GetPrompt(target);
        if (OperatingSystem.IsWindows())
        {
            // Passed as its own argv token: aer-core's Windows spawn (`Command::args`) applies
            // correct Win32 argument quoting to it exactly once, so no manual escaping of a literal
            // quote/backtick/dollar/backslash is needed here.
            Assert.Contains("Quote this: \"$HOME\" and `whoami` and a\\backslash.", prompt);
        }
        else
        {
            // POSIX escaping: '\' -> '\\', '"' -> '\"', '`' -> '\`', '$' -> '\$', applied in that order.
            Assert.Contains("Quote this: \\\"\\$HOME\\\" and \\`whoami\\` and a\\\\backslash.", prompt);
        }

        // The adapter's own AER_ARTIFACTS_ROOT reference (a separate --add-dir argument, not part
        // of the prompt) must still appear unescaped, proving the template's defusal didn't also
        // neutralize the adapter's generated references.
        var artifactsRootVar = OperatingSystem.IsWindows() ? "%AER_ARTIFACTS_ROOT%" : "$AER_ARTIFACTS_ROOT";
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(artifactsRootVar, target.Args);
        }
        else
        {
            Assert.Contains(artifactsRootVar, target.Args[1]);
        }
    }

    [Fact]
    public void A_percent_sign_in_the_prompt_is_defused_on_windows_so_cmd_cannot_expand_it()
    {
        var invocation = new WorkerInvocation("We hit 100% and referenced %PATH% directly.");

        var target = new GeminiWorkerAdapter().Resolve(invocation, ArchitectContract);

        var prompt = GetPrompt(target);
        if (OperatingSystem.IsWindows())
        {
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
        var adapter = new GeminiWorkerAdapter();

        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(null!, ArchitectContract));
        Assert.Throws<ArgumentNullException>(() => adapter.Resolve(new WorkerInvocation("Draft a plan."), null!));
    }
}
