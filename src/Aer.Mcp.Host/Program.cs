using Aer.Mcp;
using Aer.Mcp.Host;

var captureFilePath = ParseArgValue(args, "--capture-file");
var memoryProposalDir = ParseArgValue(args, "--memory-proposal-dir");

List<IMcpTool> tools = [];
if (captureFilePath is not null)
{
    tools.Add(new YieldTool(captureFilePath));
}

if (memoryProposalDir is not null)
{
    tools.Add(new MemoryProposalTool(memoryProposalDir));
}

if (tools.Count == 0)
{
    Console.Error.WriteLine("Usage: Aer.Mcp.Host [--capture-file <path>] [--memory-proposal-dir <path>]");
    return 1;
}

var host = new McpServerHost("aer-mcp-host", "1.0.0", tools);
await host.RunAsync(Console.In, Console.Out).ConfigureAwait(false);
return 0;

static string? ParseArgValue(string[] args, string flag)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == flag)
        {
            return args[i + 1];
        }
    }

    return null;
}
