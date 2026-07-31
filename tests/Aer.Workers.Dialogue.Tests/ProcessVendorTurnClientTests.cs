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

    [Fact]
    public async Task A_substituted_prompt_over_the_oversize_threshold_throws_the_typed_guard_exception()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vendor-turn-client-{Guid.NewGuid():N}");
        try
        {
            var participant = StubVendorScripts.EchoingSuffix(root, "initiator", "claude", "preamble", suffix: "");
            var oversizePrompt = new string('x', ProcessVendorTurnClient.MaxArgumentLength + 1);

            var ex = await Assert.ThrowsAsync<DialogueArgumentTooLargeException>(
                () => new ProcessVendorTurnClient().SendTurnAsync(participant, oversizePrompt));

            Assert.Contains("initiator", ex.Message);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DirectoryCleanup.DeleteRecursively(root);
            }
        }
    }

    /// <summary>The control arm: a prompt right at the threshold (not over it) must not throw.</summary>
    [Fact]
    public async Task A_substituted_prompt_at_the_oversize_threshold_does_not_throw()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vendor-turn-client-{Guid.NewGuid():N}");
        try
        {
            var participant = StubVendorScripts.EchoingSuffix(root, "initiator", "claude", "preamble", suffix: "");
            var atThresholdPrompt = new string('x', ProcessVendorTurnClient.MaxArgumentLength);

            var result = await new ProcessVendorTurnClient().SendTurnAsync(participant, atThresholdPrompt);

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

    /// <summary>
    /// Decision 0039: a claude command's first turn (sessionId null in) mints and passes a fresh id via
    /// --session-id; a resumed turn (sessionId non-null in) passes it back via --resume instead.
    /// Detected by Command's file name, the same as the print-timeout tests above.
    /// </summary>
    [Fact]
    public async Task Claude_command_gets_session_id_on_a_fresh_turn_and_resume_on_a_resumed_turn()
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
                participant = new DialogueParticipant("initiator", "claude", Model: null, "preamble", scriptPath, ["-p", DialogueParticipant.PromptPlaceholder]);
            }
            else
            {
                var shScriptPath = Path.Combine(root, "claude");
                File.WriteAllText(shScriptPath, "#!/bin/sh\necho \"$@\"\n");
                File.SetUnixFileMode(shScriptPath, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                participant = new DialogueParticipant("initiator", "claude", Model: null, "preamble", shScriptPath, ["-p", DialogueParticipant.PromptPlaceholder]);
            }

            var client = new ProcessVendorTurnClient(TimeSpan.FromMinutes(5));

            var fresh = await client.SendTurnAsync(participant, "hello", sessionId: null);
            Assert.Contains("--session-id", fresh.Text);
            Assert.DoesNotContain("--resume", fresh.Text);
            Assert.NotNull(fresh.SessionId);
            Assert.Contains(fresh.SessionId!, fresh.Text);

            var resumed = await client.SendTurnAsync(participant, "hello again", sessionId: fresh.SessionId);
            Assert.Contains($"--resume {fresh.SessionId}", resumed.Text);
            Assert.DoesNotContain("--session-id", resumed.Text);
            Assert.Equal(fresh.SessionId, resumed.SessionId);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DirectoryCleanup.DeleteRecursively(root);
            }
        }
    }

    /// <summary>
    /// Decision 0039: an agy command's first turn (sessionId null in) passes --log-file instead (agy
    /// mints its own id), scraped back afterward; a resumed turn (sessionId non-null in) passes it via
    /// --conversation instead. The stub writes "conversation=&lt;id&gt;" to whatever --log-file path it
    /// is given, mirroring what the daemon's own agy turn loop already scrapes from the real CLI.
    /// </summary>
    [Fact]
    public async Task Agy_command_gets_log_file_on_a_fresh_turn_and_conversation_flag_on_a_resumed_turn()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vendor-turn-client-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            DialogueParticipant participant;
            if (OperatingSystem.IsWindows())
            {
                var scriptPath = Path.Combine(root, "agy.cmd");
                File.WriteAllText(
                    scriptPath,
                    "@echo off\r\n"
                    + "setlocal enabledelayedexpansion\r\n"
                    + "set LOGFILE=\r\n"
                    + ":parse\r\n"
                    + "if \"%~1\"==\"\" goto :afterparse\r\n"
                    + "if \"%~1\"==\"--log-file\" (set LOGFILE=%~2& shift & shift & goto :parse)\r\n"
                    + "shift\r\n"
                    + "goto :parse\r\n"
                    + ":afterparse\r\n"
                    + "if not \"%LOGFILE%\"==\"\" echo conversation=stub-agy-conv-id> \"%LOGFILE%\"\r\n"
                    + "echo %*\r\n");
                participant = new DialogueParticipant("responder", "gemini", Model: null, "preamble", scriptPath, ["-p", DialogueParticipant.PromptPlaceholder]);
            }
            else
            {
                var shScriptPath = Path.Combine(root, "agy");
                File.WriteAllText(
                    shScriptPath,
                    "#!/bin/sh\n"
                    + "args=\"$@\"\n"
                    + "logfile=\"\"\n"
                    + "while [ $# -gt 0 ]; do\n"
                    + "  if [ \"$1\" = \"--log-file\" ]; then logfile=\"$2\"; fi\n"
                    + "  shift\n"
                    + "done\n"
                    + "if [ -n \"$logfile\" ]; then echo \"conversation=stub-agy-conv-id\" > \"$logfile\"; fi\n"
                    + "echo \"$args\"\n");
                File.SetUnixFileMode(shScriptPath, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                participant = new DialogueParticipant("responder", "gemini", Model: null, "preamble", shScriptPath, ["-p", DialogueParticipant.PromptPlaceholder]);
            }

            var client = new ProcessVendorTurnClient(TimeSpan.FromMinutes(5));

            var fresh = await client.SendTurnAsync(participant, "hello", sessionId: null);
            Assert.Contains("--log-file", fresh.Text);
            Assert.DoesNotContain("--conversation", fresh.Text);
            Assert.Equal("stub-agy-conv-id", fresh.SessionId);

            var resumed = await client.SendTurnAsync(participant, "hello again", sessionId: fresh.SessionId);
            Assert.Contains("--conversation stub-agy-conv-id", resumed.Text);
            Assert.DoesNotContain("--log-file", resumed.Text);
            Assert.Equal("stub-agy-conv-id", resumed.SessionId);
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
    public async Task An_unscrapeable_agy_log_yields_a_null_session_and_says_so_on_stderr()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vendor-turn-client-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var originalError = Console.Error;
        var capturedError = new StringWriter();
        Console.SetError(capturedError);
        try
        {
            // A stub agy that accepts --log-file but writes NOTHING into it -- the
            // regex-miss path the silent-fallback used to hide (this branch's review).
            DialogueParticipant participant;
            if (OperatingSystem.IsWindows())
            {
                var scriptPath = Path.Combine(root, "agy.cmd");
                File.WriteAllText(scriptPath, "@echo off\r\necho %*\r\n");
                participant = new DialogueParticipant("responder", "gemini", Model: null, "preamble", scriptPath, ["-p", DialogueParticipant.PromptPlaceholder]);
            }
            else
            {
                var shScriptPath = Path.Combine(root, "agy");
                File.WriteAllText(shScriptPath, "#!/bin/sh\necho \"$@\"\n");
                File.SetUnixFileMode(shScriptPath, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                participant = new DialogueParticipant("responder", "gemini", Model: null, "preamble", shScriptPath, ["-p", DialogueParticipant.PromptPlaceholder]);
            }

            var client = new ProcessVendorTurnClient(TimeSpan.FromMinutes(5));

            var fresh = await client.SendTurnAsync(participant, "hello", sessionId: null);

            Assert.Null(fresh.SessionId);
            Assert.Contains("responder", capturedError.ToString());
            Assert.Contains("starts fresh", capturedError.ToString());
        }
        finally
        {
            Console.SetError(originalError);
            if (Directory.Exists(root))
            {
                DirectoryCleanup.DeleteRecursively(root);
            }
        }
    }

    [Theory]
    [InlineData("--print-timeout", "10s")]
    [InlineData("--print-timeout=10s", null)]
    public async Task Operator_print_timeout_below_turn_timeout_plus_60s_throws_config_exception(string flag, string? val)
    {
        var root = Path.Combine(Path.GetTempPath(), $"vendor-turn-client-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            DialogueParticipant participant;
            var args = val is null ? new[] { flag, "-p", DialogueParticipant.PromptPlaceholder } : new[] { flag, val, "-p", DialogueParticipant.PromptPlaceholder };
            if (OperatingSystem.IsWindows())
            {
                var scriptPath = Path.Combine(root, "agy.cmd");
                File.WriteAllText(scriptPath, "@echo %*\r\n");
                participant = new DialogueParticipant("responder", "gemini", Model: null, "preamble", scriptPath, args);
            }
            else
            {
                var shScriptPath = Path.Combine(root, "agy");
                File.WriteAllText(shScriptPath, "#!/bin/sh\necho \"$@\"\n");
                File.SetUnixFileMode(shScriptPath, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                participant = new DialogueParticipant("responder", "gemini", Model: null, "preamble", shScriptPath, args);
            }

            var client = new ProcessVendorTurnClient(TimeSpan.FromMinutes(5));
            var ex = await Assert.ThrowsAsync<DialogueWorkerConfigException>(
                () => client.SendTurnAsync(participant, "hello"));

            Assert.Contains("--print-timeout", ex.Message);
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

