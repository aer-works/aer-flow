namespace Aer.Flow.Concurrency;

/// <summary>
/// Enforces spec §15: at most one Flow instance may mutate a given task's workflow state at a
/// time. Backed by a kernel-held advisory file lock (<see cref="FileShare.None"/> on a
/// <see cref="FileStream"/>) scoped to the task's own directory — deliberately not a sentinel
/// file, whose mere existence would signal "locked" and would survive a crash requiring manual
/// clearing. The OS releases a <see cref="FileStream"/>'s lock the instant its owning process
/// exits, crashed or not, so a crashed holder never leaves a stale lock behind.
/// </summary>
public sealed class ConcurrencyGuard : IDisposable
{
    private const string LockFileName = "flow.lock";

    private readonly FileStream _lockStream;

    private ConcurrencyGuard(FileStream lockStream)
    {
        _lockStream = lockStream;
    }

    /// <summary>
    /// Acquires the lock for <paramref name="taskDirectoryPath"/>, creating the directory first
    /// if it does not yet exist.
    /// </summary>
    /// <exception cref="WorkflowLockedException">
    /// Another Flow instance already holds the lock for this task.
    /// </exception>
    public static ConcurrencyGuard Acquire(string taskDirectoryPath)
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
            throw new WorkflowLockedException(BuildLockedMessage(taskDirectoryPath), ex);
        }

        return new ConcurrencyGuard(lockStream);
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
    public static ConcurrencyGuard AcquireWithin(string taskDirectoryPath, TimeSpan within)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskDirectoryPath);

        Directory.CreateDirectory(taskDirectoryPath);
        var lockFilePath = Path.Combine(taskDirectoryPath, LockFileName);

        // Stopwatch, not DateTime.UtcNow: a wall clock can step backwards (an NTP correction, a
        // manual change) and silently stretch this wait well past its budget. Monotonic is what a
        // deadline actually wants.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            try
            {
                return new ConcurrencyGuard(
                    new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (elapsed.Elapsed < within)
            {
                // Thread.Sleep rather than Task.Delay: the caller may be on a starved pool, and a
                // retry that cannot be scheduled is a retry that does not happen.
                Thread.Sleep(TimeSpan.FromMilliseconds(25));
            }
            catch (IOException ex)
            {
                throw new WorkflowLockedException(
                    $"{BuildLockedMessage(taskDirectoryPath)} Still held after waiting " +
                    $"{within.TotalMilliseconds:0}ms, so this is not a routine overlap.", ex);
            }
        }
    }

    /// <summary>
    /// #857: the message no longer asserts a single cause. It used to name "a live 'aer run' pump"
    /// as the likely holder, which predates rooms and is wrong in a case an operator can hit.
    /// <para>
    /// It also does not name the room sweep specifically, deliberately. This message is shared by
    /// every caller, and most of them lock a per-execution task directory that no sweep ever
    /// touches — naming rooms there would swap one misdirection for another. A lock file cannot say
    /// who won it, so the honest wording gives the two shapes a holder can take and lets the reader
    /// match whichever applies, rather than picking one for them.
    /// </para>
    /// </summary>
    private static string BuildLockedMessage(string taskDirectoryPath) =>
        $"Directory '{taskDirectoryPath}' is already locked by another Flow instance — either a live " +
        "'aer run' pump, or a background component that takes this directory's lock briefly (a room's " +
        "memory-proposal sweep does this while escalating a new proposal). A live in-flight execution " +
        "can only be reached from the pump process itself (Ctrl+C); 'aer cancel' from a second " +
        "terminal reaches only idle tasks — a crashed pump's orphaned executions, or pending " +
        "non-process work.";

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
    /// </summary>
    public void Dispose() => _lockStream.Dispose();
}
