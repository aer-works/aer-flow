using Aer.Adapters;
using Aer.Daemon;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Outcomes;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Xunit;

namespace Aer.Daemon.Tests;

public class RoleTemplateOccupantRunnerTests
{
    private sealed class FakeWorkerAdapter : IWorkerAdapter
    {
        public WorkerInvocation? LastInvocation { get; private set; }
        public WorkerContract? LastContract { get; private set; }

        public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
        {
            LastInvocation = invocation;
            LastContract = contract;
            return new CoreDispatchTarget("fake-binary", []);
        }
    }

    private sealed class FakeCoreDispatcher : ICoreDispatcher
    {
        public Func<ExecutionRequest, CoreDispatchTarget, CoreDispatchResult>? Handler { get; set; }
        public ExecutionRequest? LastRequest { get; private set; }

        public Task<CoreDispatchResult> DispatchAsync(ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (Handler != null)
            {
                return Task.FromResult(Handler(request, target));
            }

            return Task.FromResult(new CoreDispatchResult(0, CoreExitReason.Natural));
        }
    }

    private static string CreateTestRoomDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aer_runner_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>An adapter whose target is a real spawnable process that copies a prepared
    /// actions fixture into the turn's output directory — what exercises the runner's DEFAULT
    /// dispatcher path (real <see cref="CoreDispatcher"/> + per-turn events.jsonl lifecycle
    /// log), which the injected-dispatcher tests never touch (second-reader finding).</summary>
    private sealed class CopyFixtureAdapter(string fixturePath) : IWorkerAdapter
    {
        // No embedded quotes — the CrashTestHost precedent: quote layers between the native
        // spawn and cmd mangle them, and these temp paths carry no spaces. %AER_OUTPUT_DIR% is
        // expanded AER-side by CoreDispatcher's ExpandVariables before cmd ever sees the arg.
        public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
            => OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", $"copy /y {fixturePath} %AER_OUTPUT_DIR%\\turn-actions.json >nul"])
                : new CoreDispatchTarget("sh", ["-c", $"cp {fixturePath} $AER_OUTPUT_DIR/turn-actions.json"]);
    }

