using System.Text.Json;
using Aer.Workers.Dialogue.Tests.TestSupport;
using Aer.Workers.Dialogue;

namespace Aer.Workers.Dialogue.Tests;

public class DialogueYieldWiringTests
{
    [Fact]
    public void A_claude_participant_gets_mcp_config_and_strict_mcp_config_flags()
    {
        var outputDirectory = CreateTempDir();
        try
        {
            var participant = new DialogueParticipant("initiator", "claude", null, "preamble", "claude", ["-p", "{PROMPT_FILE}"]);

            var wired = DialogueYieldWiring.Wire([participant], outputDirectory);

            Assert.Single(wired);
            var args = wired[0].Participant.Args.ToList();
            Assert.Contains("--mcp-config", args);
            Assert.Contains("--strict-mcp-config", args);

            var mcpConfigPath = args[args.IndexOf("--mcp-config") + 1];
            Assert.True(File.Exists(mcpConfigPath));

            using var doc = JsonDocument.Parse(File.ReadAllText(mcpConfigPath));
            var server = doc.RootElement.GetProperty("mcpServers").GetProperty("aer-yield");
            Assert.Equal("dotnet", server.GetProperty("command").GetString());
            var serverArgs = server.GetProperty("args").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Contains("--capture-file", serverArgs);
            Assert.Equal(wired[0].CaptureFilePath, serverArgs[serverArgs.IndexOf("--capture-file") + 1]);
            Assert.EndsWith("Aer.Mcp.Host.dll", serverArgs[0], StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public void An_agy_participant_gets_a_workspace_with_agents_mcp_config_and_add_dir()
    {
        var outputDirectory = CreateTempDir();
        try
        {
            var participant = new DialogueParticipant("responder", "agy", null, "preamble", "agy", ["-p", "{PROMPT_FILE}"]);

            var wired = DialogueYieldWiring.Wire([participant], outputDirectory);

            var args = wired[0].Participant.Args.ToList();
            Assert.Contains("--add-dir", args);
            var workspaceDir = args[args.IndexOf("--add-dir") + 1];
            Assert.True(Directory.Exists(workspaceDir));

            var mcpConfigPath = Path.Combine(workspaceDir, ".agents", "mcp_config.json");
            Assert.True(File.Exists(mcpConfigPath));

            using var doc = JsonDocument.Parse(File.ReadAllText(mcpConfigPath));
            var server = doc.RootElement.GetProperty("mcpServers").GetProperty("aer-yield");
            var serverArgs = server.GetProperty("args").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal(wired[0].CaptureFilePath, serverArgs[serverArgs.IndexOf("--capture-file") + 1]);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public void A_non_vendor_command_is_passed_through_with_no_mcp_wiring()
    {
        var outputDirectory = CreateTempDir();
        try
        {
            var participant = new DialogueParticipant("initiator", "stub-claude", null, "preamble", "stub-claude", ["{PROMPT}"]);

            var wired = DialogueYieldWiring.Wire([participant], outputDirectory);

            Assert.Equal(["{PROMPT}"], wired[0].Participant.Args);
            Assert.Same(participant, wired[0].Participant);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public void Two_participants_sharing_a_role_throws_instead_of_colliding_on_one_capture_file()
    {
        var outputDirectory = CreateTempDir();
        try
        {
            var participants = new List<DialogueParticipant>
            {
                new("initiator", "claude", null, "a", "claude", ["-p", "{PROMPT_FILE}"]),
                new("initiator", "agy", null, "b", "agy", ["-p", "{PROMPT_FILE}"]),
            };

            Assert.Throws<DialogueWorkerConfigException>(() => DialogueYieldWiring.Wire(participants, outputDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    [Fact]
    public void A_malformed_capture_file_throws_the_typed_execution_exception_and_is_still_consumed()
    {
        var outputDirectory = CreateTempDir();
        try
        {
            var captureFilePath = Path.Combine(outputDirectory, "yield-capture.json");
            File.WriteAllText(captureFilePath, "{ not json");

            // Loud and typed, never a raw JsonException out of the runner (second-reader finding
            // on #585's wiring) -- and consumed either way, so a corrupt file cannot re-fail
            // every subsequent turn.
            var ex = Assert.Throws<DialogueExecutionException>(
                () => DialogueYieldWiring.ReadAndConsumeCapture(captureFilePath));
            Assert.Contains("malformed JSON", ex.Message);
            Assert.False(File.Exists(captureFilePath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outputDirectory);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dialogue-yield-wiring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
