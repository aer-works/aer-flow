using Aer.Daemon;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Xunit;

namespace Aer.Daemon.Tests;

public class OrchestratorTurnPromptTests
{
    private static string CreateTestRoomDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aer_prompt_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Render_ColdStartBanner_PresentWhenIsColdStart_AbsentWhenNot()
    {
        // Red arm note: If renderer ignores IsColdStart or always includes the banner, one of these assertions will fail.
        var roomState = new RoomState(new Dictionary<HeldWorkRef, HeldWorkState>(), []);
        var memoryDoc = new RoomMemoryDocument(0, "", new Dictionary<string, string>(), []);
        
        var coldInput = new OrchestratorTurnInput(roomState, [], [], memoryDoc, null, IsColdStart: true, TotalEventCount: 0);
        var coldPrompt = OrchestratorTurnPrompt.Render(coldInput);
        Assert.Contains("COLD-START", coldPrompt);

        var warmInput = new OrchestratorTurnInput(roomState, [], [], memoryDoc, new OrchestratorSessionCursor(0, DateTimeOffset.UtcNow), IsColdStart: false, TotalEventCount: 0);
        var warmPrompt = OrchestratorTurnPrompt.Render(warmInput);
        Assert.DoesNotContain("COLD-START", warmPrompt);
    }

    [Fact]
    public async Task Render_SectionA_FixtureInvariant_NoCursor_EnumeratesFromRecordAlone()
    {
        // Red arm note: If renderer fails to enumerate held work or open escalations from input, rendered prompt won't contain the ref or escalation details.
        var roomDir = CreateTestRoomDir();
        try
        {
            var roomLogPath = Path.Combine(roomDir, "room.jsonl");
            var reader = new RoomEventLogReader(roomLogPath);
            var laneRef = Path.Combine(roomDir, "lanes", "feature-1");
            await using (var writer = new RoomEventLogWriter(roomLogPath))
            {
                await RoomMutationInterface.DispatchHeldWorkAsync(
                    roomDir, new HeldWorkRef(laneRef), "feature", TimeSpan.FromMinutes(10), "decider-1", reader, writer, cancellationToken: TestContext.Current.CancellationToken);
                await RoomMutationInterface.RaiseEscalationAsync(
                    roomDir, new WorkerId("worker-1"), EscalationTrigger.Ambiguity, new EscalationSubject.Decision(new DecisionId("d-99")), reader, writer, cancellationToken: TestContext.Current.CancellationToken);
            }

            var input = await OrchestratorTurnInput.AssembleAsync(roomDir, [], TestContext.Current.CancellationToken);
            Assert.True(input.IsColdStart);

            var prompt = OrchestratorTurnPrompt.Render(input);

            Assert.Contains("feature-1", prompt);
            Assert.Contains("d-99", prompt);
            Assert.Contains("Ambiguity", prompt);
            Assert.Contains("turn-actions.json", prompt);
        }
        finally
        {
            Directory.Delete(roomDir, true);
        }
    }

    [Fact]
    public void Render_EventDelta_RendersTypedLines_AndActionsFileRequirementAppears()
    {
        // Red arm note: If event delta is formatted as raw JSON or actions file requirement is missing, assertions fail.
        var roomState = new RoomState(new Dictionary<HeldWorkRef, HeldWorkState>(), []);
        var memoryDoc = new RoomMemoryDocument(0, "", new Dictionary<string, string>(), []);
        var delta = new List<RoomEvent>
        {
            new RoomEvent.TurnHostDormancyCleared("operator", DateTimeOffset.UtcNow)
        };

        var input = new OrchestratorTurnInput(roomState, delta, [], memoryDoc, null, IsColdStart: true, TotalEventCount: 1);
        var prompt = OrchestratorTurnPrompt.Render(input);

        Assert.Contains("TurnHostDormancyCleared", prompt);
        Assert.Contains("operator", prompt);
        Assert.DoesNotContain("{ \"eventType\":", prompt); // Not raw JSON blob
        Assert.Contains("turn-actions.json", prompt);
        Assert.Contains("AER_OUTPUT_DIR", prompt);
    }
}
