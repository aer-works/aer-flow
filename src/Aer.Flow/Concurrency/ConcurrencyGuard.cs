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
                $"Task directory '{taskDirectoryPath}' is already locked by another Flow instance.", ex);
        }

        return new ConcurrencyGuard(lockStream);
    }

    /// <summary>
    /// Releases the lock. The lock file itself is deliberately left on disk — under §15's
    /// guarantee, only the OS-held lock carries meaning, not the file's existence — so a
    /// subsequent <see cref="Acquire"/> call for the same task directory succeeds immediately.
    /// </summary>
    public void Dispose() => _lockStream.Dispose();
}
