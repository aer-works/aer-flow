using System.Net.Http.Json;
using Aer.Daemon;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Ui.Tests.TestSupport;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// #799: covers the two claims the pure-derivation unit tests in Aer.Flow.Tests cannot —
/// recompute-after-restart determinism against real journal fixtures, and read-only coexistence
/// with a live writer inside an actual hosted daemon.
/// </summary>
[Collection("DaemonIntegrationTests")]
public class RoomWakeBridgeIntegrationTests
{
    private static async Task<string> WriteOneStepLaneAsync(string laneDirectory, bool terminal)
    {
        Directory.CreateDirectory(laneDirectory);

        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("wake-bridge-probe"),
            1,
            [new WorkflowStepDefinition(new StepId("step-one"), "step-one", [], ["out"], [], new RetryPolicy(1))]);
        var snapshot = SnapshotBinder.Bind(definition);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(laneDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

        var logPath = Path.Combine(laneDirectory, "flow.jsonl");
        var executionId = new ExecutionId("exec-wake-bridge-1");
        var request = new ExecutionRequest(
            executionId,
            new WorkflowId("wf-wake-bridge"),
            new StepId("step-one"),
            "step-one",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromSeconds(30),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        await using (var writer = new FlowEventLogWriter(logPath))
        {
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            if (terminal)
            {
                await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(executionId), TestContext.Current.CancellationToken);
            }
        }

        return logPath;
    }

