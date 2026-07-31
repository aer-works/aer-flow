using System.Diagnostics;
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
    /// How long the writer holds the file. Well inside <see cref="LiveFileReader.ShareRetryBudget"/>
    /// so a correct reader still has most of its retries left, while a reader without the retry
    /// fails on its first open.
    /// </summary>
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The defect only ever appeared as a flake under machine load, so nothing in the suite could
    /// tell a fixed reader from a lucky one. Here the contended window is created deliberately: a
    /// writer holds the file with <c>FileShare.None</c> — the same shape as the momentary open the
    /// appender takes — and releases it while the read is in flight.
    /// <para>
    /// <b>Neither side of this test may depend on the thread pool, and both earlier drafts learned
    /// that the hard way.</b> The first ran the reader on <c>Task.Run</c> and slept before
    /// releasing, so a scheduling delay could let the reader make its first attempt after the
    /// release — passing without ever hitting a sharing violation. The second fixed that but still
    /// released from a pool task, and it failed on the very next full-suite run: under load the
    /// release was starved past <see cref="LiveFileReader.ShareRetryBudget"/>, so the reader
    /// correctly gave up and the test reported a defect that was not there. Pool starvation is the
    /// condition this whole bug family lives in, so a test for it cannot be scheduled on the pool.
    /// </para>
    /// <para>
    /// Both halves are now pool-independent. The release runs on a dedicated thread, whose
    /// <c>Thread.Sleep</c> wakes on time regardless of pool pressure. The read runs on the calling
    /// thread and its retry loop also uses <c>Thread.Sleep</c>, so its first
    /// <see cref="LiveFileReader.ShareRetryBudget"/> of retrying needs no pool thread either.
    /// </para>
    /// <para>
    /// Contention is asserted by elapsed time rather than by a flag, because a flag set just after
    /// the handle closes has its own race: the read can legitimately succeed between the release and
    /// the flag. If the read returned before the hold elapsed, it was never contended and the arm
    /// proved nothing — so that fails rather than passes quietly.
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
            try
            {
                var release = new Thread(() =>
                {
                    Thread.Sleep(HoldDuration);
                    exclusive.Dispose();
                })
                {
                    IsBackground = true,
                    Name = "aer-872-release",
                };

                var elapsed = Stopwatch.StartNew();
                release.Start();

                var content = LiveFileReader.ReadText(path);
                elapsed.Stop();
                release.Join(TimeSpan.FromSeconds(10));

                Assert.True(
                    elapsed.Elapsed >= HoldDuration,
                    $"The read returned in {elapsed.ElapsedMilliseconds}ms, inside the {HoldDuration.TotalMilliseconds}ms " +
                    "hold -- the writer's lock was never actually contended, so this arm proved nothing.");
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
