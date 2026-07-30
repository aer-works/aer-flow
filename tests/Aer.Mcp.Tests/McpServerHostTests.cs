using System.Text;
using Aer.Mcp;
using Aer.Mcp.Host;

namespace Aer.Mcp.Tests;

public class McpServerHostTests
{
    [Fact]
    public async Task Initialize_ReturnsProtocolVersionAndServerInfo()
    {
        var responses = await RunAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");

        Assert.Single(responses);
        Assert.Equal("2024-11-05", (string)responses[0]["result"]!["protocolVersion"]!);
        Assert.Equal("aer-mcp-host-test", (string)responses[0]["result"]!["serverInfo"]!["name"]!);
    }

    [Fact]
    public async Task ToolsList_ReturnsRegisteredTool()
    {
        var responses = await RunAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}");

        var tools = responses[0]["result"]!["tools"]!.AsArray();
        Assert.Single(tools);
        Assert.Equal("yield", (string)tools[0]!["name"]!);
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_ReturnsIsError()
    {
        var responses = await RunAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"nope\",\"arguments\":{}}}");

        Assert.True((bool)responses[0]["result"]!["isError"]!);
    }

    [Fact]
    public async Task ToolsCall_KnownTool_InvokesToolAndReturnsContent()
    {
        var captureFile = Path.Combine(Path.GetTempPath(), $"aer-mcp-test-{Guid.NewGuid():N}.json");
        try
        {
            var host = new McpServerHost("aer-mcp-host-test", "1.0.0", [new YieldTool(captureFile)]);
            var input = new StringReader(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"yield\",\"arguments\":{\"outcome\":\"concluded\"}}}\n");
            var output = new StringWriter();

            await host.RunAsync(input, output);

            Assert.True(File.Exists(captureFile));
            Assert.Contains("concluded", await File.ReadAllTextAsync(captureFile));
            Assert.Contains("Recorded yield", output.ToString());
        }
        finally
        {
            if (File.Exists(captureFile))
            {
                File.Delete(captureFile);
            }
        }
    }

    [Fact]
    public async Task Notification_NoId_ProducesNoResponse()
    {
        var responses = await RunAsync(
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}");

        Assert.Empty(responses);
    }

    private static async Task<List<System.Text.Json.Nodes.JsonObject>> RunAsync(string requestLine)
    {
        var host = new McpServerHost("aer-mcp-host-test", "1.0.0", [new YieldTool(Path.GetTempFileName())]);
        var input = new StringReader(requestLine + "\n");
        var output = new StringWriter();

        await host.RunAsync(input, output);

        var text = output.ToString();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Select(l => System.Text.Json.Nodes.JsonNode.Parse(l)!.AsObject()).ToList();
    }
}
