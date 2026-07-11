using Aer.Flow.Concurrency;

namespace Aer.Flow.Tests.Concurrency;

public class ConcurrencyGuardTests
{
    [Fact]
    public void Acquire_creates_the_task_directory_if_it_does_not_exist()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            Assert.False(Directory.Exists(taskDirectory));

            using var guard = ConcurrencyGuard.Acquire(taskDirectory);

            Assert.True(Directory.Exists(taskDirectory));
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public void Acquire_throws_WorkflowLockedException_when_another_holder_already_has_the_lock()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            using var firstHolder = ConcurrencyGuard.Acquire(taskDirectory);

            Assert.Throws<WorkflowLockedException>(() => ConcurrencyGuard.Acquire(taskDirectory));
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public void Dispose_releases_the_lock_so_a_subsequent_Acquire_succeeds()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            var firstHolder = ConcurrencyGuard.Acquire(taskDirectory);
            firstHolder.Dispose();

            using var secondHolder = ConcurrencyGuard.Acquire(taskDirectory);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [Fact]
    public void Dispose_leaves_the_lock_file_on_disk_because_only_the_OS_held_lock_carries_meaning_not_the_files_existence()
    {
        // Proves the guard is not a sentinel-file mechanism (§15): the lock file's mere existence
        // must never be read as "still locked" — only the live FileShare.None hold does that.
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            var holder = ConcurrencyGuard.Acquire(taskDirectory);
            var lockFilePath = Path.Combine(taskDirectory, "flow.lock");
            Assert.True(File.Exists(lockFilePath));

            holder.Dispose();

            Assert.True(File.Exists(lockFilePath));
            using var secondHolder = ConcurrencyGuard.Acquire(taskDirectory);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }
}
