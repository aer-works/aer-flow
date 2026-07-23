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
            throw new WorkflowLockedException(
                $"Task directory '{taskDirectoryPath}' is already locked by another Flow instance — most " +
                "likely a live 'aer run' pump. A live in-flight execution can only be reached from that " +
                "process itself (Ctrl+C); 'aer cancel' from a second terminal reaches only idle tasks — a " +
                "crashed pump's orphaned executions, or pending non-process work.", ex);
        }

        return new ConcurrencyGuard(lockStream);
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
    /// </summary>
    public void Dispose() => _lockStream.Dispose();
}
