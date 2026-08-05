using System.Diagnostics;
using System.Text.Json;

namespace Aer.Flow.Concurrency;

/// <summary>
/// Enforces spec §15: at most one Flow instance may mutate a given task's workflow state at a
/// time. Backed by a kernel-held advisory file lock (<see cref="FileShare.None"/> on a
/// <see cref="FileStream"/>) scoped to the task's own directory — deliberately not a sentinel
/// file, whose mere existence would signal "locked" and would survive a crash requiring manual
/// clearing. The OS releases a <see cref="FileStream"/>'s lock the instant its owning process
/// exits, crashed or not, so a crashed holder never leaves a stale lock behind.
/// <para>
/// #618: Writes a sibling sidecar file <c>flow.lock.holder</c> on successful acquire so readers can name
/// the lock holder. A stale sidecar beside a FREE lock is harmless by construction: readers only
/// consult it when an acquire has just failed against a live holder, and every new holder rewrites it.
/// </para>
/// </summary>
public sealed class ConcurrencyGuard : IDisposable
{
    private const string LockFileName = "flow.lock";
    private const string HolderFileName = "flow.lock.holder";

    private readonly FileStream _lockStream;
    private readonly string? _sidecarPath;

    private ConcurrencyGuard(FileStream lockStream, string? sidecarPath = null)
    {
        _lockStream = lockStream;
        _sidecarPath = sidecarPath;
    }

    /// <summary>
    /// Acquires the lock for <paramref name="taskDirectoryPath"/>, creating the directory first
    /// if it does not yet exist.
    /// </summary>
    /// <exception cref="WorkflowLockedException">
    /// Another Flow instance already holds the lock for this task.
    /// </exception>
    public static ConcurrencyGuard Acquire(string taskDirectoryPath, string? holderDescription = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskDirectoryPath);

        Directory.CreateDirectory(taskDirectoryPath);
        var lockFilePath = Path.Combine(taskDirectoryPath, LockFileName);

        FileStream lockStream;
        try
        {
            lockStream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            var holder = TryReadHolderInfo(taskDirectoryPath);
            throw new WorkflowLockedException(BuildLockedMessage(taskDirectoryPath, holder), ex, holder?.HolderDescription, holder?.AcquiredAtUtc);
        }

