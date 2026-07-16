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

            var text = await new ProcessVendorTurnClient().SendTurnAsync(
                participant, "hello {PROMPT} world, {PROMPT} again");

            Assert.Equal("hello {PROMPT} world, {PROMPT} again", text);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
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

            var text = await new ProcessVendorTurnClient().SendTurnAsync(participant, "hi there");

            Assert.Equal("hi there", text);
            Assert.DoesNotContain('\n', text);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
