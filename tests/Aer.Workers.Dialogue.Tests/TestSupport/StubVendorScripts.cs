using Aer.Workers.Dialogue;

namespace Aer.Workers.Dialogue.Tests.TestSupport;

/// <summary>
/// Builds a <see cref="DialogueParticipant"/> whose <see cref="DialogueParticipant.Command"/>/
/// <see cref="DialogueParticipant.Args"/> run a tiny cross-platform script standing in for a real
/// vendor CLI — the "stub vendor CLIs" the M17 Phase 2 (#165) skeleton is meant to run against.
/// Mirrors <c>Aer.Flow.Tests.TestSupport.ShellWorkerCommands.FromScript</c>'s script-file approach
/// (a script file, not an inlined command-line string, so the stub's own output can safely contain
/// arbitrary text) and its Windows convention of invoking a written <c>.cmd</c> file via
/// <c>cmd /c &lt;path&gt;</c> rather than executing it directly (Windows cannot spawn a batch file
/// as an executable without going through <c>cmd.exe</c>).
/// </summary>
internal static class StubVendorScripts
{
    /// <summary>A participant whose stub CLI echoes the received prompt followed by <paramref name="suffix"/>.</summary>
    public static DialogueParticipant EchoingSuffix(
        string scriptDirectory, string role, string vendor, string preamble, string suffix)
    {
        Directory.CreateDirectory(scriptDirectory);
        var extension = OperatingSystem.IsWindows() ? ".cmd" : ".sh";
        var scriptPath = Path.Combine(scriptDirectory, $"{Guid.NewGuid():N}{extension}");

        var body = OperatingSystem.IsWindows()
            ? $"@echo off\r\necho %~1{suffix}\r\n"
            : $"#!/bin/sh\necho \"$1{suffix}\"\n";
        File.WriteAllText(scriptPath, body);

        return OperatingSystem.IsWindows()
            ? new DialogueParticipant(role, vendor, Model: null, preamble, "cmd", ["/c", scriptPath, DialogueParticipant.PromptPlaceholder])
            : new DialogueParticipant(role, vendor, Model: null, preamble, "sh", [scriptPath, DialogueParticipant.PromptPlaceholder]);
    }
}
