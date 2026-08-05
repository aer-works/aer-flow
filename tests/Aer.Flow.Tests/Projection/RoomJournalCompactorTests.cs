using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Aer.Tests.Shared;

namespace Aer.Flow.Tests.Projection;

public class RoomJournalCompactorTests
{
    private static async Task<string> CreateTestRoomAsync(params RoomEvent[] events)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "aer_compactor_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);

        var roomLogPath = Path.Combine(tempDir, "room.jsonl");
        await using (var writer = new RoomEventLogWriter(roomLogPath))
        {
            foreach (var evt in events)
            {
                await writer.AppendAsync(evt, TestContext.Current.CancellationToken);
            }
        }

        return tempDir;
    }

    [Fact]
    public async Task CompactAsync_shrinks_journal_carrying_completed_runs()
    {
        var refCompleted = new HeldWorkRef("lane-completed");
        var refLive = new HeldWorkRef("lane-live");

        var dispatchCompleted = new RoomEvent.HeldWorkDispatched(refCompleted, "shape", TimeSpan.FromMinutes(5), "human");
        var resolveCompleted = new RoomEvent.HeldWorkResolved(refCompleted, new HeldWorkCitation("Resolved", "ok"));
        var dispatchLive = new RoomEvent.HeldWorkDispatched(refLive, "shape", TimeSpan.FromMinutes(5), "human");

        var roomDir = await CreateTestRoomAsync(dispatchCompleted, resolveCompleted, dispatchLive);
        try
        {
            var readerInitial = new RoomEventLogReader(Path.Combine(roomDir, "room.jsonl"));
            var initialEvents = await readerInitial.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            Assert.Equal(3, initialEvents.Count);

            var compacted = await RoomJournalCompactor.CompactAsync(roomDir, TestContext.Current.CancellationToken);
            Assert.True(compacted);

            var readerCompacted = new RoomEventLogReader(Path.Combine(roomDir, "room.jsonl"));
            var compactedEvents = await readerCompacted.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            Assert.Single(compactedEvents);
            Assert.IsType<RoomEvent.HeldWorkDispatched>(compactedEvents[0]);
            Assert.Equal(refLive, ((RoomEvent.HeldWorkDispatched)compactedEvents[0]).Ref);

            var roomState = RoomProjector.Project(compactedEvents);
            Assert.True(roomState.HeldWork.ContainsKey(refLive));
            Assert.False(roomState.HeldWork.ContainsKey(refCompleted));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task CompactAsync_leaves_journal_with_only_live_runs_untouched()
    {
        var refLive = new HeldWorkRef("lane-live");
        var dispatchLive = new RoomEvent.HeldWorkDispatched(refLive, "shape", TimeSpan.FromMinutes(5), "human");
        var escalatedLive = new RoomEvent.HeldWorkEscalated(refLive, "operator");

        var roomDir = await CreateTestRoomAsync(dispatchLive, escalatedLive);
        try
        {
            var compacted = await RoomJournalCompactor.CompactAsync(roomDir, TestContext.Current.CancellationToken);
            Assert.False(compacted);

            var reader = new RoomEventLogReader(Path.Combine(roomDir, "room.jsonl"));
            var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, events.Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task CompactAsync_is_noop_run_twice()
    {
        var refCompleted = new HeldWorkRef("lane-completed");
        var refLive = new HeldWorkRef("lane-live");

        var dispatchCompleted = new RoomEvent.HeldWorkDispatched(refCompleted, "shape", TimeSpan.FromMinutes(5), "human");
        var resolveCompleted = new RoomEvent.HeldWorkResolved(refCompleted, new HeldWorkCitation("Resolved", "ok"));
        var dispatchLive = new RoomEvent.HeldWorkDispatched(refLive, "shape", TimeSpan.FromMinutes(5), "human");

        var roomDir = await CreateTestRoomAsync(dispatchCompleted, resolveCompleted, dispatchLive);
        try
        {
            var firstRun = await RoomJournalCompactor.CompactAsync(roomDir, TestContext.Current.CancellationToken);
            Assert.True(firstRun);

            var secondRun = await RoomJournalCompactor.CompactAsync(roomDir, TestContext.Current.CancellationToken);
            Assert.False(secondRun);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    /// <summary>
    /// A cancelled compaction leaves the journal intact AND leaves no temp file behind. The first
    /// half is what "crash-safe" means; the second is what the write-failure path costs if nothing
    /// collects it. Cancellation is the one interruption a test can actually inject — a kill between
    /// write and rename is NOT simulated here, and that half of the claim rests on the temp-then-
    /// rename mechanism rather than on this test.
    /// </summary>
    [Fact]
    public async Task A_cancelled_compaction_leaves_the_journal_intact_and_no_temp_behind()
    {
        var refCompleted = new HeldWorkRef("lane-completed");
        var dispatchCompleted = new RoomEvent.HeldWorkDispatched(refCompleted, "shape", TimeSpan.FromMinutes(5), "human");
        var resolveCompleted = new RoomEvent.HeldWorkResolved(refCompleted, new HeldWorkCitation("Resolved", "ok"));

        var roomDir = await CreateTestRoomAsync(dispatchCompleted, resolveCompleted);
        try
        {
            var roomLogPath = Path.Combine(roomDir, "room.jsonl");
            var before = await File.ReadAllTextAsync(roomLogPath, TestContext.Current.CancellationToken);

            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => RoomJournalCompactor.CompactAsync(roomDir, cancelled.Token));

            Assert.Equal(before, await File.ReadAllTextAsync(roomLogPath, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(roomDir, "room.jsonl.tmp.*"));

            // The control: uncancelled, the same call really does rewrite this journal, so the
            // assertions above are about the cancellation and not about a no-op input.
            Assert.True(await RoomJournalCompactor.CompactAsync(roomDir, TestContext.Current.CancellationToken));
            Assert.NotEqual(before, await File.ReadAllTextAsync(roomLogPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }
    /// <summary>
    /// The compaction lock is load-bearing, not decorative — <see cref="RoomJournalCompactor"/>'s
    /// own comment says what it protects. Held lock in, refusal out.
    /// </summary>
    [Fact]
    public async Task CompactAsync_refuses_while_the_room_lock_is_held_by_someone_else()
    {
        var refCompleted = new HeldWorkRef("lane-locked");
        var roomDir = await CreateTestRoomAsync(
            new RoomEvent.HeldWorkDispatched(refCompleted, "shape", TimeSpan.FromMinutes(1), "decider"),
            new RoomEvent.HeldWorkResolved(refCompleted, new HeldWorkCitation("Resolved", "ok")));

        var before = await File.ReadAllTextAsync(Path.Combine(roomDir, "room.jsonl"), TestContext.Current.CancellationToken);

        using (Aer.Flow.Concurrency.ConcurrencyGuard.Acquire(roomDir, "test holder"))
        {
            await Assert.ThrowsAsync<Aer.Flow.Concurrency.WorkflowLockedException>(
                () => RoomJournalCompactor.CompactAsync(roomDir, TestContext.Current.CancellationToken));
        }

        // The control: the same call succeeds once the lock is free, so the refusal above is about
        // the lock and not about the journal being uncompactable.
        Assert.True(await RoomJournalCompactor.CompactAsync(roomDir, TestContext.Current.CancellationToken));
        var after = await File.ReadAllTextAsync(Path.Combine(roomDir, "room.jsonl"), TestContext.Current.CancellationToken);
        Assert.NotEqual(before, after);
    }
}
