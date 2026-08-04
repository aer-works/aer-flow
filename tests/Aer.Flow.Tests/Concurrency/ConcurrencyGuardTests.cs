using Aer.Flow.Tests.TestSupport;
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
            DirectoryCleanup.DeleteRecursively(taskDirectory);
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
            DirectoryCleanup.DeleteRecursively(taskDirectory);
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
            DirectoryCleanup.DeleteRecursively(taskDirectory);
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
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    /// <summary>
    /// #857: the behaviour the whole issue turns on — a holder that lets go quickly is waited out
    /// rather than refused.
    /// <para>
    /// Neither side runs on the thread pool. The release is a dedicated thread whose
    /// <c>Thread.Sleep</c> wakes on time under any pool pressure, and <c>AcquireWithin</c>'s own
    /// retry uses <c>Thread.Sleep</c> for the same reason. A contention test scheduled on a pool is
    /// a test that stops discriminating exactly when the machine is busy, which is the condition it
    /// exists for.
    /// </para>
    /// </summary>
    [Fact]
    public void AcquireWithin_waits_out_a_holder_that_releases_inside_the_budget()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var hold = TimeSpan.FromMilliseconds(250);
        try
        {
            var holder = ConcurrencyGuard.Acquire(taskDirectory);
            var release = new Thread(() =>
            {
                Thread.Sleep(hold);
                holder.Dispose();
            })
            {
                IsBackground = true,
                Name = "aer-857-release",
            };

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            release.Start();

            using (ConcurrencyGuard.AcquireWithin(taskDirectory, TimeSpan.FromSeconds(5)))
            {
                elapsed.Stop();
            }

            release.Join(TimeSpan.FromSeconds(10));

            Assert.True(
                elapsed.Elapsed >= hold,
                $"Acquired in {elapsed.ElapsedMilliseconds}ms, inside the {hold.TotalMilliseconds}ms hold -- " +
                "the lock was never actually contended, so this proves nothing.");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    /// <summary>
    /// Other polarity: the wait is BOUNDED. A holder that never lets go must still surface as a
    /// failure rather than being waited on forever — a stuck holder is a real problem and hiding it
    /// would be the opposite of the fix.
    /// </summary>
    [Fact]
    public void AcquireWithin_still_throws_when_the_holder_outlasts_the_budget()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            using var holder = ConcurrencyGuard.Acquire(taskDirectory);

            var exception = Assert.Throws<WorkflowLockedException>(
                () => ConcurrencyGuard.AcquireWithin(taskDirectory, TimeSpan.FromMilliseconds(100)));

            Assert.Contains("not a routine overlap", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    /// <summary>
    /// The third polarity, and the one that guards the blast radius: <see cref="ConcurrencyGuard.Acquire"/>
    /// stays FAIL-FAST. #857 adds waiting for the operator-facing path only; an <c>aer run</c> pump
    /// that loses this lock means another pump owns the task, and waiting for it is exactly wrong.
    /// </summary>
    [Fact]
    public void Acquire_remains_fail_fast_and_does_not_wait()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            using var holder = ConcurrencyGuard.Acquire(taskDirectory);

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            Assert.Throws<WorkflowLockedException>(() => ConcurrencyGuard.Acquire(taskDirectory));
            elapsed.Stop();

            Assert.True(
                elapsed.Elapsed < TimeSpan.FromMilliseconds(500),
                $"Acquire took {elapsed.ElapsedMilliseconds}ms -- it is meant to refuse immediately, not wait.");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    /// <summary>
    /// #857's second half. The reasoning lives on <c>ConcurrencyGuard.BuildLockedMessage</c>; what
    /// is pinned here is that the old single-cause wording cannot come back, in either direction —
    /// the discarded claim is absent and the missing one is present.
    /// </summary>
    [Fact]
    public void The_locked_message_does_not_blame_a_pump_as_the_single_likely_cause()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            using var holder = ConcurrencyGuard.Acquire(taskDirectory);

            var exception = Assert.Throws<WorkflowLockedException>(() => ConcurrencyGuard.Acquire(taskDirectory));

            Assert.DoesNotContain("most likely a live 'aer run' pump", exception.Message, StringComparison.Ordinal);
            // Deliberately not "room sweep" -- BuildLockedMessage's own summary owns why. What is
            // pinned here is that the second shape a holder can take is still named at all.
            Assert.Contains("background component", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    [Fact]
    public void Acquire_writes_holder_sidecar_file_with_caller_description()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            using var holder = ConcurrencyGuard.Acquire(taskDirectory, "Test Runner (pid 999)");
            var sidecarPath = Path.Combine(taskDirectory, "flow.lock.holder");

            Assert.True(File.Exists(sidecarPath));
            var content = File.ReadAllText(sidecarPath);
            Assert.Contains("Test Runner (pid 999)", content);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    [Fact]
    public void Second_Acquire_exception_carries_first_holder_description_and_acquired_at()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            using var holder = ConcurrencyGuard.Acquire(taskDirectory, "Custom Holder (pid 123)");

            var exception = Assert.Throws<WorkflowLockedException>(
                () => ConcurrencyGuard.Acquire(taskDirectory, "Second Holder"));

            Assert.Equal("Custom Holder (pid 123)", exception.HolderDescription);
            Assert.NotNull(exception.AcquiredAtUtc);
            Assert.Contains("Currently held by: Custom Holder (pid 123) since", exception.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    [Fact]
    public void Missing_sidecar_polarity_leaves_HolderDescription_null_and_retains_two_shapes_message()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            using var holder = ConcurrencyGuard.Acquire(taskDirectory);
            var sidecarPath = Path.Combine(taskDirectory, "flow.lock.holder");
            if (File.Exists(sidecarPath))
            {
                File.Delete(sidecarPath);
            }

            var exception = Assert.Throws<WorkflowLockedException>(
                () => ConcurrencyGuard.Acquire(taskDirectory));

            Assert.Null(exception.HolderDescription);
            Assert.Null(exception.AcquiredAtUtc);
            Assert.DoesNotContain("Currently held by:", exception.Message);
            Assert.Contains("background component", exception.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    [Fact]
    public void Dispose_removes_holder_sidecar_file()
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            var holder = ConcurrencyGuard.Acquire(taskDirectory, "Temp Holder");
            var sidecarPath = Path.Combine(taskDirectory, "flow.lock.holder");
            Assert.True(File.Exists(sidecarPath));

            holder.Dispose();

            Assert.False(File.Exists(sidecarPath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }
}
