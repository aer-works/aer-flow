using Aer.Core;
using Aer.Flow.Artifacts;
using Aer.Flow.Domain;
using Aer.Flow.Store;

namespace Aer.Flow.Dispatch;

/// <summary>
/// The concrete binary and arguments to spawn for an <see cref="ExecutionRequest"/>. Resolving a
/// <see cref="ExecutionRequest.Worker"/> role name (e.g. <c>"architect"</c>) to this is a vendor
/// binding concern — <c>CLAUDE.md</c>'s Adapter Isolation rule keeps that resolution out of
/// <c>Aer.Flow</c> entirely, so the caller supplies it explicitly rather than the dispatcher
/// interpreting <see cref="ExecutionRequest.Worker"/> itself.
/// </summary>
/// <param name="WorkingDirectory">
/// The real, already-resolved absolute directory to spawn <see cref="Program"/> in (M23 Phase 3,
/// #272), or <see langword="null"/> to keep the prior default (Core's own process working
/// directory — AER's scratch artifacts folder, never a git-repo requirement). Vendor-agnostic: every
/// <c>IWorkerAdapter</c> forwards <c>WorkerInvocation.WorkingDirectory</c> here unchanged, so a
/// worker can operate on an arbitrary existing project the way it would run raw in a terminal.
/// </param>
/// <param name="PromptText">
/// The exact instructional text this dispatch's adapter built for the worker (issue #292) — e.g.
/// <c>ClaudeWorkerAdapter</c>/<c>GeminiWorkerAdapter</c> set this to the identical string they embed
/// as their <c>-p</c> argument. May still contain unexpanded <c>%AER_INPUT_0%</c>/<c>%AER_OUTPUT_DIR%</c>-
/// style placeholders (same convention <see cref="Args"/> already uses) — <see cref="CoreDispatcher"/>
/// expands it the same way before durably writing it to <c>{outputDirectory}/prompt.txt</c>
/// (<see cref="ArtifactManager.PromptFileName"/>), so this record still carries no execution-specific
/// resolved path, matching every other field here. <see langword="null"/> means this adapter has
/// nothing worth capturing this way — <c>DialogueWorkerAdapter</c> leaves this null since its own
/// worker process already durably records each turn's prompt in <c>transcript.jsonl</c>. Archival
/// capture only, for UI/audit display (CLAUDE.md Architecture Rule 1) — never read back by Flow to
/// make a routing decision.
/// </param>
/// <param name="Environment">
/// Extra environment variables to set on the spawned process, beyond whatever
/// <see cref="ExecutionRequest.Environment"/>'s <see cref="EnvironmentVariable.AerComputed"/> entries
/// already contribute (#533). This is the adapter's own seam, not the engine's: a variable like
/// Claude Code's <c>CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH</c> is a vendor quirk, and Architecture Rule
/// 2 keeps vendor quirks inside <c>Aer.Adapters</c> rather than letting <c>Aer.Flow</c> know the
/// variable's name exists. <see langword="null"/> or empty contributes nothing. The child process
/// still inherits the daemon's own environment otherwise (<c>AerTask.WithClearEnv</c> is never
/// called) — this only ever adds variables, it does not scope what a worker can already see.
/// </param>
public sealed record CoreDispatchTarget(
    string Program,
    IReadOnlyList<string> Args,
    string? WorkingDirectory = null,
    Action<string>? OnStdoutLine = null,
    string? PromptText = null,
    IReadOnlyList<(string Name, string Value)>? Environment = null);

/// <summary>
/// The raw, unclassified facts of a completed dispatch (spec §8's <c>NaturalExit</c> |
/// <c>TimedOut</c> | <c>CancelRequested</c> vocabulary). M7 Phase 6 explicitly excludes outcome
/// classification — mapping this into <c>ExecutionSucceeded</c>/<c>ExecutionFailed</c>/
/// <c>ExecutionCancelled</c> is the Outcome Classifier's job (Phase 7, spec §8).
/// </summary>
/// <param name="StderrTail">
/// The last <see cref="CoreDispatcher.MaxRetainedStderrLength"/> characters the worker wrote to
/// stderr, or <see langword="null"/> if it wrote nothing (#563). The <i>tail</i> specifically: a
/// vendor CLI's actionable line is the last thing it prints, so head-first truncation would discard
/// exactly the message this field exists to carry.
/// <para>
/// Null also on the crash-recovery path, where <c>MutationInterface</c> rebuilds a result from a
/// stored <c>CoreEvent.ExecutionExited</c> after a restart — stderr was never written to the Event
/// Store, so it genuinely does not survive a crash. Read a null as "not recorded", never as "the
/// worker was silent".
/// </para>
/// </param>
public sealed record CoreDispatchResult(int ExitCode, CoreExitReason Reason, string? StderrTail = null);

