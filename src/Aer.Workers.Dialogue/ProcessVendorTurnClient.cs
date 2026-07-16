using System.Diagnostics;

namespace Aer.Workers.Dialogue;

/// <summary>
/// The real <see cref="IVendorTurnClient"/> (M17 Phase 2, #165): spawns
/// <see cref="DialogueParticipant.Command"/> directly — no shell wrapper, unlike
/// <c>Aer.Adapters</c>'s vendor adapters — with <see cref="DialogueParticipant.Args"/> passed as
/// <see cref="ProcessStartInfo.ArgumentList"/> entries, so each argument (including the substituted
/// prompt, however long or metacharacter-laden) reaches the child process exactly once, quoted
/// correctly by the runtime for the host platform, with no injection or re-quoting question the way
/// a shell-wrapped invocation has (spike #21's Windows token-quoting findings do not apply here for
/// exactly that reason). Real per-vendor argument shaping (the actual <c>claude</c>/<c>agy</c> flag
/// vocabularies) is Phase 3's concern — this client only knows how to run whatever
/// <see cref="DialogueParticipant"/> configuration names, real vendor or test stub alike.
/// <para>
/// Stdin is redirected but never written to and closed immediately, the same "avoid a stdin-wait
/// stall" reasoning <c>ClaudeWorkerAdapter</c>'s remarks record for the real vendor CLIs.
/// </para>
/// <para>
/// <b>Exit code and stderr are captured, not discarded</b> (M17 Phase 3, #166): <see cref="DialogueRunner"/>
/// needs the exit code to classify a turn as failed (a non-zero exit ends the exchange, the same
/// "exit code alone is not success" split <c>OutcomeClassifier</c> applies one layer up), and
/// captured stderr gives a failure message something a human can act on. Stdout and stderr are read
/// concurrently before <see cref="Process.WaitForExitAsync(CancellationToken)"/> — reading them
/// sequentially risks the classic pipe deadlock if a chatty CLI fills the unread stream's OS buffer
/// while blocked writing to it.
/// </para>
/// </summary>
public sealed class ProcessVendorTurnClient : IVendorTurnClient
{
    public async Task<VendorTurnResult> SendTurnAsync(
        DialogueParticipant participant, string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(prompt);

        var startInfo = new ProcessStartInfo
        {
            FileName = participant.Command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };

        foreach (var arg in participant.Args)
        {
            startInfo.ArgumentList.Add(arg == DialogueParticipant.PromptPlaceholder ? prompt : arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new VendorTurnResult(
            stdoutTask.Result.TrimEnd('\r', '\n'),
            process.ExitCode,
            stderrTask.Result.TrimEnd('\r', '\n'));
    }
}
