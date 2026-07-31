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
/// <see cref="DialogueParticipant"/> configuration names, real vendor or test stub alike.
/// <paramref name="prompt"/> (see <see cref="IVendorTurnClient.SendTurnAsync"/>) is always substituted
/// directly into the argv element equal to <see cref="DialogueParticipant.PromptPlaceholder"/> — decision
/// 0039 retired the <c>{PROMPT_FILE}</c> file-passing mechanism <c>#580</c> added, because a bounded
/// per-turn prompt (see <see cref="DialogueRunner"/>) never approaches the argv limit #579 crashed on.
/// <para>
/// <b>Vendor-native session continuation (decision 0039).</b> Detected the same way
/// <see cref="DialogueYieldWiring"/> detects a real vendor CLI to wire MCP into: by
/// <see cref="DialogueParticipant.Command"/>'s file name, never <see cref="DialogueParticipant.Vendor"/>
/// (opaque to this worker beyond the transcript) — so a test stub command gets no session flags
/// injected, exactly like it gets no MCP wiring. For a <c>claude</c> command: <paramref name="sessionId"/>
/// null means this participant's session has not started yet, so a fresh id is minted and passed via
/// <c>--session-id</c>; non-null means <c>--resume &lt;id&gt;</c>. For an <c>agy</c> command: <c>agy</c>
/// mints its own id, so a null <paramref name="sessionId"/> instead passes <c>--log-file</c> pointed at
/// a fresh temp file, which is scraped afterward for a <c>conversation=&lt;id&gt;</c> line (the same
/// regex the daemon's own interactive-session turn loop uses against agy's log output); a non-null
/// <paramref name="sessionId"/> passes it straight through via <c>--conversation</c>. Any other command
/// gets no flags at all and echoes <paramref name="sessionId"/> back unchanged.
/// </para>
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

    /// <summary>
    /// The safe-well-under-the-platform-limit ceiling decision 0039 asks for (originally #581):
    /// #579 measured Windows' real argv ceiling at ~32,767 characters, and a bounded per-turn prompt
    /// (see <see cref="DialogueRunner"/>) should never come close to either number — hitting this is
    /// always an unanticipated outlier, so it fails loud and typed rather than crashing the platform
    /// way #579 originally did.
    /// </summary>
    public const int MaxArgumentLength = 16_000;

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
        DialogueParticipant participant, string prompt, string? sessionId = null, CancellationToken cancellationToken = default)
        => SendTurnAsync(participant, prompt, sessionId, turnTimeout: null, cancellationToken);

    public async Task<VendorTurnResult> SendTurnAsync(
        DialogueParticipant participant, string prompt, string? sessionId, TimeSpan? turnTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(prompt);

        var effectiveTurnTimeout = turnTimeout ?? _configuredTurnTimeout ?? DialogueWorkerConfig.DefaultTurnTimeout;

        if (TryGetOperatorPrintTimeout(participant.Args, out var rawTimeout) && rawTimeout is not null)
        {
            if (!TryParseGoDuration(rawTimeout, out var operatorTimeout))
            {
                throw new DialogueWorkerConfigException($"Operator --print-timeout '{rawTimeout}' is not a valid Go duration.");
            }

            var minRequired = effectiveTurnTimeout + PrintTimeoutMargin;
            if (operatorTimeout < minRequired)
            {
                throw new DialogueWorkerConfigException(
                    $"Operator --print-timeout '{rawTimeout}' ({operatorTimeout.TotalSeconds}s) must be at least TurnTimeout + 60s ({minRequired.TotalSeconds}s) for TurnTimeout of {effectiveTurnTimeout.TotalSeconds}s.");
            }
        }

        string? agyLogFilePath = null;

        try
        {
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
                var substituted = arg == DialogueParticipant.PromptPlaceholder ? prompt : arg;

                if (substituted.Length > MaxArgumentLength)
                {
                    throw new DialogueArgumentTooLargeException(
                        $"Turn for role '{participant.Role}' substituted an argument of {substituted.Length} characters, "
                        + $"exceeding the safe threshold of {MaxArgumentLength} (decision 0039's defensive guard). "
                        + "A bounded per-turn prompt should never approach this -- this is very likely an unbounded value reaching argv unexpectedly.");
                }

                startInfo.ArgumentList.Add(substituted);
            }

            // Vendor-native session continuation (decision 0039), detected the same way
            // DialogueYieldWiring detects a real vendor CLI: by Command's file name, never
            // participant.Vendor -- a test-stub command matches neither branch and gets no flags,
            // echoing sessionId back unchanged below.
            string? establishedSessionId = sessionId;
            if (IsClaudeCommand(participant.Command))
            {
                if (sessionId is null)
                {
                    establishedSessionId = Guid.NewGuid().ToString();
                    startInfo.ArgumentList.Add("--session-id");
                    startInfo.ArgumentList.Add(establishedSessionId);
                }
                else
                {
                    startInfo.ArgumentList.Add("--resume");
                    startInfo.ArgumentList.Add(sessionId);
                }
            }
            else if (IsAgyCommand(participant.Command))
            {
                if (sessionId is null)
                {
                    // agy mints its own conversation id; there is nothing to pass on this turn.
                    // Point --log-file at a fresh temp file so the id can be scraped back out after
                    // the process exits (below) -- the same conversation=<id> shape the daemon's own
                    // interactive-session turn loop already scrapes agy's log output for.
                    agyLogFilePath = Path.Combine(Path.GetTempPath(), $"aer-dialogue-agy-log-{Guid.NewGuid():N}.txt");
                    startInfo.ArgumentList.Add("--log-file");
                    startInfo.ArgumentList.Add(agyLogFilePath);
                }
                else
                {
                    startInfo.ArgumentList.Add("--conversation");
                    startInfo.ArgumentList.Add(sessionId);
                }
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
                        $"Turn timed out after {effectiveTurnTimeout} for role '{participant.Role}'.",
                        TimedOut: true,
                        SessionId: sessionId);
                }

                if (agyLogFilePath is not null)
                {
                    establishedSessionId = TryScrapeAgyConversationId(agyLogFilePath);
                }

                return new VendorTurnResult(
                    stdoutTask.Result.TrimEnd('\r', '\n'),
                    process.ExitCode,
                    stderrTask.Result.TrimEnd('\r', '\n'),
                    TimedOut: false,
                    SessionId: establishedSessionId);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return new VendorTurnResult(
                    string.Empty,
                    124,
                    $"Turn timed out after {effectiveTurnTimeout} for role '{participant.Role}'.",
                    TimedOut: true,
                    SessionId: sessionId);
            }
        }
        finally
        {
            if (agyLogFilePath is not null && File.Exists(agyLogFilePath))
            {
                try
                {
                    File.Delete(agyLogFilePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Failed to delete temporary agy log file '{agyLogFilePath}': {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Mirrors the regex the daemon's own interactive-session turn loop (<c>Program.cs</c>) already
    /// uses against agy's <c>--log-file</c> output; not shared code because the daemon's copy carries
    /// its own establishment-tracking semantics this worker does not need (record-once ties this
    /// comment to that behavior, not to a shared helper the two have no other reason to share).
    /// </summary>
    private static string? TryScrapeAgyConversationId(string logFilePath)
    {
        if (!File.Exists(logFilePath))
        {
            return null;
        }

        try
        {
            var logText = File.ReadAllText(logFilePath);
            var match = System.Text.RegularExpressions.Regex.Match(logText, @"conversation=([^\s\r\n]+)");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (IOException)
        {
            return null;
        }
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

    private static bool HasOperatorPrintTimeout(IReadOnlyList<string> args)
    {
        return TryGetOperatorPrintTimeout(args, out _);
    }

    private static bool TryGetOperatorPrintTimeout(IReadOnlyList<string> args, out string? rawTimeout)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--print-timeout" && i + 1 < args.Count)
            {
                rawTimeout = args[i + 1];
                return true;
            }

            if (args[i].StartsWith("--print-timeout=", StringComparison.Ordinal))
            {
                rawTimeout = args[i]["--print-timeout=".Length..];
                return true;
            }
        }

        rawTimeout = null;
        return false;
    }

    public static bool TryParseGoDuration(string input, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var s = input.Trim();
        var isNegative = false;
        if (s.StartsWith('-'))
        {
            isNegative = true;
            s = s[1..];
        }
        else if (s.StartsWith('+'))
        {
            s = s[1..];
        }

        if (string.IsNullOrEmpty(s))
        {
            return false;
        }

        double totalSeconds = 0;
        var i = 0;
        var len = s.Length;

        while (i < len)
        {
            var startNum = i;
            while (i < len && (char.IsDigit(s[i]) || s[i] == '.'))
            {
                i++;
            }

            if (i == startNum)
            {
                return false;
            }

            if (!double.TryParse(s[startNum..i], System.Globalization.CultureInfo.InvariantCulture, out var num))
            {
                return false;
            }

            var startUnit = i;
            while (i < len && (char.IsLetter(s[i]) || s[i] == 'µ'))
            {
                i++;
            }

            if (i == startUnit)
            {
                return false;
            }

            var unit = s[startUnit..i];
            var unitInSeconds = unit switch
            {
                "ns" => 1e-9,
                "us" or "µs" => 1e-6,
                "ms" => 1e-3,
                "s" => 1.0,
                "m" => 60.0,
                "h" => 3600.0,
                _ => -1.0,
            };

            if (unitInSeconds < 0)
            {
                return false;
            }

            totalSeconds += num * unitInSeconds;
        }

        var ts = TimeSpan.FromSeconds(totalSeconds);
        duration = isNegative ? -ts : ts;
        return true;
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

