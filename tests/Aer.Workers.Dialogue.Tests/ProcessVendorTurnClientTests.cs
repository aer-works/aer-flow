using Aer.Workers.Dialogue;
using Aer.Workers.Dialogue.Tests.TestSupport;

namespace Aer.Workers.Dialogue.Tests;

public class ProcessVendorTurnClientTests
{
    [Fact]
    public async Task Prompt_text_containing_literal_placeholder_syntax_passes_through_unmodified()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vendor-turn-client-{Guid.NewGuid():N}");
        try
        {
            var participant = StubVendorScripts.EchoingSuffix(root, "initiator", "claude", "preamble", suffix: "");

            var result = await new ProcessVendorTurnClient().SendTurnAsync(
                participant, "hello {PROMPT} world, {PROMPT} again");

            Assert.Equal("hello {PROMPT} world, {PROMPT} again", result.Text);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DirectoryCleanup.DeleteRecursively(root);
            }
        }
    }

    [Fact]
    public async Task Captures_stdout_trimmed_of_a_trailing_newline()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vendor-turn-client-{Guid.NewGuid():N}");
        try
        {
            var participant = StubVendorScripts.EchoingSuffix(root, "initiator", "claude", "preamble", suffix: "");

            var result = await new ProcessVendorTurnClient().SendTurnAsync(participant, "hi there");

            Assert.Equal("hi there", result.Text);
            Assert.DoesNotContain('\n', result.Text);
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DirectoryCleanup.DeleteRecursively(root);
            }
        }
    }

    [Fact]
    public async Task Captures_a_non_zero_exit_code_and_stderr()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vendor-turn-client-{Guid.NewGuid():N}");
        try
        {
            var participant = StubVendorScripts.ExitingWithCode(root, "initiator", "claude", "preamble", exitCode: 3, stderrText: "vendor CLI blew up");

            var result = await new ProcessVendorTurnClient().SendTurnAsync(participant, "hi there");

            Assert.Equal(3, result.ExitCode);
            Assert.Contains("vendor CLI blew up", result.StandardError);
            Assert.Equal("", result.Text);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DirectoryCleanup.DeleteRecursively(root);
            }
        }
    }

    [Fact]
    public async Task Captures_an_empty_stdout_with_a_clean_exit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vendor-turn-client-{Guid.NewGuid():N}");
        try
        {
            var participant = StubVendorScripts.ProducingEmptyOutput(root, "initiator", "claude", "preamble");

            var result = await new ProcessVendorTurnClient().SendTurnAsync(participant, "hi there");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("", result.Text);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DirectoryCleanup.DeleteRecursively(root);
            }
        }
    }
}
