using System.Diagnostics;

namespace Aer.Workers.Dialogue;

/// <summary>
/// The real <see cref="IVendorTurnClient"/> (M17 Phase 2, #165): spawns
/// <see cref="DialogueParticipant.Command"/> directly — no shell wrapper, unlike
/// <c>Aer.Adapters</c>'s vendor adapters — with <see cref="DialogueParticipant.Args"/> passed as
/// <see cref="ProcessStartInfo.ArgumentList"/> entries, so each argument reaches the child process
/// exactly once, quoted
/// correctly by the runtime for the host platform, with no injection or re-quoting question the way
/// a shell-wrapped invocation has (spike #21's Windows token-quoting findings do not apply here for
/// exactly that reason). Real per-vendor argument shaping (the actual <c>claude</c>/<c>agy</c> flag
/// vocabularies) is Phase 3's concern — this client only knows how to run whatever
/// <see cref="DialogueParticipant"/> configuration names, real vendor or test stub alike. See
/// <see cref="IVendorTurnClient.SendTurnAsync"/> for what <c>prompt</c> means for each of the two
/// placeholders a participant's <see cref="DialogueParticipant.Args"/> can use — only
/// <see cref="DialogueParticipant.PromptPlaceholder"/> substitutes prompt text directly into argv;
/// <see cref="DialogueParticipant.PromptFilePlaceholder"/> substitutes a file path instead, which is
/// how a long exchange avoids #579's command-line length crash.
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
    private static readonly TimeSpan PrintTimeoutMargin = TimeSpan.FromSeconds(60);

    private readonly TimeSpan? _configuredTurnTimeout;

    public ProcessVendorTurnClient(TimeSpan? turnTimeout = null)
    {
        _configuredTurnTimeout = turnTimeout;
    }

    public ProcessVendorTurnClient(DialogueWorkerConfig config)
        : this(config?.TurnTimeout)
    {
    }

    public Task<VendorTurnResult> SendTurnAsync(
        DialogueParticipant participant, string prompt, CancellationToken cancellationToken = default)
        => SendTurnAsync(participant, prompt, turnTimeout: null, cancellationToken);

    public async Task<VendorTurnResult> SendTurnAsync(
        DialogueParticipant participant, string prompt, TimeSpan? turnTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(prompt);

        var effectiveTurnTimeout = turnTimeout ?? _configuredTurnTimeout ?? DialogueWorkerConfig.DefaultTurnTimeout;

        string? tempPromptFile = null;
        try
        {
            // DialogueRunner always pre-writes the turn's prompt and passes its path, so File.Exists(prompt)
            // is true on every production call. The else branch is exercised directly by
            // ProcessVendorTurnClientTests, which call SendTurnAsync with raw prompt text (not a path)
            // against a {PROMPT_FILE} participant to test this client in isolation from DialogueRunner —
            // it writes that text to a temp file so the substitution below still has a path to work
            // with, then cleans the temp file up in the finally block.
            var hasPromptFilePlaceholder = participant.Args.Any(a => a.Contains(DialogueParticipant.PromptFilePlaceholder, StringComparison.Ordinal));
            string? promptFilePath = null;
            if (hasPromptFilePlaceholder)
            {
                if (File.Exists(prompt))
                {
                    promptFilePath = prompt;
                }
                else
                {
                    tempPromptFile = Path.Combine(Path.GetTempPath(), $"aer-dialogue-prompt-{Guid.NewGuid():N}.txt");
                    await File.WriteAllTextAsync(tempPromptFile, prompt, cancellationToken).ConfigureAwait(false);
                    promptFilePath = tempPromptFile;
                }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = participant.Command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };

            // Applied before the args are built purely for readability; ProcessStartInfo.Environment
            // is a plain dictionary and order carries no meaning. Set rather than merged: a value
            // AER computed for the gate has to win over an inherited one of the same name, which is
            // the entire point of CLAUDE_CODE_SIMPLE=0 (#703).
            foreach (var (name, value) in participant.Environment ?? new Dictionary<string, string>())
            {
                startInfo.Environment[name] = value;
            }

            foreach (var arg in participant.Args)
            {
                var substituted = arg;
                if (arg == DialogueParticipant.PromptPlaceholder)
                {
                    substituted = prompt;
                }
                else if (promptFilePath is not null && arg.Contains(DialogueParticipant.PromptFilePlaceholder, StringComparison.Ordinal))
                {
                    substituted = arg.Replace(DialogueParticipant.PromptFilePlaceholder, promptFilePath, StringComparison.Ordinal);
                }

                startInfo.ArgumentList.Add(substituted);
            }

            if (IsAgyCommand(participant.Command) && !HasOperatorPrintTimeout(participant.Args))
            {
                startInfo.ArgumentList.Add("--print-timeout");
                startInfo.ArgumentList.Add(FormatPrintTimeout(effectiveTurnTimeout));
            }

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            process.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTurnTimeout);
            using var registration = timeoutCts.Token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }
            });

            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

                if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return new VendorTurnResult(
                        string.Empty,
                        124,
                        $"Turn timed out after {effectiveTurnTimeout} for role '{participant.Role}'.");
                }

                return new VendorTurnResult(
                    stdoutTask.Result.TrimEnd('\r', '\n'),
                    process.ExitCode,
                    stderrTask.Result.TrimEnd('\r', '\n'));
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return new VendorTurnResult(
                    string.Empty,
                    124,
                    $"Turn timed out after {effectiveTurnTimeout} for role '{participant.Role}'.");
            }
        }
        finally
        {
            if (tempPromptFile is not null && File.Exists(tempPromptFile))
            {
                try
                {
                    File.Delete(tempPromptFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Failed to delete temporary prompt file '{tempPromptFile}': {ex.Message}");
                }
            }
        }
    }

    private static bool IsAgyCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(command);
        return string.Equals(name, "agy", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasOperatorPrintTimeout(IReadOnlyList<string> args)
    {
        return args.Any(a => a == "--print-timeout" || a.StartsWith("--print-timeout=", StringComparison.Ordinal));
    }

    private static string FormatPrintTimeout(TimeSpan timeout)
    {
        var withMargin = timeout > TimeSpan.MaxValue - PrintTimeoutMargin
            ? TimeSpan.MaxValue
            : timeout + PrintTimeoutMargin;

        var seconds = (long)Math.Ceiling(withMargin.TotalSeconds);
        return $"{Math.Max(seconds, 1)}s";
    }
}