/// <summary>
/// What <c>MutationInterface</c> needs from a dispatcher (spec §12's "Flow never executes a
/// process; it only ever reads the Event Store and emits requests" — this is the seam through
/// which it emits them). Extracted from <see cref="CoreDispatcher"/> so mutation-level tests can
/// substitute a stub with <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}"/>-controlled
/// completion order (M8 Phase 3) instead of spawning real processes.
/// </summary>
public interface ICoreDispatcher
{
    /// <inheritdoc cref="CoreDispatcher.DispatchAsync"/>
    Task<CoreDispatchResult> DispatchAsync(
        ExecutionRequest request,
        CoreDispatchTarget target,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Calls the aer-core M5 <c>AerTask</c> binding with an <see cref="ExecutionRequest"/> and records
/// Core's lifecycle events to the combined log (M7 Phase 6). This is the P/Invoke Layer
/// <c>CLAUDE.md</c> requires: the only place in <c>Aer.Flow</c> that touches <c>Aer.Core</c>
/// directly.
/// </summary>
public sealed class CoreDispatcher(ICoreEventLogWriter coreEventLogWriter) : ICoreDispatcher
{
    /// <summary>
    /// How many characters of a worker's stderr are retained for
    /// <see cref="CoreDispatchResult.StderrTail"/> (#563).
    /// </summary>
    /// <remarks>
    /// Deliberately larger than <c>OutcomeClassifier</c>'s own display cap. This bound exists to stop
    /// a chatty worker from growing an unbounded buffer in a native callback; deciding how much of it
    /// an operator actually reads is the classifier's job, and pre-truncating here to the display
    /// size would take that choice away from it.
    /// </remarks>
    public const int MaxRetainedStderrLength = 2000;

    /// <summary>
    /// Spawns <paramref name="target"/> with <paramref name="request"/>'s AER-computed environment
    /// variables and timeout, and returns once the process has exited, timed out, or been
    /// cancelled. Never throws for any of those three outcomes — each is a normal result §8 must
    /// later classify, not an error condition — but does not suppress genuine dispatch failures
    /// (e.g. the binary could not be spawned at all), which propagate as <see cref="AerException"/>.
    /// </summary>
    public async Task<CoreDispatchResult> DispatchAsync(
        ExecutionRequest request,
        CoreDispatchTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(target);

        // Resolve variable values from request.Environment
        var pathVariables = request.Environment
            .OfType<EnvironmentVariable.AerComputed>()
            .ToDictionary(v => v.Name, v => v.Value);

        // Perform expansion on target arguments
        var expandedArgs = target.Args.Select(arg => ExpandVariables(arg, pathVariables)).ToList();

        // Issue #292: durably capture the resolved prompt an ordinary (non-dialogue) step's worker
        // was actually invoked with — the same UI/audit transparency a dialogue step's transcript.jsonl
        // already gives its per-turn prompts (CLAUDE.md Architecture Rule 1: archival capture for UI
        // display, never read back to make a routing decision). Written before AerTask ever spawns
        // (below), so it is present even if the execution later fails or times out. Null PromptText
        // (DialogueWorkerAdapter; a future adapter with nothing to capture) is a deliberate no-op, not
        // a missing-data condition.
        if (target.PromptText is { } promptText && pathVariables.TryGetValue("AER_OUTPUT_DIR", out var outputDirectory))
        {
            var promptFilePath = Path.Combine(outputDirectory, ArtifactManager.PromptFileName);
            await File.WriteAllTextAsync(promptFilePath, ExpandVariables(promptText, pathVariables), CancellationToken.None)
                .ConfigureAwait(false);
        }

        // Only ever invoked for a WorkerBinding.Process dispatch (MutationInterface never calls a
        // dispatcher for a NonProcess execution, §17.3) — Timeout is therefore always set.
        using var task = new AerTask(target.Program, [.. expandedArgs]).WithTimeout(request.Timeout!.Value);

        if (target.WorkingDirectory is { } workingDirectory)
        {
            task.WithCwd(workingDirectory);
        }

        // Unconditional since #563. This used to be gated on `target.OnStdoutLine is not null`, i.e.
        // the dialogue/chat path only, which meant an ordinary `aer run` never captured — and
        // aer-core's no-sink drain runs `io::copy(&mut reader, &mut io::sink())` (os/mod.rs:121), so
        // every byte the worker wrote explaining its own failure was read and thrown away.
        //
        // Nothing visible regresses by turning this on: both platforms already spawn the child with
        // `.stderr(Stdio::piped())` unconditionally and explicitly never `Stdio::inherit`
        // (os/unix.rs:26, os/windows.rs:78), so this output has never reached the operator's terminal
        // and there is no inherited stream to take away.
        //
        // aer-core has no stderr-only capture mode — one bool covers both streams — so this also
        // starts delivering StdoutChunk for non-chat dispatches. That case stays a no-op below and
        // must remain allocation-free: the guard runs before any decode.
        task.WithCaptureOutput(true);

        foreach (var environmentVariable in request.Environment)
        {
            // PassThrough variable *values* are resolved by whatever wires a concrete worker
            // adapter (Aer.Adapters, no milestone yet — spec §3) — out of scope here. Only
            // AER-computed variables (paths the Artifact Manager already resolved) are set.
            if (environmentVariable is EnvironmentVariable.AerComputed aerComputed)
            {
                task.WithEnv(aerComputed.Name, aerComputed.Value);
            }
        }

        if (target.Environment is { } targetEnvironment)
        {
            foreach (var (name, value) in targetEnvironment)
            {
                task.WithEnv(name, value);
            }
        }

        var exitCode = 0;
        var reason = CoreExitReason.Natural;
        var pendingLogWrites = new List<Task>();
        var stdoutBuffer = new System.Text.StringBuilder();
        var stdoutLock = new object();

        // #563. Decoded incrementally with a *stateful* decoder rather than one GetString per chunk:
        // a pipe splits at arbitrary byte offsets, so a multi-byte UTF-8 sequence routinely straddles
        // two chunks. Decoding each chunk independently would emit a replacement character at every
        // such boundary and corrupt exactly the non-ASCII diagnostics this field exists to carry.
        var stderrBuffer = new System.Text.StringBuilder();
        var stderrDecoder = System.Text.Encoding.UTF8.GetDecoder();
        var stderrLock = new object();

        task.EventRaised += (_, e) =>
        {
            switch (e.Kind)
            {
                case AerTaskEventKind.Started:
                    // CancellationToken.None, not cancellationToken: a cancellation firing is
                    // exactly what makes this record worth having (§7, §9's crash clause depends on
                    // Started actually landing before a cancel/timeout/host-stop can be attributed
                    // to it), so recording it must not itself be cancellable by that same signal —
                    // the same reasoning DispatchAndRecordOutcomeAsync's outcome append already
                    // applies to its own append.
                    pendingLogWrites.Add(coreEventLogWriter.AppendAsync(
                        new CoreEvent.ExecutionStarted(request.ExecutionId, e.Pid), CancellationToken.None));
                    break;

                case AerTaskEventKind.StdoutChunk:
                    if (target.OnStdoutLine is not null && e.Data is { Length: > 0 })
                    {
                        var text = System.Text.Encoding.UTF8.GetString(e.Data);
                        lock (stdoutLock)
                        {
                            stdoutBuffer.Append(text);
                            var content = stdoutBuffer.ToString();
                            int newlineIndex;
                            while ((newlineIndex = content.IndexOf('\n')) >= 0)
                            {
                                var line = content[..newlineIndex].TrimEnd('\r');
                                target.OnStdoutLine(line);
                                content = content[(newlineIndex + 1)..];
                            }
                            stdoutBuffer.Clear();
                            stdoutBuffer.Append(content);
                        }
                    }
                    break;

                case AerTaskEventKind.StderrChunk:
                    if (e.Data is { Length: > 0 })
                    {
                        lock (stderrLock)
                        {
                            AppendBoundedTail(stderrBuffer, stderrDecoder, e.Data);
                        }
                    }
                    break;

                case AerTaskEventKind.Exited:
                    exitCode = e.ExitCode;
                    reason = ToCoreExitReason(e.ExitReason);
                    pendingLogWrites.Add(coreEventLogWriter.AppendAsync(
                        new CoreEvent.ExecutionExited(request.ExecutionId, e.ExitCode, reason), CancellationToken.None));
                    break;
            }
        };

        try
        {
            // Dispatch(Exited) above has already run by the time RunAsync's Task completes (native
            // callbacks fire synchronously inside aer_task_run, which returns before RunAsync's
            // wrapping Task.Run does), so exitCode/reason are already set here on the natural path.
            await task.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AerTimeoutException)
        {
            reason = CoreExitReason.TimedOut;
        }
        catch (AerCancelException)
        {
            reason = CoreExitReason.CancelRequested;
        }

        await Task.WhenAll(pendingLogWrites).ConfigureAwait(false);

        lock (stdoutLock)
        {
            if (target.OnStdoutLine is not null && stdoutBuffer.Length > 0)
            {
                target.OnStdoutLine(stdoutBuffer.ToString());
                stdoutBuffer.Clear();
            }
        }

        string? stderrTail;
        lock (stderrLock)
        {
            // Flushing emits U+FFFD for a trailing sequence the worker cut short (it died mid-write,
            // or its last character straddled the byte cap). Better a visible replacement character
            // than silently dropping the final char of the very line being diagnosed.
            FlushDecoder(stderrBuffer, stderrDecoder);
            stderrTail = stderrBuffer.Length > 0 ? stderrBuffer.ToString() : null;
        }

        return new CoreDispatchResult(exitCode, reason, stderrTail);
    }

    /// <summary>
    /// Decodes one stderr chunk onto <paramref name="buffer"/> and re-trims it to the last
    /// <see cref="MaxRetainedStderrLength"/> characters.
    /// </summary>
    internal static void AppendBoundedTail(System.Text.StringBuilder buffer, System.Text.Decoder decoder, byte[] data)
    {
        var maxChars = decoder.GetCharCount(data, 0, data.Length, flush: false);
        if (maxChars == 0)
        {
            // A chunk carrying only the opening bytes of a multi-byte sequence decodes to nothing
            // yet — the decoder holds them until the rest arrives. Not an empty chunk.
            return;
        }

        var chars = new char[maxChars];
        var written = decoder.GetChars(data, 0, data.Length, chars, 0, flush: false);
        buffer.Append(chars, 0, written);
        TrimToTail(buffer);
    }

    /// <summary>Drains whatever the decoder still holds once the stream has ended.</summary>
    internal static void FlushDecoder(System.Text.StringBuilder buffer, System.Text.Decoder decoder)
    {
        var maxChars = decoder.GetCharCount([], 0, 0, flush: true);
        if (maxChars == 0)
        {
            return;
        }

        var chars = new char[maxChars];
        var written = decoder.GetChars([], 0, 0, chars, 0, flush: true);
        if (written > 0)
        {
            buffer.Append(chars, 0, written);
            TrimToTail(buffer);
        }
    }

    /// <summary>
    /// Drops the oldest characters so <paramref name="buffer"/> holds at most
    /// <see cref="MaxRetainedStderrLength"/> — keeping the <i>end</i>, which is where a vendor CLI
    /// puts the line worth reading.
    /// </summary>
    internal static void TrimToTail(System.Text.StringBuilder buffer)
    {
        if (buffer.Length <= MaxRetainedStderrLength)
        {
            return;
        }

        var excess = buffer.Length - MaxRetainedStderrLength;

        // Cutting from the front is the mirror of ContractValidator.TrimWithoutSplittingSurrogatePair,
        // which cuts from the back: if the first surviving char is a low surrogate, its high half is
        // among the ones being removed, so drop the orphan too rather than leaving a lone half-pair.
        if (char.IsLowSurrogate(buffer[excess]))
        {
            excess++;
        }

        buffer.Remove(0, excess);
    }

    private static CoreExitReason ToCoreExitReason(AerExitReason reason) => reason switch
    {
        AerExitReason.Natural => CoreExitReason.Natural,
        AerExitReason.TimedOut => CoreExitReason.TimedOut,
        AerExitReason.CancelRequested => CoreExitReason.CancelRequested,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown AerExitReason."),
    };

    private static string ExpandVariables(string arg, Dictionary<string, string> vars)
    {
        var sortedVars = vars.OrderByDescending(v => v.Key.Length).ToList();
        foreach (var (name, value) in sortedVars)
        {
            arg = arg.Replace($"%{name}%", value)  // Windows syntax
                     .Replace($"${name}", value);  // Unix syntax
        }
        return arg;
    }
}
