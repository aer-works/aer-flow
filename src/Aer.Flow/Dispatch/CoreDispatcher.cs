using Aer.Core;
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
public sealed record CoreDispatchTarget(
    string Program,
    IReadOnlyList<string> Args,
    string? WorkingDirectory = null,
    Action<string>? OnStdoutLine = null);

/// <summary>
/// The raw, unclassified facts of a completed dispatch (spec §8's <c>NaturalExit</c> |
/// <c>TimedOut</c> | <c>CancelRequested</c> vocabulary). M7 Phase 6 explicitly excludes outcome
/// classification — mapping this into <c>ExecutionSucceeded</c>/<c>ExecutionFailed</c>/
/// <c>ExecutionCancelled</c> is the Outcome Classifier's job (Phase 7, spec §8).
/// </summary>
public sealed record CoreDispatchResult(int ExitCode, CoreExitReason Reason);

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

        // Only ever invoked for a WorkerBinding.Process dispatch (MutationInterface never calls a
        // dispatcher for a NonProcess execution, §17.3) — Timeout is therefore always set.
        using var task = new AerTask(target.Program, [.. expandedArgs]).WithTimeout(request.Timeout!.Value);

        if (target.WorkingDirectory is { } workingDirectory)
        {
            task.WithCwd(workingDirectory);
        }

        if (target.OnStdoutLine is not null)
        {
            task.WithCaptureOutput(true);
        }

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

        var exitCode = 0;
        var reason = CoreExitReason.Natural;
        var pendingLogWrites = new List<Task>();
        var stdoutBuffer = new System.Text.StringBuilder();
        var stdoutLock = new object();

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

        return new CoreDispatchResult(exitCode, reason);
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
