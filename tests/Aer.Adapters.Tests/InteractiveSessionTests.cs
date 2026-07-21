using Aer.Adapters;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Xunit;

namespace Aer.Adapters.Tests;

public sealed class InteractiveSessionTests
{
    private readonly WorkerContract _contract = new(
        WorkerName: "chat-worker",
        RequiredInputs: [],
        ProducedOutputs: [new ProducedOutput("response.md")],
        OptionalMetadata: []);

    [Fact]
    public void ClaudeWorkerAdapter_ResolvesSessionFlags_FirstTurnAndResumed()
    {
        var adapter = new ClaudeWorkerAdapter();

        // Turn 1: new session ID -> --session-id <uuid>
        var invTurn1 = new WorkerInvocation(
            PromptTemplate: "Hello",
            SessionId: "session-123",
            ResumeSession: false,
            MinimalOverhead: true,
            StreamJson: true);

        var target1 = adapter.Resolve(invTurn1, _contract);
        Assert.Contains("--session-id", target1.Args);
        Assert.Contains("session-123", target1.Args);
        Assert.Contains("--bare", target1.Args);
        Assert.Contains("stream-json", target1.Args);
        Assert.Contains("--include-partial-messages", target1.Args);
        Assert.DoesNotContain("--resume", target1.Args);

        // Turn 2: resume session -> --resume <uuid>
        var invTurn2 = new WorkerInvocation(
            PromptTemplate: "Next message",
            SessionId: "session-123",
            ResumeSession: true,
            MinimalOverhead: true,
            StreamJson: true);

        var target2 = adapter.Resolve(invTurn2, _contract);
        Assert.Contains("--resume", target2.Args);
        Assert.Contains("session-123", target2.Args);
        Assert.DoesNotContain("--session-id", target2.Args);
    }

    [Fact]
    public void GeminiWorkerAdapter_ResolvesSessionFlags_ConversationAndLogFile()
    {
        var adapter = new GeminiWorkerAdapter();

        // Turn 1: initial -> --log-file
        var invTurn1 = new WorkerInvocation(
            PromptTemplate: "Hello",
            SessionId: null,
            ResumeSession: false,
            LogFilePath: "/tmp/agy-log.txt");

        var target1 = adapter.Resolve(invTurn1, _contract);
        Assert.Contains("--log-file", target1.Args);
        Assert.Contains("/tmp/agy-log.txt", target1.Args);
        Assert.DoesNotContain("--conversation", target1.Args);

        // Turn 2: resume -> --conversation <id>
        var invTurn2 = new WorkerInvocation(
            PromptTemplate: "Next message",
            SessionId: "conv-999",
            ResumeSession: true);

        var target2 = adapter.Resolve(invTurn2, _contract);
        Assert.Contains("--conversation", target2.Args);
        Assert.Contains("conv-999", target2.Args);
    }

    [Fact]
    public void Materialize_CreatesValidSingleStepDefinitionAndMetadata()
    {
        var (def, bindings, meta) = InteractiveSessionMaterializer.Materialize(
            sessionId: "sess-abc",
            taskDirectoryPath: "/tmp/aer/sessions/session-sess-abc",
            adapter: "claude",
            initialMessage: "Opening prompt");

        Assert.Equal("interactive-session-template", def.WorkflowTemplateId.Value);
        Assert.Single(def.Steps);
        Assert.Equal("chat", def.Steps[0].StepId.Value);
        Assert.NotNull(def.Steps[0].PausePoint);

        Assert.Single(bindings);
        Assert.True(bindings.ContainsKey("chat-worker"));
        Assert.Equal("claude", bindings["chat-worker"].Adapter);
        Assert.Equal("Opening prompt", bindings["chat-worker"].PromptTemplate);

        Assert.Equal("sess-abc", meta.SessionId);
        Assert.Equal("claude", meta.CurrentAdapter);
        Assert.Equal(0, meta.TurnCount);
    }

    [Fact]
    public void SynthesizeContextSummary_FormatsHistoryCorrectly()
    {
        List<SessionTurn> turns =
        [
            new SessionTurn(1, "claude", "What is 2+2?", "4", DateTimeOffset.UtcNow, false, false),
            new SessionTurn(2, "claude", "What is 3+3?", "6", DateTimeOffset.UtcNow, true, false)
        ];

        var summary = InteractiveSessionMaterializer.SynthesizeContextSummary(turns, "What is 4+4?");
        Assert.Contains("User: What is 2+2?", summary);
        Assert.Contains("Assistant: 4", summary);
        Assert.Contains("User: What is 3+3?", summary);
        Assert.Contains("Now continue with the following user request:", summary);
        Assert.Contains("What is 4+4?", summary);
    }

    [Fact]
    public void ClaudeAndGeminiWorkerAdapters_DiscoverCapabilities_ReturnsNonEmptyItemsAndModels()
    {
        var claudeAdapter = new ClaudeWorkerAdapter();
        var claudeCaps = claudeAdapter.DiscoverCapabilities();

        Assert.Equal("claude", claudeCaps.Vendor);
        Assert.Contains("claude-3-5-sonnet", claudeCaps.Models);
        Assert.Contains(claudeCaps.Items, item => item.Name == "/compact");

        var geminiAdapter = new GeminiWorkerAdapter();
        var geminiCaps = geminiAdapter.DiscoverCapabilities();

        Assert.Equal("gemini", geminiCaps.Vendor);
        Assert.Contains("gemini-1.5-pro", geminiCaps.Models);
        Assert.Contains(geminiCaps.Items, item => item.Name == "/compact");
    }

    [Fact]
    public async Task KnownProjectsStore_AddsAndRetrievesProject()
    {
        var testPath = Path.Combine(Path.GetTempPath(), "test-aer-project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testPath);
        try
        {
            await KnownProjectsStore.AddOrUpdateProjectAsync(testPath, "Test Project", TestContext.Current.CancellationToken);
            var projects = await KnownProjectsStore.LoadProjectsAsync(TestContext.Current.CancellationToken);
            Assert.Contains(projects, p => p.FriendlyName == "Test Project" && string.Equals(p.Path, Path.GetFullPath(testPath), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(testPath))
            {
                Directory.Delete(testPath, true);
            }
        }
    }
}
