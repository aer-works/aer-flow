using Aer.Daemon;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Xunit;

namespace Aer.Daemon.Tests;

public class RoomRetentionSweepTests
{
    private static async Task<string> CreateRoomWithEventsAsync(string parentDir, string roomName, params RoomEvent[] events)
    {
        var roomDir = Path.Combine(parentDir, roomName);
        Directory.CreateDirectory(roomDir);

        var roomLogPath = Path.Combine(roomDir, "room.jsonl");
        await using (var writer = new RoomEventLogWriter(roomLogPath))
        {
            foreach (var evt in events)
            {
                await writer.AppendAsync(evt, TestContext.Current.CancellationToken);
            }
        }

        return roomDir;
    }

    [Fact]
    public async Task PerRoomResilience_RoomFailure_DoesNotStopSweepFromCompactingNextRoom()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "aer_sweep_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            // Room 1: Corrupt room log that will throw on read/compaction
            var room1Dir = Path.Combine(tempRoot, "room-1-corrupt");
            Directory.CreateDirectory(room1Dir);
            await File.WriteAllTextAsync(Path.Combine(room1Dir, "room.jsonl"), "INVALID_JSON_CORRUPT_CONTENT\n", TestContext.Current.CancellationToken);

            // Room 2: Valid room with a resolved run that needs compaction
            var refCompleted = new HeldWorkRef("run-completed");
            var refLive = new HeldWorkRef("run-live");
            var dispatchCompleted = new RoomEvent.HeldWorkDispatched(refCompleted, "shape", TimeSpan.FromMinutes(5), "human");
            var resolveCompleted = new RoomEvent.HeldWorkResolved(refCompleted, new HeldWorkCitation("Resolved", "ok"));
            var dispatchLive = new RoomEvent.HeldWorkDispatched(refLive, "shape", TimeSpan.FromMinutes(5), "human");

            var room2Dir = await CreateRoomWithEventsAsync(tempRoot, "room-2-valid", dispatchCompleted, resolveCompleted, dispatchLive);

            var sweep = new RoomRetentionSweep();

            // Run sweep with 0 byte threshold so size doesn't skip room 2
            var count = await sweep.ExecuteSingleSweepAsync(
                roomsDirectoryOverride: tempRoot,
                thresholdBytesOverride: 0,
                cancellationToken: TestContext.Current.CancellationToken);

            // Room 1 threw, Room 2 was compacted successfully
            Assert.Equal(1, count);

            var reader2 = new RoomEventLogReader(Path.Combine(room2Dir, "room.jsonl"));
            var room2Events = await reader2.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            Assert.Single(room2Events);
            Assert.Equal(refLive, ((RoomEvent.HeldWorkDispatched)room2Events[0]).Ref);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public async Task ExecuteSingleSweepAsync_SkipsRoomsBelowThreshold()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "aer_sweep_test_thresh_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var refCompleted = new HeldWorkRef("run-completed");
            var dispatchCompleted = new RoomEvent.HeldWorkDispatched(refCompleted, "shape", TimeSpan.FromMinutes(5), "human");
            var resolveCompleted = new RoomEvent.HeldWorkResolved(refCompleted, new HeldWorkCitation("Resolved", "ok"));

            var roomDir = await CreateRoomWithEventsAsync(tempRoot, "room-small", dispatchCompleted, resolveCompleted);

            var fileInfo = new FileInfo(Path.Combine(roomDir, "room.jsonl"));
            var fileSize = fileInfo.Length;

            var sweep = new RoomRetentionSweep();

            // Threshold set higher than file size -> should skip
            var countSkipped = await sweep.ExecuteSingleSweepAsync(
                roomsDirectoryOverride: tempRoot,
                thresholdBytesOverride: fileSize + 1000,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, countSkipped);

            // Threshold set lower than file size -> should compact
            var countCompacted = await sweep.ExecuteSingleSweepAsync(
                roomsDirectoryOverride: tempRoot,
                thresholdBytesOverride: 0,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, countCompacted);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public void EnvironmentVariables_DefaultsAndOverrides()
    {
        Assert.False(RoomRetentionSweep.IsEnabled());
        Assert.Equal(RoomRetentionSweep.PlaceholderDefaultInterval, RoomRetentionSweep.GetInterval());
        Assert.Equal(RoomRetentionSweep.PlaceholderDefaultThresholdBytes, RoomRetentionSweep.GetThresholdBytes());
    }

    [Fact]
    public void GetInterval_ClampsPathologicalValue_InsteadOfOverflowing()
    {
        var prior = Environment.GetEnvironmentVariable(RoomRetentionSweep.IntervalSecondsEnvironmentVariable);
        try
        {
            // Pins the clamp (RoomRetentionSweep.MaxInterval documents why it exists): a value whose
            // seconds would overflow TimeSpan.FromSeconds must collapse to MaxInterval, never throw.
            Environment.SetEnvironmentVariable(RoomRetentionSweep.IntervalSecondsEnvironmentVariable, "1e300");
            var interval = RoomRetentionSweep.GetInterval();
            Assert.Equal(RoomRetentionSweep.MaxInterval, interval);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RoomRetentionSweep.IntervalSecondsEnvironmentVariable, prior);
        }
    }

    [Fact]
    public void GetInterval_LiftsSubSecondValue_ToMinInterval()
    {
        var prior = Environment.GetEnvironmentVariable(RoomRetentionSweep.IntervalSecondsEnvironmentVariable);
        try
        {
            // Pins the lower clamp (RoomRetentionSweep.MinInterval documents the rationale): a value below
            // one second must lift to MinInterval rather than pass through near-zero.
            Environment.SetEnvironmentVariable(RoomRetentionSweep.IntervalSecondsEnvironmentVariable, "1e-9");
            Assert.Equal(RoomRetentionSweep.MinInterval, RoomRetentionSweep.GetInterval());
        }
        finally
        {
            Environment.SetEnvironmentVariable(RoomRetentionSweep.IntervalSecondsEnvironmentVariable, prior);
        }
    }

    [Fact]
    public async Task ExecuteSingleSweepAsync_PropagatesCancellation_InsteadOfSwallowingItAsAPerRoomError()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "aer_sweep_test_cancel_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var refCompleted = new HeldWorkRef("run-completed");
            var dispatchCompleted = new RoomEvent.HeldWorkDispatched(refCompleted, "shape", TimeSpan.FromMinutes(5), "human");
            var resolveCompleted = new RoomEvent.HeldWorkResolved(refCompleted, new HeldWorkCitation("Resolved", "ok"));
            await CreateRoomWithEventsAsync(tempRoot, "room-cancel", dispatchCompleted, resolveCompleted);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var sweep = new RoomRetentionSweep();

            // A pre-cancelled token makes CompactAsync throw OperationCanceledException. The per-room catch
            // must rethrow it so shutdown unwinds the whole sweep — not log it as a compaction error and
            // march to the next room, which would swallow the cancellation. Without the rethrow clause this
            // returns a count instead of throwing.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                sweep.ExecuteSingleSweepAsync(
                    roomsDirectoryOverride: tempRoot,
                    thresholdBytesOverride: 0,
                    cancellationToken: cts.Token));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }
}
