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
    /// </summary>
    /// <exception cref="WorkflowLockedException">
    /// The lock was still held when <paramref name="within"/> ran out.
    /// </exception>
    public static ConcurrencyGuard AcquireWithin(string taskDirectoryPath, TimeSpan within, string? holderDescription = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskDirectoryPath);

        Directory.CreateDirectory(taskDirectoryPath);
        var lockFilePath = Path.Combine(taskDirectoryPath, LockFileName);

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
                Thread.Sleep(TimeSpan.FromMilliseconds(25));
            }
            catch (IOException ex)
            {
                var holder = TryReadHolderInfo(taskDirectoryPath);
                var message = $"{BuildLockedMessage(taskDirectoryPath, holder)} Still held after waiting " +
                    $"{within.TotalMilliseconds:0}ms, so this is not a routine overlap.";
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
        catch (IOException)
        {
            // Best-effort: an IOException writing the sidecar must not fail the acquire
            // (the lock is real even when the label write loses a race).
        }

        return new ConcurrencyGuard(lockStream, sidecarPath);
    }

    private static string DefaultHolderDescription()
    {
        try
        {
            return $"{Process.GetCurrentProcess().ProcessName} (pid {Environment.ProcessId})";
        }
        catch
        {
            return $"process (pid {Environment.ProcessId})";
        }
    }

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
        catch
        {
            // Tolerating absence/unreadability -> nulls
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
    /// Releases the lock and removes the sidecar file best-effort before disposing the stream.
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
            catch (IOException)
            {
                // Best-effort cleanup of sidecar file.
            }
        }

        _lockStream.Dispose();
    }

    private sealed record LockHolderInfo(string HolderDescription, int Pid, DateTime AcquiredAtUtc);
}
