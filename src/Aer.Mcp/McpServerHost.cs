using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aer.Mcp;

/// <summary>
/// AER's own MCP server host (#585) — the "server" side of a stdio-transport Model Context Protocol
/// server: reads newline-delimited JSON-RPC 2.0 requests from <see cref="RunAsync"/>'s
/// <c>input</c>, dispatches <c>initialize</c>/<c>tools/list</c>/<c>tools/call</c>, and writes
/// newline-delimited JSON-RPC responses to its <c>output</c>. Generic on purpose: this class knows
/// nothing about <c>yield</c> or dialogue — a caller supplies the <see cref="IMcpTool"/> set a given
/// server instance exposes. That split is what lets 0029's later blocking-<c>tools/call</c> mechanism
/// reuse this exact host with a different tool, and is why per-participant attribution (#585's
/// "who called yield is structural, never inferred from text") works: each participant's vendor CLI
/// invocation is wired (via its own <c>--mcp-config</c>/workspace) to spawn its own instance of this
/// host, so the instance that received a call — not anything parsed from a turn's own text — is what
/// identifies the caller.
/// <para>
/// One request in, one line out: this host is a per-invocation stdio server, matching how a vendor
/// CLI itself spawns an MCP server subprocess for the lifetime of one <c>-p</c> turn (see the
/// Aer.Workers.Dialogue wiring) — it does not hold connections open across turns, and exits when its
/// input stream closes.
/// </para>
/// </summary>
public sealed class McpServerHost(string serverName, string serverVersion, IReadOnlyList<IMcpTool> tools)
{
    private const string ProtocolVersion = "2024-11-05";

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        string? line;
        while ((line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? request;
            try
            {
                request = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (request is null)
            {
                continue;
            }

            var method = request["method"]?.GetValue<string>();
            var hasId = request.AsObject().TryGetPropertyValue("id", out var idNode);

            // notifications/* carry no id and expect no response, per JSON-RPC 2.0.
            if (!hasId)
            {
                continue;
            }

            JsonNode result = method switch
            {
                "initialize" => BuildInitializeResult(),
                "tools/list" => BuildToolsListResult(),
                "tools/call" => BuildToolsCallResult(request),
                _ => BuildMethodNotFound(),
            };

            var isError = method is null || (method != "initialize" && method != "tools/list" && method != "tools/call");
            var envelope = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idNode?.DeepClone(),
            };
            envelope[isError ? "error" : "result"] = result;

            await output.WriteLineAsync(envelope.ToJsonString()).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private JsonNode BuildInitializeResult() => new JsonObject
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = serverName, ["version"] = serverVersion },
    };

    private JsonNode BuildToolsListResult()
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = JsonNode.Parse(tool.InputSchemaJson),
            });
        }

        return new JsonObject { ["tools"] = array };
    }

    private JsonNode BuildToolsCallResult(JsonNode request)
    {
        var paramsNode = request["params"];
        var name = paramsNode?["name"]?.GetValue<string>();
        var tool = tools.FirstOrDefault(t => t.Name == name);

        if (tool is null)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"Unknown tool '{name}'." }),
                ["isError"] = true,
            };
        }

        var argumentsNode = paramsNode?["arguments"];
        var argumentsElement = argumentsNode is null
            ? JsonDocument.Parse("{}").RootElement
            : JsonDocument.Parse(argumentsNode.ToJsonString()).RootElement;

        var callResult = tool.Call(argumentsElement);

        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = callResult.Text }),
            ["isError"] = callResult.IsError,
        };
    }

    private static JsonNode BuildMethodNotFound() => new JsonObject
    {
        ["code"] = -32601,
        ["message"] = "Method not found",
    };
}
