using Aer.Adapters;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Ui.Tests.TestSupport;

/// <summary>
/// #590: detects two dispatches racing the SAME room directory. Each dispatched process checks for
/// a marker file left by another still-running dispatch in the same <see cref="WorkerInvocation.WorkingDirectory"/>,
/// records a collision if one is found, then holds the marker for <see cref="DispatchDelay"/> before
/// clearing it -- long enough that an unserialised pair of concurrent dispatches against one
/// directory is caught deterministically, while two dispatches against two different directories
/// (their own, distinct marker files) never collide by construction.
/// </summary>
internal sealed class SlowCollisionStubAdapter : IWorkerAdapter
{
    public static readonly TimeSpan DispatchDelay = TimeSpan.FromMilliseconds(900);

    public const string MarkerFileName = ".dispatch-marker";
    public const string CollisionFileName = ".dispatch-collision";

    /// <summary>
    /// One line appended by every dispatch that actually reaches this adapter's process -- distinct
    /// from the marker/collision pair above. A caller whose second concurrent dispatch is refused
    /// upstream (e.g. Flow's own per-directory <c>ConcurrencyGuard</c>, spec §15) never reaches this
    /// process at all, so it never collides on the marker either -- which would make "no collision
    /// file" a false pass for a dispatch that was silently dropped rather than one that was safely
    /// serialised. Counting completions is what tells the two apart.
    /// </summary>
    public const string CompletionsFileName = ".dispatch-completions";

    /// <summary>
    /// Prefix of a per-dispatch stamp file created the moment the dispatch's hold window opens.
    /// The file's own <c>LastWriteTimeUtc</c> is the start timestamp -- filesystem clocks, no shell
    /// date-format portability. Two dispatches on DIFFERENT directories are proven concurrent by
    /// their start gap being under <see cref="DispatchDelay"/>: global serialisation forces the
    /// second start to wait out the first's full hold, while true concurrency leaves only spawn
    /// jitter (both processes pay the same interpreter cold-start, so it cancels out of the gap).
    /// A total-wall-clock bound cannot make that distinction on a noisy CI runner -- measured: a
    /// genuinely concurrent pair took 1390ms combined against a 1350ms bound (PR #831's first CI
    /// run).
    /// </summary>
    public const string StartStampFilePrefix = ".dispatch-start-";

    /// <summary>
    /// Embedded in <see cref="WorkerInvocation.PromptTemplate"/> (same convention as
    /// <c>SessionTurnStubAdapter.FailureSentinel</c>) to force this dispatch to exit non-zero after
    /// still doing its marker/collision/completions bookkeeping -- used to drive a step to a
    /// deterministic Failed/Paused state so a <c>RetryWithRevision</c> decision has something to
    /// legitimately re-dispatch (<c>ExternalDecisionValidator</c> refuses that decision once the
    /// paused outcome is Succeeded).
    /// </summary>
    public const string ForceFailureSentinel = "STUB_FORCE_DISPATCH_FAILURE";

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        var outputName = contract.ProducedOutputs.Count > 0
            ? contract.ProducedOutputs[0].Name
            : "out";
        var dir = invocation.WorkingDirectory ?? Path.GetTempPath();
        var markerFile = Path.Combine(dir, MarkerFileName);
        var collisionFile = Path.Combine(dir, CollisionFileName);
        var completionsFile = Path.Combine(dir, CompletionsFileName);
        var shouldFail = invocation.PromptTemplate.Contains(ForceFailureSentinel, StringComparison.Ordinal);

        if (OperatingSystem.IsWindows())
        {
            // PowerShell, not cmd: SessionTurnStubAdapter's NoOutputFileSentinel/AgyNoOutputFileSentinel
            // comments already measured that an embedded `>` inside a single combined `cmd /c "..."`
            // argv element silently produces no file at all when several quoted-path redirects are
            // chained with `&` -- exactly this script's original shape. Following the same working
            // pattern ShellWorkerCommands.BlockUntilReleased already uses in this project (Test-Path /
            // New-Item -Force / single-quoted literal paths / $env:AER_OUTPUT_DIR via Join-Path).
            var finalStep = shouldFail
                ? "exit 1"
                : $"Set-Content -Path (Join-Path $env:AER_OUTPUT_DIR '{outputName}') -Value 'stub-response'";
            var script =
                $"if (Test-Path '{markerFile}') {{ Add-Content -Path '{collisionFile}' -Value 'collision' }}; " +
                $"New-Item -ItemType File -Force '{markerFile}' | Out-Null; " +
                $"New-Item -ItemType File -Force (Join-Path '{dir}' ('{StartStampFilePrefix}' + $PID)) | Out-Null; " +
                $"Start-Sleep -Milliseconds {DispatchDelay.TotalMilliseconds}; " +
                $"Remove-Item -Force '{markerFile}'; " +
                $"Add-Content -Path '{completionsFile}' -Value 'done'; " +
                finalStep;
            return new CoreDispatchTarget("powershell", ["-NoProfile", "-Command", script]);
        }
        else
        {
            var finalStep = shouldFail ? "exit 1" : $"echo stub-response > \"$AER_OUTPUT_DIR/{outputName}\"";
            var script =
                $"if [ -f '{markerFile}' ]; then echo collision >> '{collisionFile}'; fi; " +
                $"touch '{markerFile}'; " +
                $"touch '{dir}/{StartStampFilePrefix}'$$; " +
                $"sleep 1; " +
                $"rm -f '{markerFile}'; " +
                $"echo done >> '{completionsFile}'; " +
                finalStep;
            return new CoreDispatchTarget("sh", ["-c", script]);
        }
    }
}
