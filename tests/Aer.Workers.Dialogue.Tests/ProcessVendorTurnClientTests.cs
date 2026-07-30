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

    [Fact]
    public async Task Kills_child_process_exceeding_turn_timeout_and_returns_named_timeout_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vendor-turn-client-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            DialogueParticipant participant;
            if (OperatingSystem.IsWindows())
            {
                var scriptPath = Path.Combine(root, $"{Guid.NewGuid():N}.ps1");
                File.WriteAllText(scriptPath, "param([string]$Prompt)\r\nStart-Sleep -Seconds 5\r\nWrite-Output 'done'\r\n");
                participant = new DialogueParticipant(
                    "initiator", "claude", Model: null, "preamble", "powershell",
                    ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, DialogueParticipant.PromptPlaceholder]);
            }
            else
            {
                var shScriptPath = Path.Combine(root, $"{Guid.NewGuid():N}.sh");
                File.WriteAllText(shScriptPath, "#!/bin/sh\nsleep 5\necho \"done\"\n");
                participant = new DialogueParticipant("initiator", "claude", Model: null, "preamble", "sh", [shScriptPath, DialogueParticipant.PromptPlaceholder]);
            }

            var client = new ProcessVendorTurnClient(TimeSpan.FromMilliseconds(200));
            var result = await client.SendTurnAsync(participant, "hi there");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Turn timed out", result.StandardError);
            Assert.Contains("00:00:00.2000000", result.StandardError);
            Assert.Contains("initiator", result.StandardError);
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
    public async Task Agy_participant_without_operator_flag_gets_derived_print_timeout_appended()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vendor-turn-client-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            DialogueParticipant participant;
            if (OperatingSystem.IsWindows())
            {
                var scriptPath = Path.Combine(root, "agy.cmd");
                File.WriteAllText(scriptPath, "@echo %*\r\n");
                participant = new DialogueParticipant(
                    "responder", "gemini", Model: null, "preamble", scriptPath,
                    ["-p", DialogueParticipant.PromptPlaceholder]);
            }
            else
            {
                var shScriptPath = Path.Combine(root, "agy");
                File.WriteAllText(shScriptPath, "#!/bin/sh\necho \"$@\"\n");
                File.SetUnixFileMode(shScriptPath, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                participant = new DialogueParticipant("responder", "gemini", Model: null, "preamble", shScriptPath, ["-p", DialogueParticipant.PromptPlaceholder]);
            }

            var client = new ProcessVendorTurnClient(TimeSpan.FromMinutes(5));
            var result = await client.SendTurnAsync(participant, "hello");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("--print-timeout 360s", result.Text);
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
    public async Task Agy_participant_with_operator_flag_is_not_overridden()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vendor-turn-client-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            DialogueParticipant participant;
            if (OperatingSystem.IsWindows())
            {
                var scriptPath = Path.Combine(root, "agy.cmd");
                File.WriteAllText(scriptPath, "@echo %*\r\n");
                participant = new DialogueParticipant(
                    "responder", "gemini", Model: null, "preamble", scriptPath,
                    ["--print-timeout", "10m", "-p", DialogueParticipant.PromptPlaceholder]);
            }
            else
            {
                var shScriptPath = Path.Combine(root, "agy");
                File.WriteAllText(shScriptPath, "#!/bin/sh\necho \"$@\"\n");
                File.SetUnixFileMode(shScriptPath, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                participant = new DialogueParticipant("responder", "gemini", Model: null, "preamble", shScriptPath, ["--print-timeout", "10m", "-p", DialogueParticipant.PromptPlaceholder]);
            }

            var client = new ProcessVendorTurnClient(TimeSpan.FromMinutes(5));
            var result = await client.SendTurnAsync(participant, "hello");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("--print-timeout 10m", result.Text);
            Assert.DoesNotContain("360s", result.Text);
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
    public async Task Non_agy_participant_gets_nothing_appended()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vendor-turn-client-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            DialogueParticipant participant;
            if (OperatingSystem.IsWindows())
            {
                var scriptPath = Path.Combine(root, "claude.cmd");
                File.WriteAllText(scriptPath, "@echo %*\r\n");
                participant = new DialogueParticipant(
                    "initiator", "claude", Model: null, "preamble", scriptPath,
                    ["-p", DialogueParticipant.PromptPlaceholder]);
            }
            else
            {
                var shScriptPath = Path.Combine(root, "claude");
                File.WriteAllText(shScriptPath, "#!/bin/sh\necho \"$@\"\n");
                File.SetUnixFileMode(shScriptPath, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                participant = new DialogueParticipant("initiator", "claude", Model: null, "preamble", shScriptPath, ["-p", DialogueParticipant.PromptPlaceholder]);
            }

            var client = new ProcessVendorTurnClient(TimeSpan.FromMinutes(5));
            var result = await client.SendTurnAsync(participant, "hello");

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("--print-timeout", result.Text);
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
