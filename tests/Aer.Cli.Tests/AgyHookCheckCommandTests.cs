using System.Text.Json;

namespace Aer.Cli.Tests;

/// <summary>
/// #554: <see cref="AgyHookCheckCommand"/> is the executable target <c>agy</c> spawns for every
/// matched <c>PreToolUse</c> event. These drive <see cref="AgyHookCheckCommand.Execute"/> against
/// the exact stdin shape the live CLI produces — captured by
/// <c>agy.hook-env-inherited</c> in <c>tools/vendor-verify/verify.py</c>, which logs the real
/// payload — rather than a hand-shaped fixture, so a regression in field handling surfaces here.
/// </summary>
/// <remarks>
/// <b>Every assertion below checks the parsed <c>decision</c> field, never the exit code.</b> On agy
/// the exit code carries no gating meaning; the verdict is a JSON object on stdout, and
/// <c>agy.hook-malformed-stdout-fails-open</c> measured that output agy cannot parse — or no output
/// at all — is read as an <b>allow</b>. A test asserting on an exit code would pass while the gate
/// silently let everything through, which is the failure this suite exists to catch.
/// <para>
/// The polarity pairs are deliberate (gate 2): a denied tool blocked and a granted tool allowed, on
/// the same payload shape and the same denied list, so a mechanism that denies (or allows)
/// unconditionally cannot pass both.
/// </para>
/// </remarks>
public class AgyHookCheckCommandTests
{
    /// <summary>
    /// The real payload agy sends, from the live capture in <c>agy.hook-env-inherited</c>'s log.
    /// Note <c>toolCall.name</c> nested and camelCase — claude's is a root-level <c>tool_name</c> —
    /// and the undocumented <c>modelName</c> field (recorded in <c>docs/vendor-doc-audit.md</c>),
    /// present here so a parser that trips over unexpected fields fails in this suite.
    /// </summary>
    private static string Payload(string toolName) => $$"""
        {"artifactDirectoryPath":"C:/x/brain/abc","conversationId":"abc",
         "modelName":"gemini-3.6-flash-medium","stepIdx":3,
         "toolCall":{"args":{"CommandLine":"node --version","Cwd":"C:\\x","WaitMsBeforeAsync":5000},
                     "name":"{{toolName}}"},
         "transcriptPath":"C:/x/transcript_full.jsonl","workspacePaths":["C:/x"]}
        """;

    private static string Decide(string stdinText, string? denied)
    {
        using var stdin = new StringReader(stdinText);
        using var stdout = new StringWriter();

        var exitCode = AgyHookCheckCommand.Execute(stdin, stdout, denied);

        Assert.Equal(AgyHookCheckCommand.ExitCode, exitCode);

        // Parsing rather than substring-matching is the point: agy parses this, and output that
        // merely *contains* the word "deny" while being invalid JSON is an allow.
        var raw = stdout.ToString();
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("decision").GetString()!;
    }

    [Fact]
    public void A_tool_named_in_the_denied_list_is_denied()
    {
        Assert.Equal("deny", Decide(Payload("run_command"), "run_command,manage_task"));
    }

    [Fact]
    public void A_tool_not_named_in_the_denied_list_is_allowed()
    {
        // Same payload shape and same denied list as the deny case above — only the tool name
        // differs, so neither result can come from a mechanism that ignores the input.
        Assert.Equal("allow", Decide(Payload("view_file"), "run_command,manage_task"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_or_blank_denied_list_allows_every_tool(string? denied)
    {
        // A known-empty grant withholds nothing, which is different from being unable to determine
        // what is withheld — the cases below deny for exactly that reason.
        Assert.Equal("allow", Decide(Payload("run_command"), denied));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"toolCall":{}}""")]
    [InlineData("""{"toolCall":"not-an-object"}""")]
    [InlineData("""{"tool_name":"run_command"}""")]
    [InlineData("""{"toolCall":{"name":""}}""")]
    public void Input_it_cannot_judge_is_denied_never_allowed(string stdinText)
    {
        // The core of #554 and the opposite of HookCheckCommand's claude-side posture. claude has
        // --disallowedTools independently covering the same names, so failing open there is "no
        // worse than what exists". agy has no such flag (agy.permissions-are-global-only, decision
        // 0029): this hook is the only per-worker gate, so anything it cannot judge must be denied.
        //
        // `{"tool_name":"run_command"}` is in this list deliberately: that is claude's payload
        // shape, and it must NOT be understood here. If a future refactor merged the two commands,
        // this case would start returning "allow" (claude's field, agy's fail-open) and this test
        // is what would catch it.
        Assert.Equal("deny", Decide(stdinText, "run_command,manage_task"));
    }

    [Fact]
    public void A_denial_reason_names_the_tool_so_the_model_is_told_what_was_withheld()
    {
        using var stdin = new StringReader(Payload("run_command"));
        using var stdout = new StringWriter();

        AgyHookCheckCommand.Execute(stdin, stdout, "run_command");

        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Contains("run_command", doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void A_failure_to_read_stdin_is_denied_rather_than_allowed()
    {
        // The one path that cannot be reached by feeding text in: a reader that throws. Without
        // this arm the IOException branch is untested, and it is precisely the branch where a
        // crash-to-allow would be invisible.
        using var stdout = new StringWriter();

        var exitCode = AgyHookCheckCommand.Execute(new ThrowingReader(), stdout, "run_command");

        Assert.Equal(AgyHookCheckCommand.ExitCode, exitCode);
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("deny", doc.RootElement.GetProperty("decision").GetString());
    }

    [Theory]
    [InlineData("browser_navigate", "deny")]
    [InlineData("browser_click", "deny")]
    [InlineData("browser", "allow")]          // the bare prefix without the separator is not a match
    [InlineData("view_file", "allow")]
    public void A_trailing_star_entry_withholds_a_whole_tool_family(string toolName, string expected)
    {
        // agy's corpus offers `browser_.*` as a matcher example -- "Match any tool starting with
        // browser_" -- while enumerating no such tools, so the family cannot be listed by name. The
        // allow rows are the polarity control: a prefix matcher that matched everything would pass
        // the deny rows alone.
        Assert.Equal(expected, Decide(Payload(toolName), "browser_*,search_web"));
    }

    [Fact]
    public void A_bare_star_does_not_deny_everything_by_accident()
    {
        // Guards the prefix implementation's edge: `entry.Length > 1` means a lone "*" is not
        // treated as a match-all prefix. If it ever were, an adapter bug emitting "*" would silently
        // withhold every tool and break every worker -- loudly, but for a baffling reason.
        Assert.Equal("allow", Decide(Payload("view_file"), "*"));
    }

    [Fact]
    public void The_denied_tools_variable_matches_the_adapter_side_contract()
    {
        // Aer.Adapters cannot reference Aer.Cli, so the variable name is a plain string contract
        // mirrored on both sides. Each side asserts the literal in its own suite; if they drift,
        // the hook reads an empty list, treats it as "nothing withheld", and allows everything.
        Assert.Equal("AER_HOOK_DENIED_TOOLS", AgyHookCheckCommand.DeniedToolsEnvironmentVariable);
    }

    private sealed class ThrowingReader : TextReader
    {
        public override string ReadToEnd() => throw new IOException("simulated pipe failure");
    }
}
