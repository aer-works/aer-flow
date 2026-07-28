namespace Aer.Cli.Tests;

/// <summary>
/// #543: <see cref="HookCheckCommand"/> is the executable target Claude Code spawns directly (exec
/// form, no shell) for every <c>PreToolUse</c> event. These drive <see cref="HookCheckCommand.Execute"/>
/// directly against the exact stdin shape <c>.vendor-survey/corpus/claude__hooks.md</c> documents
/// (<c>{"tool_name": "...", ...}</c>), rather than only asserting against pre-shaped fixtures, so a
/// regression in field-name handling shows up here.
/// </summary>
public class HookCheckCommandTests
{
    [Fact]
    public void A_tool_named_in_the_denied_list_is_blocked_with_exit_code_2()
    {
        using var stdin = new StringReader("""{"tool_name": "Bash", "tool_input": {"command": "ls"}}""");
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(stdin, stderr, "claude:Edit,Write,Bash");

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Contains("Bash", stderr.ToString());
    }

    [Fact]
    public void A_tool_not_named_in_the_denied_list_is_allowed()
    {
        using var stdin = new StringReader("""{"tool_name": "Read", "tool_input": {"file_path": "x"}}""");
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(stdin, stderr, "claude:Edit,Write,Bash");

        Assert.Equal(HookCheckCommand.AllowedExitCode, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_or_blank_denied_list_now_denies_because_the_gate_cannot_know(string? deniedToolsRaw)
    {
        using var stdin = new StringReader("""{"tool_name": "Bash"}""");
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(stdin, stderr, deniedToolsRaw);

        // #600 inverted this deliberately. It used to allow, which meant "AER set the list and nothing
        // is withheld" and "the list never arrived" were the same observable outcome — so a channel
        // that had stopped working looked exactly like one that was. An empty list AER actually sent
        // still allows; it now arrives tagged (`claude:`), which is what makes the two tellable apart.
        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
    }

    [Fact]
    public void Matching_is_exact_not_a_substring_or_prefix_match()
    {
        // "Bash" denied must not accidentally deny "BashOutput" or match on a scoped
        // "Bash(rm *)"-shaped tool_input; BuildDisallowedTools never emits scoped entries, so
        // hook-check has no reason to parse them, but an accidental substring match would silently
        // widen the denial beyond what was actually withheld.
        using var stdin = new StringReader("""{"tool_name": "BashOutput"}""");
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(stdin, stderr, "claude:Bash");

        Assert.Equal(HookCheckCommand.AllowedExitCode, exitCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"tool_name": null}""")]
    [InlineData("[]")]
    public void Unreadable_or_shapeless_stdin_fails_open(string stdinContent)
    {
        using var stdin = new StringReader(stdinContent);
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(stdin, stderr, "claude:Bash,Edit,Write");

        Assert.Equal(HookCheckCommand.AllowedExitCode, exitCode);
    }

    [Fact]
    public void A_null_stdin_reader_throws_rather_than_silently_allowing()
    {
        using var stderr = new StringWriter();

        Assert.Throws<ArgumentNullException>(() => HookCheckCommand.Execute(null!, stderr, "claude:Bash"));
    }
}