    [Fact]
    public async Task Recompute_after_restart_is_deterministic_a_fresh_read_of_the_same_journals_reproduces_the_identical_set()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"wake-bridge-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "room");
        var terminalLane = Path.Combine(testRoot, "lane-terminal");
        var orphanLaneRef = new HeldWorkRef(Path.Combine(testRoot, "lane-orphan"));
        try
        {
            await WriteOneStepLaneAsync(terminalLane, terminal: true);
            // lane-orphan is deliberately never created: dispatch recorded, lane journal absent.

            var roomLogPath = Path.Combine(roomDirectory, "room.jsonl");
            Directory.CreateDirectory(roomDirectory);
            await using (var writer = new RoomEventLogWriter(roomLogPath))
            {
                await writer.AppendAsync(
                    new RoomEvent.HeldWorkDispatched(new HeldWorkRef(terminalLane), "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
                    TestContext.Current.CancellationToken);
                await writer.AppendAsync(
                    new RoomEvent.HeldWorkDispatched(orphanLaneRef, "shape-2", TimeSpan.FromMinutes(10), "decider-1"),
                    TestContext.Current.CancellationToken);
            }

            // "A crash-restarted daemon recomputes the identical set" -- simulated here as two
            // wholly independent calls against the same on-disk journals, no shared in-memory state
            // between them (the static helper takes no bridge instance at all).
            var firstRead = (await RoomWakeBridge.DeriveCurrentWakesAsync(roomDirectory, TestContext.Current.CancellationToken)).Wakes;
            var secondRead = (await RoomWakeBridge.DeriveCurrentWakesAsync(roomDirectory, TestContext.Current.CancellationToken)).Wakes;

            Assert.Equal(2, firstRead.Count);
            Assert.Equal(firstRead.OrderBy(w => w.Ref.Value), secondRead.OrderBy(w => w.Ref.Value));
            Assert.Contains(firstRead, w => w.Ref.Value == terminalLane && w.Kind == RoomWakeKind.DispatchedWorkflowTerminated);
            Assert.Contains(firstRead, w => w.Ref == orphanLaneRef && w.Kind == RoomWakeKind.DispatchOrphaned);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Resolving_the_ref_clears_the_wake_on_the_next_recompute()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"wake-bridge-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "room");
        var terminalLane = Path.Combine(testRoot, "lane-terminal");
        try
        {
            await WriteOneStepLaneAsync(terminalLane, terminal: true);

            var roomLogPath = Path.Combine(roomDirectory, "room.jsonl");
            Directory.CreateDirectory(roomDirectory);
            var laneRef = new HeldWorkRef(terminalLane);
            await using (var writer = new RoomEventLogWriter(roomLogPath))
            {
                await writer.AppendAsync(
                    new RoomEvent.HeldWorkDispatched(laneRef, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
                    TestContext.Current.CancellationToken);
            }

            var beforeResolve = (await RoomWakeBridge.DeriveCurrentWakesAsync(roomDirectory, TestContext.Current.CancellationToken)).Wakes;
            Assert.Single(beforeResolve);

            await using (var writer = new RoomEventLogWriter(roomLogPath))
            {
                await writer.AppendAsync(
                    new RoomEvent.HeldWorkResolved(
                        laneRef, new HeldWorkCitation("exec-wake-bridge-1", "executionSucceeded", 1)),
                    TestContext.Current.CancellationToken);
            }

            // Resolving the ref IS what clears the wake -- no daemon-side ack, no separate call.
            var afterResolve = (await RoomWakeBridge.DeriveCurrentWakesAsync(roomDirectory, TestContext.Current.CancellationToken)).Wakes;
            Assert.Empty(afterResolve);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Daemon_hosted_bridge_coexists_read_only_with_a_live_writer_appending_to_the_same_lane_journal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"wake-bridge-daemon-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "room");
        var laneDirectory = Path.Combine(testRoot, "lane");
        await using var daemon = await DaemonTestHost.StartAsync();
        var client = new HttpClient();
        try
        {
            var aerDir = Aer.Adapters.AerPaths.Root;
            var tokenFile = Path.Combine(aerDir, "daemon.token");
            if (File.Exists(tokenFile))
            {
                var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var logPath = await WriteOneStepLaneAsync(laneDirectory, terminal: false);

            var roomLogPath = Path.Combine(roomDirectory, "room.jsonl");
            Directory.CreateDirectory(roomDirectory);
            await using (var roomWriter = new RoomEventLogWriter(roomLogPath))
            {
                await roomWriter.AppendAsync(
                    new RoomEvent.HeldWorkDispatched(new HeldWorkRef(laneDirectory), "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
                    TestContext.Current.CancellationToken);
            }

            var watchResponse = await client.PostAsJsonAsync(
                $"{daemon.BaseUrl}/api/rooms/watch", new { RoomDirectoryPath = roomDirectory }, TestContext.Current.CancellationToken);
            Assert.True(watchResponse.IsSuccessStatusCode);

            // A concurrent append to the SAME lane journal the hosted bridge is polling: this is
            // the control that actually discriminates. If the bridge ever took the lane's
            // ConcurrencyGuard, this append -- a fresh, independent FlowEventLogWriter, exactly
            // what a live 'aer run' pump would be -- would contend with it. It must never throw.
            var executionId = new ExecutionId("exec-wake-bridge-1");
            Exception? writerException = null;
            for (var i = 0; i < 20 && writerException is null; i++)
            {
                try
                {
                    await using var laneWriter = new FlowEventLogWriter(logPath);
                    await laneWriter.AppendAsync(new FlowEvent.CancellationRequested(new ExecutionId($"noop-{i}")), TestContext.Current.CancellationToken);
                }
                catch (Exception ex)
                {
                    writerException = ex;
                }

                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.Null(writerException);

            // Now let the lane actually finish and confirm the daemon's derived wake set picks it
            // up on a subsequent poll tick -- proving the read-only tailing is functioning, not
            // merely non-throwing.
            await using (var laneWriter = new FlowEventLogWriter(logPath))
            {
                await laneWriter.AppendAsync(new FlowEvent.ExecutionSucceeded(executionId), TestContext.Current.CancellationToken);
            }

            IReadOnlyList<Aer.Flow.Projection.RoomWake>? wakes = null;
            for (var i = 0; i < 40; i++)
            {
                var response = await client.GetAsync($"{daemon.BaseUrl}/api/rooms/wakes", TestContext.Current.CancellationToken);
                Assert.True(response.IsSuccessStatusCode);
                var body = await response.Content.ReadFromJsonAsync<WakesResponse>(cancellationToken: TestContext.Current.CancellationToken);
                if (body?.Wakes is { Count: > 0 })
                {
                    wakes = body.Wakes.Select(w => new Aer.Flow.Projection.RoomWake(new HeldWorkRef(w.Ref), Enum.Parse<RoomWakeKind>(w.Kind))).ToList();
                    break;
                }

                await Task.Delay(200, TestContext.Current.CancellationToken);
            }

            Assert.NotNull(wakes);
            Assert.Contains(wakes!, w => w.Ref.Value == laneDirectory && w.Kind == RoomWakeKind.DispatchedWorkflowTerminated);
        }
        finally
        {
            client.Dispose();
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_malformed_lane_snapshot_suppresses_only_that_lanes_wake_never_the_whole_tick()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"wake-bridge-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "room");
        var healthyLane = Path.Combine(testRoot, "lane-healthy");
        var sickLane = Path.Combine(testRoot, "lane-sick");
        try
        {
            await WriteOneStepLaneAsync(healthyLane, terminal: true);

            // A lane whose snapshot.json is unreadable garbage -- the reviewer's construction for
            // a transiently mid-write (or genuinely corrupted) snapshot: flow.jsonl exists, so the
            // orphan arm does not apply, and the probe's snapshot load throws.
            await WriteOneStepLaneAsync(sickLane, terminal: true);
            await File.WriteAllTextAsync(
                Path.Combine(sickLane, "snapshot.json"), "{ not json", TestContext.Current.CancellationToken);

            var roomLogPath = Path.Combine(roomDirectory, "room.jsonl");
            Directory.CreateDirectory(roomDirectory);
            await using (var writer = new RoomEventLogWriter(roomLogPath))
            {
                await writer.AppendAsync(
                    new RoomEvent.HeldWorkDispatched(new HeldWorkRef(healthyLane), "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
                    TestContext.Current.CancellationToken);
                await writer.AppendAsync(
                    new RoomEvent.HeldWorkDispatched(new HeldWorkRef(sickLane), "shape-2", TimeSpan.FromMinutes(10), "decider-1"),
                    TestContext.Current.CancellationToken);
            }

            var tick = await RoomWakeBridge.DeriveCurrentWakesAsync(roomDirectory, TestContext.Current.CancellationToken);

            // The healthy lane's wake must survive the sick lane's probe failure...
            Assert.Contains(tick.Wakes, w => w.Ref.Value == healthyLane && w.Kind == RoomWakeKind.DispatchedWorkflowTerminated);
            // ...and the sick lane must produce no wake this tick (the transience reasoning lives
            // on RoomWakeBridgeState.CurrentProbeFailures' doc comment), but the failure is
            // surfaced, never logged-and-lost.
            Assert.DoesNotContain(tick.Wakes, w => w.Ref.Value == sickLane);
            var failure = Assert.Single(tick.ProbeFailures);
            Assert.Equal(sickLane, failure.Ref.Value);
            Assert.False(string.IsNullOrWhiteSpace(failure.Error));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #833: the daemon-hosted poller the bridge now runs each tick. Proves the wiring end to end --
    /// a capture written under the room's own execution directory becomes visible held work in that
    /// SAME room's journal after <see cref="RoomWakeBridge.SweepMemoryProposalsAsync"/> runs, with no
    /// room identifier passed anywhere except the room directory itself.
    /// </summary>
    [Fact]
    public async Task SweepMemoryProposalsAsync_escalates_a_capture_under_the_rooms_own_execution_directory()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"wake-bridge-sweep-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "room");
        try
        {
            var captureDir = Path.Combine(roomDirectory, "artifacts", "execution_exec-1", "memory-proposals");
            Directory.CreateDirectory(captureDir);
            var captureFile = Path.Combine(captureDir, "proposal-1.json");
            await File.WriteAllTextAsync(
                captureFile,
                """{"Operation":"add","TargetPath":"fact.md","Content":"x","Rationale":"y"}""",
                TestContext.Current.CancellationToken);

            await RoomWakeBridge.SweepMemoryProposalsAsync(roomDirectory, TestContext.Current.CancellationToken);

            var state = RoomProjector.Project(
                await new RoomEventLogReader(Path.Combine(roomDirectory, "room.jsonl"))
                    .ReadAllRoomEventsAsync(TestContext.Current.CancellationToken));

            var @ref = new HeldWorkRef(Path.GetFullPath(captureFile));
            Assert.Single(state.HeldWork);
            Assert.Equal(MemoryProposalEscalation.MemoryProposalShape, state.HeldWork[@ref].Shape);
            Assert.Equal(MemoryProposalEscalation.DefaultDeciderIdentity, state.HeldWork[@ref].DeciderIdentity);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private sealed record WakesResponse(string? RoomDirectoryPath, List<WakeDto> Wakes);
    private sealed record WakeDto(string Ref, string Kind);
}
