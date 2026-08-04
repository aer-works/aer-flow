using Aer.Daemon;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Xunit;

namespace Aer.Daemon.Tests;

public class RoomTurnHostTests
{
    private sealed class StubRunner : IOccupantTurnRunner
    {
        public Func<OrchestratorTurnInput, TimeSpan, CancellationToken, Task<OccupantTurnResult>>? Handler { get; set; }
        public int CallCount { get; private set; }

        public async Task<OccupantTurnResult> RunTurnAsync(OrchestratorTurnInput input, TimeSpan budget, CancellationToken ct)
        {
            CallCount++;
            if (Handler != null)
            {
                return await Handler(input, budget, ct);
            }

            return new OccupantTurnResult.Completed();
        }
    }

    private static string CreateTestRoomDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aer_room_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Replay_ThrowingRunner_LeavesCursorUnmoved_SecondAssembleDeltaIdentical()
    {
        // Red arm note: If CommitTurn was called despite exception, cursor.ProcessedEventCount would advance and second turn's delta would be empty.
        var roomDir = CreateTestRoomDir();
        try
        {
            var roomLogPath = Path.Combine(roomDir, "room.jsonl");
            await using (var writer = new RoomEventLogWriter(roomLogPath))
            {
                await writer.AppendAsync(new RoomEvent.TurnHostDormancyCleared("operator", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
            }

            var wakeBridgeState = new RoomWakeBridgeState
            {
                RoomDirectoryPath = roomDir,
                CurrentWakes = [new RoomWake(new HeldWorkRef("lanes/l1"), RoomWakeKind.DispatchOrphaned)]
            };
            var hostState = new RoomTurnHostState();
            var stubRunner = new StubRunner
            {
                Handler = (_, _, _) => throw new InvalidOperationException("Runner failure test")
            };

            var host = new RoomTurnHost(wakeBridgeState, hostState, stubRunner);

            // Tick 1: runner throws
            await host.ExecuteSingleTickAsync(TestContext.Current.CancellationToken);

            var cursorAfterFirst = OrchestratorSessionStore.Load(roomDir);
            Assert.Null(cursorAfterFirst); // Cursor unmoved
            Assert.Equal(1, hostState.ConsecutiveFailures);

            // Tick 2: assemble input and verify delta has the event
            var inputSecond = await OrchestratorTurnInput.AssembleAsync(roomDir, wakeBridgeState.CurrentWakes, TestContext.Current.CancellationToken);
            var singleEvent = Assert.Single(inputSecond.EventDelta);
            Assert.IsType<RoomEvent.TurnHostDormancyCleared>(singleEvent);
        }
        finally
        {
            Directory.Delete(roomDir, true);
        }
    }

    [Fact]
    public async Task Watchdog_Timeout_DoesNotCommit_CountsFailure_AndRaisesConfidenceEscalation()
    {
        // Red arm note: If watchdog timeout wasn't caught or didn't raise escalation, no EscalationRaised would exist in journal.
        var roomDir = CreateTestRoomDir();
        try
        {
            var roomLogPath = Path.Combine(roomDir, "room.jsonl");
            await using (var writer = new RoomEventLogWriter(roomLogPath))
            {
                await writer.AppendAsync(new RoomEvent.TurnHostDormancyCleared("operator", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
            }

            var wakeBridgeState = new RoomWakeBridgeState
            {
                RoomDirectoryPath = roomDir,
                CurrentWakes = [new RoomWake(new HeldWorkRef("lanes/l1"), RoomWakeKind.DispatchOrphaned)]
            };
            var hostState = new RoomTurnHostState();
            var stubRunner = new StubRunner
            {
                Handler = async (_, _, ct) =>
                {
                    await Task.Delay(1000, ct); // Sleep longer than tiny budget
                    return new OccupantTurnResult.Completed();
                }
            };

            // Host with tiny 50ms budget
            var host = new RoomTurnHost(wakeBridgeState, hostState, stubRunner, turnBudget: TimeSpan.FromMilliseconds(50));

            await host.ExecuteSingleTickAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, hostState.ConsecutiveFailures);
            var cursor = OrchestratorSessionStore.Load(roomDir);
            Assert.Null(cursor); // No commit

            var reader = new RoomEventLogReader(roomLogPath);
            var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            var escalation = Assert.Single(events.OfType<RoomEvent.EscalationRaised>());
            Assert.Equal(EscalationTrigger.Confidence, escalation.Trigger);
            Assert.Equal(new WorkerId("turn-host"), escalation.FromWorkerId);
            var subject = Assert.IsType<EscalationSubject.HostCondition>(escalation.Subject);
            Assert.Equal("turn-watchdog-timeout", subject.Condition);
        }
        finally
        {
            Directory.Delete(roomDir, true);
        }
    }

    [Fact]
    public async Task BreakerEndToEnd_3FailingTurns_EntersDormancy_AndHostMakesNoFurtherRunnerCalls()
    {
        // Red arm note: If breaker didn't stop ticks, stubRunner.CallCount would continue past 3.
        var roomDir = CreateTestRoomDir();
        try
        {
            File.WriteAllText(Path.Combine(roomDir, "turn-throttles.json"), """{"machineTurnMinimumGapSeconds": 0}""");
            var roomLogPath = Path.Combine(roomDir, "room.jsonl");
            await using (var writer = new RoomEventLogWriter(roomLogPath))
            {
                await writer.AppendAsync(new RoomEvent.TurnHostDormancyCleared("operator", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
            }

            var wakeBridgeState = new RoomWakeBridgeState
            {
                RoomDirectoryPath = roomDir,
                CurrentWakes = [new RoomWake(new HeldWorkRef("lanes/l1"), RoomWakeKind.DispatchOrphaned)]
            };
            var hostState = new RoomTurnHostState();
            var stubRunner = new StubRunner
            {
                Handler = (_, _, _) => Task.FromResult<OccupantTurnResult>(new OccupantTurnResult.Failed("Runner failed"))
            };

            var host = new RoomTurnHost(wakeBridgeState, hostState, stubRunner);

            // 3 failing turns (default limit = 3, min machine gap overridden by throttles or custom starts)
            for (int i = 0; i < 3; i++)
            {
                await host.ExecuteSingleTickAsync(TestContext.Current.CancellationToken);
            }

            Assert.Equal(3, hostState.ConsecutiveFailures);
            Assert.Equal(3, stubRunner.CallCount);

            // 4th tick: breaker triggers Dormant decision
            await host.ExecuteSingleTickAsync(TestContext.Current.CancellationToken);

            Assert.Equal(3, stubRunner.CallCount); // Assert call count stopped at 3!
            Assert.Equal("Dormant", hostState.LastDecisionReason);

            var reader = new RoomEventLogReader(roomLogPath);
            var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            var state = RoomProjector.Project(events);
            Assert.True(state.IsDormant);
            Assert.Contains(events, e => e is RoomEvent.TurnHostDormancyEntered);
        }
        finally
        {
            Directory.Delete(roomDir, true);
        }
    }

    [Fact]
    public async Task SuccessPolarity_CompletedTurn_CommitsCursorAndResetsFailureCount()
    {
        // The positive arm the failure tests discriminate against: with this deleted, a host that
        // never committed anything would still pass Replay/Watchdog/Breaker. Red arm: against a
        // host whose Completed branch is emptied, the cursor stays null and failures stay at 1.
        var roomDir = CreateTestRoomDir();
        try
        {
            var roomLogPath = Path.Combine(roomDir, "room.jsonl");
            await using (var writer = new RoomEventLogWriter(roomLogPath))
            {
                await writer.AppendAsync(new RoomEvent.TurnHostDormancyCleared("operator", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
            }

            var wakeBridgeState = new RoomWakeBridgeState
            {
                RoomDirectoryPath = roomDir,
                CurrentWakes = [new RoomWake(new HeldWorkRef("lanes/l1"), RoomWakeKind.DispatchOrphaned)]
            };
            var hostState = new RoomTurnHostState();
            var failThenSucceed = 0;
            var stubRunner = new StubRunner
            {
                Handler = (_, _, _) => Task.FromResult<OccupantTurnResult>(
                    ++failThenSucceed == 1 ? new OccupantTurnResult.Failed("first") : new OccupantTurnResult.Completed())
            };

            File.WriteAllText(Path.Combine(roomDir, "turn-throttles.json"), """{"machineTurnMinimumGapSeconds": 0}""");
            var host = new RoomTurnHost(wakeBridgeState, hostState, stubRunner);

            await host.ExecuteSingleTickAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, hostState.ConsecutiveFailures);
            Assert.Null(OrchestratorSessionStore.Load(roomDir));

            await host.ExecuteSingleTickAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, hostState.ConsecutiveFailures);
            var cursor = OrchestratorSessionStore.Load(roomDir);
            Assert.NotNull(cursor);
            Assert.Equal(1, cursor.ProcessedEventCount);
        }
        finally
        {
            Directory.Delete(roomDir, true);
        }
    }

    [Fact]
    public async Task LiveReload_UpdatingThrottlesFile_AppliesNewValuesOnNextTick()
    {
        // Red arm note: If throttles were cached statically instead of reloaded per tick, writing turn-throttles.json wouldn't change hostState.Throttles.
        var roomDir = CreateTestRoomDir();
        try
        {
            var wakeBridgeState = new RoomWakeBridgeState
            {
                RoomDirectoryPath = roomDir,
            };
            var hostState = new RoomTurnHostState();
            var host = new RoomTurnHost(wakeBridgeState, hostState, new StubRunner());

            // Initial tick
            await host.ExecuteSingleTickAsync(TestContext.Current.CancellationToken);
            Assert.Equal(RoomTurnThrottles.Defaults, hostState.Throttles);

            // Write new throttles
            File.WriteAllText(Path.Combine(roomDir, "turn-throttles.json"), """{"machineTurnMinimumGapSeconds": 15}""");

            // Second tick
            await host.ExecuteSingleTickAsync(TestContext.Current.CancellationToken);
            Assert.Equal(TimeSpan.FromSeconds(15), hostState.Throttles.MachineTurnMinimumGap);
        }
        finally
        {
            Directory.Delete(roomDir, true);
        }
    }
}