    [Fact]
    public async Task DefaultDispatcherPath_RealSpawn_RecordsLifecycleLogAndCompletes()
    {
        // Red arm: with the lifecycle-log branch deleted (or the writer never disposed), either
        // no events.jsonl exists in the turn's output directory or this test's read of it finds
        // an empty file. Covers the one path the injected-dispatcher tests cannot: a real
        // CoreDispatcher spawn writing Core events through a FlowEventLogWriter that must
        // flush and dispose before the turn returns.
        var roomDir = CreateTestRoomDir();
        try
        {
            var fixturePath = Path.Combine(roomDir, "fixture-actions.json");
            File.WriteAllText(fixturePath, """{ "contractVersion": 1, "report": "spawned for real", "escalations": [] }""");

            var adapters = new Dictionary<string, IWorkerAdapter> { ["claude"] = new CopyFixtureAdapter(fixturePath) };
            var runner = new RoleTemplateOccupantRunner(adapters); // no injected dispatcher — the default path

            var input = await OrchestratorTurnInput.AssembleAsync(roomDir, [], TestContext.Current.CancellationToken);
            var result = await runner.RunTurnAsync(input, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            if (result is OccupantTurnResult.Failed f)
            {
                Assert.Fail($"turn failed: {f.Reason}");
            }

            var turnDirs = Directory.GetDirectories(Path.Combine(roomDir, ".aer", "occupant-turns"));
            var turnDir = Assert.Single(turnDirs);
            var lifecycleLog = Path.Combine(turnDir, "events.jsonl");
            Assert.True(File.Exists(lifecycleLog), "the spawn left no lifecycle record");
            Assert.NotEqual(0, new FileInfo(lifecycleLog).Length);
        }
        finally
        {
            Directory.Delete(roomDir, true);
        }
    }

    [Fact]
    public async Task MissingAdapterInRegistry_FailsWithAdapterName()
    {
        // Red arm: an empty registry must produce Failed naming the adapter, not a throw.
        var roomDir = CreateTestRoomDir();
        try
        {
            var runner = new RoleTemplateOccupantRunner(new Dictionary<string, IWorkerAdapter>());
            var input = await OrchestratorTurnInput.AssembleAsync(roomDir, [], TestContext.Current.CancellationToken);

            var result = await runner.RunTurnAsync(input, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var failed = Assert.IsType<OccupantTurnResult.Failed>(result);
            Assert.Contains("claude", failed.Reason);
        }
        finally
        {
            Directory.Delete(roomDir, true);
        }
    }

    [Fact]
    public async Task RunTurnAsync_EverythingEscalates_RaisesTwoEscalations_NoHeldWorkDispatched()
    {
        // Red arm note: If runner fails to parse actions or fails to raise escalations, room journal won't contain EscalationRaised events or HeldWorkDispatched might be incorrectly appended.
        var roomDir = CreateTestRoomDir();
        try
        {
            var fakeAdapter = new FakeWorkerAdapter();
            var fakeDispatcher = new FakeCoreDispatcher
            {
                Handler = (req, target) =>
                {
                    var outputDirVar = req.Environment.OfType<EnvironmentVariable.AerComputed>().FirstOrDefault(e => e.Name == "AER_OUTPUT_DIR");
                    Assert.NotNull(outputDirVar);
                    Directory.CreateDirectory(outputDirVar.Value);

                    var actionsJson = """
                    {
                      "contractVersion": 1,
                      "report": "Analysis finished",
                      "escalations": [
                        { "trigger": "Ambiguity", "subject": { "kind": "decision", "decisionId": "d-1" } },
                        { "trigger": "Direction", "subject": { "kind": "origination", "templateId": "review-run", "briefRef": "artifacts/brief.md" } }
                      ]
                    }
                    """;
                    File.WriteAllText(Path.Combine(outputDirVar.Value, "turn-actions.json"), actionsJson);

                    return new CoreDispatchResult(0, CoreExitReason.Natural);
                }
            };

            var adapters = new Dictionary<string, IWorkerAdapter> { ["claude"] = fakeAdapter };
            var runner = new RoleTemplateOccupantRunner(adapters, fakeDispatcher);

            var roomState = new RoomState(new Dictionary<HeldWorkRef, HeldWorkState>(), []);
            var memoryDoc = new RoomMemoryDocument(0, "", new Dictionary<string, string>(), []);
            var input = new OrchestratorTurnInput(roomState, [], [], memoryDoc, null, IsColdStart: true, TotalEventCount: 0, RoomDirectoryPath: roomDir);

            var budget = TimeSpan.FromMinutes(5);
            var result = await runner.RunTurnAsync(input, budget, TestContext.Current.CancellationToken);

            Assert.IsType<OccupantTurnResult.Completed>(result);

            // Verify captured request & grant parameters
            Assert.NotNull(fakeAdapter.LastInvocation);
            Assert.NotNull(fakeAdapter.LastInvocation.PermissionGrant);
            Assert.True(fakeAdapter.LastInvocation.PermissionGrant!.ReadFiles);
            Assert.False(fakeAdapter.LastInvocation.PermissionGrant!.WriteFiles);
            Assert.False(fakeAdapter.LastInvocation.PermissionGrant!.RunShellCommands);
            Assert.False(fakeAdapter.LastInvocation.PermissionGrant!.NetworkAccess);
            Assert.Equal(roomDir, fakeAdapter.LastInvocation.WorkingDirectory);
            Assert.Equal(budget, fakeAdapter.LastInvocation.Timeout);

            // Verify escalations raised in room log
            var roomLogPath = Path.Combine(roomDir, "room.jsonl");
            var reader = new RoomEventLogReader(roomLogPath);
            var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);

            Assert.DoesNotContain(events, e => e is RoomEvent.HeldWorkDispatched);

            var escalations = events.OfType<RoomEvent.EscalationRaised>().ToList();
            Assert.Equal(2, escalations.Count);

            Assert.Equal(EscalationTrigger.Ambiguity, escalations[0].Trigger);
            var decSub = Assert.IsType<EscalationSubject.Decision>(escalations[0].Subject);
            Assert.Equal(new DecisionId("d-1"), decSub.DecisionId);

            Assert.Equal(EscalationTrigger.Direction, escalations[1].Trigger);
            var origSub = Assert.IsType<EscalationSubject.ProposedOrigination>(escalations[1].Subject);
            Assert.Equal(new WorkflowTemplateId("review-run"), origSub.TemplateId);
            Assert.Equal("artifacts/brief.md", origSub.BriefRef);
        }
        finally
        {
            Directory.Delete(roomDir, true);
        }
    }

    [Fact]
    public async Task RunTurnAsync_StubWritesNoFile_ReturnsFailed()
    {
        // Red arm note: If runner treats missing turn-actions.json as success, result will be Completed.
        var roomDir = CreateTestRoomDir();
        try
        {
            var fakeAdapter = new FakeWorkerAdapter();
            var fakeDispatcher = new FakeCoreDispatcher
            {
                Handler = (req, target) => new CoreDispatchResult(0, CoreExitReason.Natural) // No file written
            };

            var adapters = new Dictionary<string, IWorkerAdapter> { ["claude"] = fakeAdapter };
            var runner = new RoleTemplateOccupantRunner(adapters, fakeDispatcher);

            var roomState = new RoomState(new Dictionary<HeldWorkRef, HeldWorkState>(), []);
            var memoryDoc = new RoomMemoryDocument(0, "", new Dictionary<string, string>(), []);
            var input = new OrchestratorTurnInput(roomState, [], [], memoryDoc, null, IsColdStart: true, TotalEventCount: 0, RoomDirectoryPath: roomDir);

            var result = await runner.RunTurnAsync(input, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

            var failed = Assert.IsType<OccupantTurnResult.Failed>(result);
            Assert.Contains("no turn-actions.json", failed.Reason);
        }
        finally
        {
            Directory.Delete(roomDir, true);
        }
    }

    [Fact]
    public async Task RunTurnAsync_StubWritesMalformedJson_ReturnsFailed()
    {
        // Red arm note: If runner fails to validate JSON parsing, result will be Completed.
        var roomDir = CreateTestRoomDir();
        try
        {
            var fakeAdapter = new FakeWorkerAdapter();
            var fakeDispatcher = new FakeCoreDispatcher
            {
                Handler = (req, target) =>
                {
                    var outputDirVar = req.Environment.OfType<EnvironmentVariable.AerComputed>().FirstOrDefault(e => e.Name == "AER_OUTPUT_DIR");
                    Assert.NotNull(outputDirVar);
                    Directory.CreateDirectory(outputDirVar.Value);
                    File.WriteAllText(Path.Combine(outputDirVar.Value, "turn-actions.json"), "invalid json { {");
                    return new CoreDispatchResult(0, CoreExitReason.Natural);
                }
            };

            var adapters = new Dictionary<string, IWorkerAdapter> { ["claude"] = fakeAdapter };
            var runner = new RoleTemplateOccupantRunner(adapters, fakeDispatcher);

            var roomState = new RoomState(new Dictionary<HeldWorkRef, HeldWorkState>(), []);
            var memoryDoc = new RoomMemoryDocument(0, "", new Dictionary<string, string>(), []);
            var input = new OrchestratorTurnInput(roomState, [], [], memoryDoc, null, IsColdStart: true, TotalEventCount: 0, RoomDirectoryPath: roomDir);

            var result = await runner.RunTurnAsync(input, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

            var failed = Assert.IsType<OccupantTurnResult.Failed>(result);
            Assert.Contains("Failed to parse turn-actions.json", failed.Reason);
        }
        finally
        {
            Directory.Delete(roomDir, true);
        }
    }
}
