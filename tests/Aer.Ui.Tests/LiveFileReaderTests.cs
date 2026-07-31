using Aer.Ui.Tests.TestSupport;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// #872. Deliberately carries no <c>[Collection]</c> and no fixture: this is pure filesystem I/O and
/// needs no daemon. Putting it in <c>SessionDirectoryDispatchSerializationTests</c> would have made
/// it pay for a real Kestrel host it never touches, and — worse — serialise it against every other
/// test in that collection, which exists to protect a shared per-user config file this test does not
/// go near.
/// </summary>
public class LiveFileReaderTests
{
    /// <summary>
    /// The defect only ever appeared as a flake under machine load, so nothing in the suite could
    /// tell a fixed reader from a lucky one. Here the contended window is created deliberately: a
    /// writer holds the file with <c>FileShare.None</c> — the same shape as the momentary open the
    /// appender takes — and a background task releases it while the read is in flight.
    /// <para>
    /// The ordering is guaranteed rather than raced for, which an earlier draft of this test got
    /// wrong. That draft started the reader on a background task and slept before releasing, so a
    /// thread-pool delay could let the reader make its first attempt <b>after</b> the release — it
    /// would still pass, without ever having hit a sharing violation. A test that can pass without
    /// discriminating is the thing being guarded against here, so the read now runs on the calling
    /// thread, begun while the lock is provably still held, and the release is what happens in the
    /// background.
    /// </para>
    /// <para>
    /// Windows-only, scoped to the mechanism rather than for convenience: <c>FileShare.None</c> is
    /// enforced by the OS on Windows, while POSIX file locking is advisory, so on Linux and macOS
    /// the read simply succeeds and the arm would be green whether or not the fix were present.
    /// Reporting a skip is better than reporting a pass that proves nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_reader_waits_out_a_writer_holding_the_file_without_sharing_read()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("FileShare.None is only OS-enforced on Windows; elsewhere this arm cannot discriminate. See #872.");
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "aer_872_share_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var path = Path.Combine(directory, "turn-errors.log");
            await File.WriteAllTextAsync(path, "the dispatch failure text", TestContext.Current.CancellationToken);

            var exclusive = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            var released = false;

            // Captured out here rather than read inside the lambda: TestContext.Current is
            // AsyncLocal, and this test should not quietly depend on how it flows into Task.Run.
            var cancellationToken = TestContext.Current.CancellationToken;
            try
            {
                // Released well inside LiveFileReader.ShareRetryBudget, so a correct reader still
                // has most of its retries left; a reader without the retry fails on its first open.
                var release = Task.Run(async () =>
                {
                    await Task.Delay(250, cancellationToken);
                    released = true;
                    exclusive.Dispose();
                }, cancellationToken);

                var content = await LiveFileReader.WaitForContentAsync(path);
                await release;

                Assert.True(released, "The read returned before the writer released -- the lock was not actually contended.");
                Assert.Equal("the dispatch failure text", content);
            }
            finally
            {
                exclusive.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
