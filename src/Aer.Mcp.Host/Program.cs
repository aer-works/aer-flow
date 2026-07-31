using Aer.Mcp;
using Aer.Mcp.Host;

var captureFilePath = ParseArgValue(args, "--capture-file");
// #833: no literal path arrives on the command line -- the MCP config file naming this process is
// written once per worker-binding entry, before any execution's AER_OUTPUT_DIR exists (the seam
// #801 and #833 both record), so nothing resolved at that time can vary per execution. This
// process is spawned as a child of the vendor CLI, which is itself spawned per execution with
// AER_OUTPUT_DIR already in its environment (Aer.Flow.Artifacts.ArtifactManager.BuildEnvironment) --
// a child process inherits that, the same fact Aer.Cli.Program's "hook-check"/"agy-hook-check"
// branches already rest on for the identical reason.
var enableMemoryProposalTool = args.Contains("--memory-proposal-tool");

List<IMcpTool> tools = [];
if (captureFilePath is not null)
{
    tools.Add(new YieldTool(captureFilePath));
}

if (enableMemoryProposalTool)
{
    var outputDirectory = Environment.GetEnvironmentVariable("AER_OUTPUT_DIR");
    if (string.IsNullOrEmpty(outputDirectory))
    {
        Console.Error.WriteLine(
            "--memory-proposal-tool requires AER_OUTPUT_DIR in this process's environment (set per " +
            "execution and inherited from the spawning vendor CLI); none was found.");
        return 1;
    }

    tools.Add(new MemoryProposalTool(Path.Combine(outputDirectory, MemoryProposalTool.CaptureDirectoryName)));
}

if (tools.Count == 0)
{
    Console.Error.WriteLine("Usage: Aer.Mcp.Host [--capture-file <path>] [--memory-proposal-tool]");
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
