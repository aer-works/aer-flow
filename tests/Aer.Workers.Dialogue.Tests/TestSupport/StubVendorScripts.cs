using Aer.Workers.Dialogue;

namespace Aer.Workers.Dialogue.Tests.TestSupport;

/// <summary>
/// Builds a <see cref="DialogueParticipant"/> whose <see cref="DialogueParticipant.Command"/>/
/// <see cref="DialogueParticipant.Args"/> run a tiny cross-platform script standing in for a real
/// vendor CLI — the "stub vendor CLIs" the M17 Phase 2 (#165) skeleton is meant to run against.
/// <para>
/// Windows deliberately does **not** reuse <c>Aer.Flow.Tests.TestSupport.ShellWorkerCommands.FromScript</c>'s
/// <c>cmd /c &lt;path&gt;</c> convention: <c>cmd.exe</c>'s own <c>/c</c> tail parser truncates at an
/// embedded newline even inside a quoted argument (confirmed live in <c>ClaudeWorkerAdapter</c>'s
/// own remarks), and <see cref="Aer.Workers.Dialogue.DialogueRunner"/> genuinely sends multi-line
/// prompts (preamble + blank line + threaded context) — a real prompt this stub must round-trip
/// intact. <c>powershell.exe</c> is a normal argv-based executable, not a line-oriented command
/// reinterpreter, so its <c>-File</c> parameter binding preserves an embedded newline the way any
/// other Win32 process argument does; unix's plain <c>sh &lt;script&gt; &lt;prompt&gt;</c> never had
/// this problem, since POSIX <c>$1</c> is just an argv slot, not a re-parsed command line.
/// </para>
/// </summary>
internal static class StubVendorScripts
{
    /// <summary>A participant whose stub CLI echoes the received prompt followed by <paramref name="suffix"/>.</summary>
    public static DialogueParticipant EchoingSuffix(
        string scriptDirectory, string role, string vendor, string preamble, string suffix)
    {
        Directory.CreateDirectory(scriptDirectory);

        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(scriptDirectory, $"{Guid.NewGuid():N}.ps1");
            File.WriteAllText(scriptPath, $"param([string]$Prompt)\r\nWrite-Output ($Prompt + '{suffix}')\r\n");

            return new DialogueParticipant(
                role, vendor, Model: null, preamble, "powershell",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, DialogueParticipant.PromptPlaceholder]);
        }

        var shScriptPath = Path.Combine(scriptDirectory, $"{Guid.NewGuid():N}.sh");
        File.WriteAllText(shScriptPath, $"#!/bin/sh\necho \"$1{suffix}\"\n");

        return new DialogueParticipant(role, vendor, Model: null, preamble, "sh", [shScriptPath, DialogueParticipant.PromptPlaceholder]);
    }

    /// <summary>A participant whose stub CLI writes <paramref name="stderrText"/> to stderr and exits with <paramref name="exitCode"/>, never writing anything to stdout — the "vendor CLI exits non-zero" failure path (M17 Phase 3, #166).</summary>
    public static DialogueParticipant ExitingWithCode(
        string scriptDirectory, string role, string vendor, string preamble, int exitCode, string stderrText)
    {
        Directory.CreateDirectory(scriptDirectory);

        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(scriptDirectory, $"{Guid.NewGuid():N}.ps1");
            File.WriteAllText(scriptPath, $"param([string]$Prompt)\r\n[Console]::Error.WriteLine('{stderrText}')\r\nexit {exitCode}\r\n");

            return new DialogueParticipant(
                role, vendor, Model: null, preamble, "powershell",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, DialogueParticipant.PromptPlaceholder]);
        }

        var shScriptPath = Path.Combine(scriptDirectory, $"{Guid.NewGuid():N}.sh");
        File.WriteAllText(shScriptPath, $"#!/bin/sh\necho \"{stderrText}\" 1>&2\nexit {exitCode}\n");

        return new DialogueParticipant(role, vendor, Model: null, preamble, "sh", [shScriptPath, DialogueParticipant.PromptPlaceholder]);
    }

    /// <summary>A participant whose stub CLI exits 0 but writes nothing to stdout — the "vendor CLI produces an empty turn" failure path (M17 Phase 3, #166; the walkthrough's recorded <c>agy</c> "clarifying question, no file written" habit, applied to stdout instead of a declared output file).</summary>
    public static DialogueParticipant ProducingEmptyOutput(string scriptDirectory, string role, string vendor, string preamble)
    {
        Directory.CreateDirectory(scriptDirectory);

        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(scriptDirectory, $"{Guid.NewGuid():N}.ps1");
            File.WriteAllText(scriptPath, "param([string]$Prompt)\r\nexit 0\r\n");

            return new DialogueParticipant(
                role, vendor, Model: null, preamble, "powershell",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, DialogueParticipant.PromptPlaceholder]);
        }

        var shScriptPath = Path.Combine(scriptDirectory, $"{Guid.NewGuid():N}.sh");
        File.WriteAllText(shScriptPath, "#!/bin/sh\nexit 0\n");

        return new DialogueParticipant(role, vendor, Model: null, preamble, "sh", [shScriptPath, DialogueParticipant.PromptPlaceholder]);
    }
}
