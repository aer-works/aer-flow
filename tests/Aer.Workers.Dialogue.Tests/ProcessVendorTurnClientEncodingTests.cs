using System.Diagnostics;
using System.Text;
using Aer.Workers.Dialogue;

namespace Aer.Workers.Dialogue.Tests;

/// <summary>
/// #466's Conversation-tab half. A participant CLI writes UTF-8 to the redirected pipe; the reader
/// must decode UTF-8. With no <c>StandardOutputEncoding</c> set, Windows decodes the pipe with the
/// console OEM codepage instead — cp437 turns <c>—</c> (<c>e2 80 94</c>) into <c>ΓÇö</c>, the exact
/// signature the operator reported from the Conversation tab. Proven the same way
/// <c>CapturedOutputEncodingEndToEndTests</c> pins the engine half: python emits a fixture's exact
/// bytes (no shell in between to transcode them first), and re-encoding the returned turn text must
/// reproduce that byte sequence. The mechanism itself was reproduced in-process during diagnosis:
/// SetConsoleOutputCP(437) + a null-encoding Process read of these very bytes yielded
/// <c>ΓÇö Γåö</c>.
/// <para>
/// Scope of what this test can catch, measured not assumed: it went green against the UNFIXED
/// client both normally and under a <c>chcp 437</c> parent console, because the test host is
/// spawned windowless and the null-encoding decode then falls back to UTF-8 there. So this test
/// discriminates only where the ambient console code page differs from UTF-8 — a real operator
/// console — and the always-firing regression net is the source-level check
/// <c>Aer.Architecture.Tests.RedirectedProcessEncodingTests</c>, whose red against the unfixed
/// tree was proven instead.
/// </para>
/// </summary>
public class ProcessVendorTurnClientEncodingTests
{
    [Fact]
    public async Task A_turns_raw_utf8_stdout_bytes_round_trip_exactly_through_the_client()
    {
        if (!IsPythonAvailable())
        {
            Assert.Skip("python is not on PATH");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"dialogue-utf8-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);

            // The #466 report's own characters (em dash, arrow), plus a 2-byte sequence (§), a
            // 3-byte check mark, and a non-BMP 4-byte emoji so every UTF-8 width is under test.
            byte[] fixtureBytes = "Me ↔ you: — check✓ section§ rocket🚀 end"u8.ToArray();
            var fixturePath = Path.Combine(root, "fixture.bin");
            await File.WriteAllBytesAsync(fixturePath, fixtureBytes, TestContext.Current.CancellationToken);

            // Raw bytes straight to the stdout pipe — no shell echo whose own codepage would
            // corrupt the fixture before the client ever reads it (the trap the #466 instrument
            // notes recorded for cmd.exe, and powershell shares it).
            var scriptPath = Path.Combine(root, "emit.py");
            var fixtureForPython = fixturePath.Replace('\\', '/');
            await File.WriteAllTextAsync(
                scriptPath,
                $"import sys\nsys.stdout.buffer.write(open('{fixtureForPython}', 'rb').read())\n",
                TestContext.Current.CancellationToken);

            var participant = new DialogueParticipant(
                "probe", "claude", Model: null, "You are an encoding probe.", "python",
                [scriptPath, DialogueParticipant.PromptPlaceholder]);

            var client = new ProcessVendorTurnClient();
            var result = await client.SendTurnAsync(
                participant, "prompt is ignored by the stub", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);

            byte[] recaptured = Encoding.UTF8.GetBytes(result.Text);
            Assert.True(
                ContainsSequence(recaptured, fixtureBytes),
                "The turn text did not round-trip the exact UTF-8 bytes the stub emitted — the " +
                "client transcoded them (#466: redirected stdout decoded with the OEM codepage). " +
                $"Fixture: {BitConverter.ToString(fixtureBytes)} | " +
                $"Recaptured: {BitConverter.ToString(recaptured)}");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    private static bool IsPythonAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "python",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            process!.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
