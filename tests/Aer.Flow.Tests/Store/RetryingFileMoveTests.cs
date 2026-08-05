using Aer.Flow.Store;
using Aer.Tests.Shared;

namespace Aer.Flow.Tests.Store;

public sealed class RetryingFileMoveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"retrying-move-{Guid.NewGuid():N}");

    private string Path_(string name) => Path.Combine(_directory, name);

    public RetryingFileMoveTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_directory);

    [Fact]
    public void Plain_move_moves_file_and_preserves_content()
    {
        var src = Path_("source.txt");
        var dst = Path_("dest.txt");
        File.WriteAllText(src, "plain move content");

        RetryingFileMove.Move(src, dst, overwrite: true);

        Assert.False(File.Exists(src));
        Assert.True(File.Exists(dst));
        Assert.Equal("plain move content", File.ReadAllText(dst));
    }

    [Fact]
    public async Task Retries_and_succeeds_when_destination_lock_is_released_within_budget()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("FileShare.None only blocks a move's delete-share requirement on Windows.");
        }

        var src = Path_("source.txt");
        var dst = Path_("dest.txt");
        File.WriteAllText(src, "transient source content");
        File.WriteAllText(dst, "transient dest initial content");

        // Open destination with FileShare.None on a background task that releases after ~300ms.
        // Red arm note: with the retry loop removed (a bare File.Move), this throws
        // UnauthorizedAccessException or IOException on Windows because destination is locked.
        var lockAcquired = new ManualResetEventSlim(false);
        var task = Task.Run(async () =>
        {
            using (new FileStream(dst, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                lockAcquired.Set();
                await Task.Delay(300, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        lockAcquired.Wait(TestContext.Current.CancellationToken);
        RetryingFileMove.Move(src, dst, overwrite: true, budget: TimeSpan.FromSeconds(5));

        await task;
        Assert.False(File.Exists(src));
        Assert.Equal("transient source content", File.ReadAllText(dst));
    }

    [Fact]
    public void Throws_when_destination_lock_is_held_longer_than_retry_budget()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("FileShare.None only blocks a move's delete-share requirement on Windows.");
        }

        var src = Path_("source.txt");
        var dst = Path_("dest.txt");
        File.WriteAllText(src, "expiry source content");
        File.WriteAllText(dst, "expiry dest initial content");

        using (new FileStream(dst, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var budget = TimeSpan.FromMilliseconds(300);
            var ex = Assert.ThrowsAny<Exception>(() =>
                RetryingFileMove.Move(src, dst, overwrite: true, budget: budget));

            Assert.True(ex is IOException or UnauthorizedAccessException,
                $"Expected IOException or UnauthorizedAccessException, got {ex.GetType()}");
        }
    }
}