        return CreateWithSidecar(lockStream, taskDirectoryPath, holderDescription);
    }

    /// <summary>
    /// Acquires the lock like <see cref="Acquire"/>, but retries a lost race until
    /// <paramref name="within"/> elapses instead of failing on the first attempt.
    /// <para>
    /// This is opt-in, and <see cref="Acquire"/> deliberately stays fail-fast: for an
    /// <c>aer run</c> pump, losing the lock means another pump owns this task and waiting for it is
    /// exactly the wrong behaviour. What this exists for is the opposite case — a holder known to
    /// let go in milliseconds, where failing fast turns a routine overlap into a user-visible
    /// error. #857: the room sweep takes this same lock while escalating a newly-appeared memory
    /// proposal, so an operator's approve/reject could lose a coin-flip to a background tick and be
    /// refused, with nothing wrong and nothing to retry but the click.
    /// </para>
    /// <para>
    /// Bounded rather than indefinite on purpose. A genuinely stuck holder must still surface as a
    /// failure; the budget is sized to cover a routine overlap, not to hide one that is not
    /// routine.
    /// </para>
    /// </summary>
    /// <exception cref="WorkflowLockedException">
    /// The lock was still held when <paramref name="within"/> ran out.
    /// </exception>
    public static ConcurrencyGuard AcquireWithin(string taskDirectoryPath, TimeSpan within, string? holderDescription = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskDirectoryPath);

        Directory.CreateDirectory(taskDirectoryPath);
        var lockFilePath = Path.Combine(taskDirectoryPath, LockFileName);

        // Stopwatch, not DateTime.UtcNow: a wall clock can step backwards (an NTP correction, a
        // manual change) and silently stretch this wait well past its budget. Monotonic is what a
        // deadline actually wants.
        var elapsed = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                var lockStream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return CreateWithSidecar(lockStream, taskDirectoryPath, holderDescription);
            }
            catch (IOException) when (elapsed.Elapsed < within)
            {
                // Thread.Sleep rather than Task.Delay: the caller may be on a starved pool, and a
                // retry that cannot be scheduled is a retry that does not happen.
                Thread.Sleep(TimeSpan.FromMilliseconds(25));
            }
            catch (IOException ex)
            {
                var holder = TryReadHolderInfo(taskDirectoryPath);
                var message = $"{BuildLockedMessage(taskDirectoryPath, holder)} Still held after waiting " +
                    $"{within.TotalMilliseconds:0}ms, so this is not a routine overlap.";

                // The OS holder probe costs hundreds of milliseconds, so it runs only here — the
                // budget is already spent and the holder is by definition anomalous. Acquire's
                // fail-fast refusal never pays for it: a routine sweep-vs-pump overlap must be
                // refused immediately (#857), and the sidecar above already names a cooperative
                // holder there.
                if (Store.FileHolderProbe.IsSharingViolation(ex))
                {
                    message += $" Current holder: {Store.FileHolderProbe.DescribeHolders(lockFilePath)}";
                }

                throw new WorkflowLockedException(message, ex, holder?.HolderDescription, holder?.AcquiredAtUtc);
            }
        }
    }

    private static ConcurrencyGuard CreateWithSidecar(FileStream lockStream, string taskDirectoryPath, string? holderDescription)
    {
        var sidecarPath = Path.Combine(taskDirectoryPath, HolderFileName);
        var description = holderDescription ?? DefaultHolderDescription();
        try
        {
            var info = new LockHolderInfo(description, Environment.ProcessId, DateTime.UtcNow);
            var json = JsonSerializer.Serialize(info);
            File.WriteAllText(sidecarPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a failed sidecar write must not fail the acquire — the lock is real
            // even when the label write loses a race (Windows throws UnauthorizedAccessException,
            // not IOException, for several of the transient-handle cases here).
        }

        return new ConcurrencyGuard(lockStream, sidecarPath);
    }

    private static string DefaultHolderDescription() =>
        // A value the current process can always supply — the callers that know a better name
        // (the aer run pump) pass one explicitly.
        $"{Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "process"} (pid {Environment.ProcessId})";

    /// <summary>
    /// #857: the base message does not assert a single cause. It used to name "a live 'aer run'
    /// pump" as the likely holder, which predates rooms and is wrong in a case an operator can
    /// hit; it also does not name the room sweep specifically, because most callers lock a
    /// per-execution task directory no sweep ever touches. The lock file itself still cannot say
    /// who won it — what changed with #618 is the sidecar beside it: when a holder wrote one, its
    /// self-description is appended here, and the two-shapes wording stays as the fallback for a
    /// holder that did not (or whose write lost a race).
    /// </summary>
    private static string BuildLockedMessage(string taskDirectoryPath, LockHolderInfo? holder)
    {
        var baseMsg = $"Directory '{taskDirectoryPath}' is already locked by another Flow instance — either a live " +
            "'aer run' pump, or a background component that takes this directory's lock briefly (a room's " +
            "memory-proposal sweep does this while escalating a new proposal). A live in-flight execution " +
            "can only be reached from the pump process itself (Ctrl+C); 'aer cancel' from a second " +
            "terminal reaches only idle tasks — a crashed pump's orphaned executions, or pending " +
            "non-process work.";

        if (holder != null && !string.IsNullOrWhiteSpace(holder.HolderDescription))
        {
            return $"{baseMsg} Currently held by: {holder.HolderDescription} since {holder.AcquiredAtUtc:O}.";
        }

        return baseMsg;
    }

    /// <summary>
    /// Reads the holder sidecar file <c>flow.lock.holder</c> for <paramref name="taskDirectoryPath"/> if present and readable.
    /// Tolerates absence/unreadability by returning null for both fields.
    /// </summary>
    public static (string? HolderDescription, DateTime? AcquiredAtUtc) ReadHolderInfo(string taskDirectoryPath)
    {
        var info = TryReadHolderInfo(taskDirectoryPath);
        return (info?.HolderDescription, info?.AcquiredAtUtc);
    }

    private static LockHolderInfo? TryReadHolderInfo(string taskDirectoryPath)
    {
        try
        {
            var sidecarPath = Path.Combine(taskDirectoryPath, HolderFileName);
            if (File.Exists(sidecarPath))
            {
                var text = File.ReadAllText(sidecarPath);
                return JsonSerializer.Deserialize<LockHolderInfo>(text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The three ways a sidecar can be unreadable mid-race: held/vanished (IO), ACL-denied
            // (Windows reports it as UnauthorizedAccess), or half-written (Json). All collapse to
            // "the holder did not say", which the caller renders honestly; anything else is a real
            // bug and propagates.
        }

        return null;
    }

    /// <summary>
    /// Reports whether another live holder currently owns the lock for
    /// <paramref name="taskDirectoryPath"/>, without acquiring it and without creating the
    /// directory or the lock file. A read-only probe: callers that need the lock still go through
    /// <see cref="Acquire"/>. A missing <c>flow.lock</c> (or a non-existent directory) means no
    /// holder. A lock file left on disk by a previously-released guard is deliberately <em>not</em>
    /// treated as a hold — under §15 only the live <see cref="FileShare.None"/> stream carries
    /// meaning, not the file's existence — so this opens the file to test the OS-held lock rather
    /// than reading its mere presence.
    /// </summary>
    public static bool IsHeld(string taskDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskDirectoryPath);

        var lockFilePath = Path.Combine(taskDirectoryPath, LockFileName);
        if (!File.Exists(lockFilePath))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(lockFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// Releases the lock. The lock file itself is deliberately left on disk — under §15's
    /// guarantee, only the OS-held lock carries meaning, not the file's existence — so a
    /// subsequent <see cref="Acquire"/> call for the same task directory succeeds immediately.
    /// The holder sidecar is removed best-effort first, while the lock is still held, so no reader
    /// ever sees this holder's label on a lock it has already released; a delete that loses a race
    /// leaves only the stale-beside-free-lock case the class doc calls harmless by construction.
    /// </summary>
    public void Dispose()
    {
        if (_sidecarPath != null)
        {
            try
            {
                if (File.Exists(_sidecarPath))
                {
                    File.Delete(_sidecarPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort for the same reason the write is: the release must not fail over a label.
            }
        }

        _lockStream.Dispose();
    }

    private sealed record LockHolderInfo(string HolderDescription, int Pid, DateTime AcquiredAtUtc);
}
