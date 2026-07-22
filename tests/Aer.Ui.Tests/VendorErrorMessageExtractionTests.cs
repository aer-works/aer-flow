using Aer.Daemon;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// Unit coverage for <see cref="DaemonHost.TryExtractVendorErrorMessage"/> (#285): a failed chat
/// turn's stderr never reaches the daemon (aer-core's P/Invoke boundary doesn't surface it), but a
/// failed `claude --output-format stream-json` call prints its error as the final stdout line
/// before exiting non-zero. Confirmed live by replaying a real broken session's actual args
/// (`--resume` of an unestablished id) against the installed `claude` CLI directly -- it produced
/// exactly the <c>{"type":"result","is_error":true,"errors":[...]}</c> shape asserted below.
/// </summary>
public class VendorErrorMessageExtractionTests
{
    [Fact]
    public void ExtractsTheErrorsArrayFromARealResultLine()
    {
        var rawStdout = """
            {"type":"system","subtype":"init","session_id":"abc"}
            {"type":"result","subtype":"error_during_execution","is_error":true,"errors":["No conversation found with session ID: 4b195030-5ee6-4abb-a842-55036787dfaf"]}
            """;

        var message = DaemonHost.TryExtractVendorErrorMessage(rawStdout);

        Assert.Equal("No conversation found with session ID: 4b195030-5ee6-4abb-a842-55036787dfaf", message);
    }

    [Fact]
    public void JoinsMultipleErrorsWhenPresent()
    {
        var rawStdout = """{"type":"result","is_error":true,"errors":["first problem","second problem"]}""";

        var message = DaemonHost.TryExtractVendorErrorMessage(rawStdout);

        Assert.Equal("first problem; second problem", message);
    }

    [Fact]
    public void FallsBackToTheResultTextWhenNoErrorsArrayIsPresent()
    {
        var rawStdout = """{"type":"result","is_error":true,"result":"something went wrong"}""";

        var message = DaemonHost.TryExtractVendorErrorMessage(rawStdout);

        Assert.Equal("something went wrong", message);
    }

    [Fact]
    public void IgnoresNonResultJsonLinesAndScansBackward()
    {
        var rawStdout = """
            {"type":"system","subtype":"init"}
            {"type":"stream_event","event":{"type":"message_start"}}
            {"type":"result","is_error":true,"errors":["the real error"]}
            """;

        var message = DaemonHost.TryExtractVendorErrorMessage(rawStdout);

        Assert.Equal("the real error", message);
    }

    [Fact]
    public void FallsBackToTheLastLineWhenNothingParsesAsAResultObject()
    {
        var rawStdout = "not json at all\nsecond line";

        var message = DaemonHost.TryExtractVendorErrorMessage(rawStdout);

        Assert.Equal("second line", message);
    }

    [Fact]
    public void FallsBackToAGenericMessageWhenNoOutputWasCapturedAtAll()
    {
        var message = DaemonHost.TryExtractVendorErrorMessage("");

        Assert.Equal("The vendor process exited without producing a response.", message);
    }
}
