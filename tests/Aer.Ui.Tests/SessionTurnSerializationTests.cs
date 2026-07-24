using Aer.Daemon;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// #393: turns must be serialised per session directory. <c>ExecuteSessionTurnAsync</c> reads the
/// projection, branches on it, and -- in the re-materialize branch -- deletes
/// <c>snapshot.json</c>/<c>flow.jsonl</c>/<c>artifacts</c> before <c>RunAsync</c> takes Flow's
/// per-task-directory lock, so that window sat outside every lock; and turns run fire-and-forget
/// behind an already-returned 200, so overlapping sends genuinely interleave there.
///
/// These cover the lock's <em>identity</em> -- that one directory maps to exactly one semaphore.
/// It is the failure mode that would make the whole fix a silent no-op while every other test still
/// passed: two spellings of one directory yielding two semaphores serialises nothing.
/// <see cref="SessionTurnSerializationEndToEndTests"/> covers the behaviour over a real daemon.
/// </summary>
public class SessionTurnSerializationTests
{
    private static string UniqueDirectory() =>
        Path.Combine(Path.GetTempPath(), "aer-393-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TrailingSeparator_MapsToTheSameLock()
    {
        var directory = UniqueDirectory();

        Assert.Same(
            DaemonHost.SessionTurnLockFor(directory),
            DaemonHost.SessionTurnLockFor(directory + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void RelativeSegments_MapToTheSameLock()
    {
        var directory = UniqueDirectory();
        var viaDotSegment = Path.Combine(directory, ".");

        Assert.Same(
            DaemonHost.SessionTurnLockFor(directory),
            DaemonHost.SessionTurnLockFor(viaDotSegment));
    }

    [Fact]
    public void DifferentCasing_MapsToTheSameLock()
    {
        // Windows resolves these to one directory, so they must share one lock. On a case-sensitive
        // filesystem this can only ever over-serialise two genuinely distinct directories -- safe;
        // under-locking is the failure worth preventing.
        var directory = UniqueDirectory();

        Assert.Same(
            DaemonHost.SessionTurnLockFor(directory.ToUpperInvariant()),
            DaemonHost.SessionTurnLockFor(directory.ToLowerInvariant()));
    }

    [Fact]
    public void DistinctDirectories_DoNotShareALock()
    {
        // The other half: over-broad keying would serialise every session in the daemon behind one
        // semaphore, turning a correctness fix into a throughput bug.
        Assert.NotSame(
            DaemonHost.SessionTurnLockFor(UniqueDirectory()),
            DaemonHost.SessionTurnLockFor(UniqueDirectory()));
    }

    [Fact]
    public async Task TheLockIsBinary_ASecondHolderMustWait()
    {
        // Guards the count: a SemaphoreSlim(2, 2) would admit two turns at once and every identity
        // test above would still pass.
        var turnLock = DaemonHost.SessionTurnLockFor(UniqueDirectory());

        await turnLock.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(await turnLock.WaitAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken));
        }
        finally
        {
            turnLock.Release();
        }

        // Released, so the next turn gets in.
        Assert.True(await turnLock.WaitAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken));
        turnLock.Release();
    }
}
