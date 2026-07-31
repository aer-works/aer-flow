using Aer.Mcp;
using Aer.Mcp.Host;

var captureFilePath = ParseCaptureFilePath(args);
if (captureFilePath is null)
{
    Console.Error.WriteLine("Usage: Aer.Mcp.Host --capture-file <path>");
    return 1;
}

var host = new McpServerHost("aer-mcp-host", "1.0.0", [new YieldTool(captureFilePath)]);
await host.RunAsync(Console.In, Console.Out).ConfigureAwait(false);
return 0;

static string? ParseCaptureFilePath(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--capture-file")
        {
            return args[i + 1];
        }
    }

    return null;
}
