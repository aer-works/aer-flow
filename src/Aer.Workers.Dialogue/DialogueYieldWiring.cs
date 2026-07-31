using System.Text.Json;
using Aer.Mcp.Host;

namespace Aer.Workers.Dialogue;

/// <summary>
/// Wires each dialogue participant to its own instance of <c>Aer.Mcp.Host</c>'s stdio MCP server so a
/// vendor CLI's <c>yield</c> tool call can be read back after the turn's process exits (#585, decision
/// 0035). One participant's wiring never reaches another's: each gets its own capture-file path, keyed
/// by a sanitized version of <see cref="DialogueParticipant.Role"/>, so which participant is credited
/// with a yield call is structural — baked into which participant's own config the capture path was
/// wired into — never inferred from a turn's own text.
/// </summary>
internal static class DialogueYieldWiring
{
    public readonly record struct WiredParticipant(DialogueParticipant Participant, string CaptureFilePath);

    /// <summary>
    /// Computes one <see cref="WiredParticipant"/> per entry in <paramref name="participants"/>, in the
    /// same order, before any turn runs — a participant's wiring (its capture file path, and for
    /// claude/agy, its MCP config file) is fixed for the whole exchange, not rebuilt per turn.
    /// </summary>
    public static IReadOnlyList<WiredParticipant> Wire(IReadOnlyList<DialogueParticipant> participants, string outputDirectory)
    {
        var hostDllPath = Path.Combine(AppContext.BaseDirectory, "Aer.Mcp.Host.dll");
        var wired = new WiredParticipant[participants.Count];
        var seenSlugs = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < participants.Count; i++)
        {
            var participant = participants[i];
            var slug = SanitizeForFileName(participant.Role);
            if (!seenSlugs.Add(slug))
            {
                // Two participants sharing a Role (or two Roles that sanitize to the same slug) would
                // otherwise share one capture file path, making a yield call from either
                // indistinguishable from the other's -- exactly the attribution guarantee this
                // mechanism exists to provide.
                throw new DialogueWorkerConfigException(
                    $"Two or more participants share the role '{participant.Role}' (or sanitize to the same file-name slug '{slug}') -- yield attribution requires every participant's role to be distinct.");
            }

            var captureFilePath = Path.Combine(outputDirectory, $"yield-capture-{slug}.json");
            wired[i] = new WiredParticipant(WireOne(participant, slug, outputDirectory, hostDllPath, captureFilePath), captureFilePath);
        }

        return wired;
    }

    /// <summary>
    /// Reads and deletes <paramref name="captureFilePath"/> if a yield call landed there. Deleting it
    /// immediately after reading is what keeps a capture left by a prior turn of the same participant
    /// from ever being misread as a later turn's call — the file is known-absent again the instant it's
    /// consumed.
    /// </summary>
    public static YieldCapture? ReadAndConsumeCapture(string captureFilePath)
    {
        if (!File.Exists(captureFilePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(captureFilePath);
            return JsonSerializer.Deserialize<YieldCapture>(json);
        }
        finally
        {
            File.Delete(captureFilePath);
        }
    }

    private static DialogueParticipant WireOne(
        DialogueParticipant participant, string slug, string outputDirectory, string hostDllPath, string captureFilePath)
    {
        if (IsClaudeCommand(participant.Command))
        {
            var mcpConfigPath = Path.Combine(outputDirectory, $"mcp-config-{slug}.json");
            WriteMcpConfig(mcpConfigPath, hostDllPath, captureFilePath);
            return participant with
            {
                Args = [.. participant.Args, "--mcp-config", mcpConfigPath, "--strict-mcp-config"],
            };
        }

        if (IsAgyCommand(participant.Command))
        {
            // agy has no per-invocation flag equivalent to claude's --mcp-config, so a real workspace
            // directory has to exist on disk for --add-dir to point at (docs/vendor-doc-audit.md's agy
            // MCP findings; decision 0035).
            var workspaceDir = Path.Combine(outputDirectory, $"workspace-{slug}");
            var agentsDir = Path.Combine(workspaceDir, ".agents");
            Directory.CreateDirectory(agentsDir);
            WriteMcpConfig(Path.Combine(agentsDir, "mcp_config.json"), hostDllPath, captureFilePath);
            return participant with { Args = [.. participant.Args, "--add-dir", workspaceDir] };
        }

        // A test-stub command (e.g. this project's stub CLIs, or ProcessVendorTurnClientTests'
        // fixtures) gets no MCP wiring, and its capture file is never checked for it.
        return participant;
    }

    private static void WriteMcpConfig(string configPath, string hostDllPath, string captureFilePath)
    {
        var json = JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["aer-yield"] = new { command = "dotnet", args = new[] { hostDllPath, "--capture-file", captureFilePath } },
            },
        });

        File.WriteAllText(configPath, json);
    }

    private static bool IsClaudeCommand(string command) => CommandNameEquals(command, "claude");

    private static bool IsAgyCommand(string command) => CommandNameEquals(command, "agy");

    private static bool CommandNameEquals(string command, string name)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        return string.Equals(Path.GetFileNameWithoutExtension(command), name, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeForFileName(string role)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = role.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
