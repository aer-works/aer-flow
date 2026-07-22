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
        // --print + --output-format=stream-json refuses to run at all without --verbose (confirmed
        // against the installed claude CLI) -- regression coverage for that failure mode.
        Assert.Contains("--verbose", target1.Args);
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
    public void Materialize_CreatesValidTwoStepDefinitionAndMetadata()
    {
        var (def, bindings, meta) = InteractiveSessionMaterializer.Materialize(
            sessionId: "sess-abc",
            taskDirectoryPath: "/tmp/aer/sessions/session-sess-abc",
            adapter: "claude",
            initialMessage: "Opening prompt");

        // #285: "chat" itself declares no PausePoint (a successful turn must flow straight through
        // to the anchor, uninterrupted); the downstream "turn-anchor" step declares the PausePoint,
        // targeting "chat" -- a legal, distinct-ancestor Supersede target per spec §17.1, unlike the
        // old single self-referencing step.
        Assert.Equal("interactive-session-template", def.WorkflowTemplateId.Value);
        Assert.Equal(2, def.Steps.Count);
        var chatStep = Assert.Single(def.Steps, s => s.StepId.Value == "chat");
        Assert.Null(chatStep.PausePoint);
        var anchorStep = Assert.Single(def.Steps, s => s.StepId.Value == InteractiveSessionMaterializer.AnchorStepId);
        Assert.Contains(new StepId("chat"), anchorStep.DependsOn);
        Assert.NotNull(anchorStep.PausePoint);
        Assert.Contains(new StepId("chat"), anchorStep.PausePoint!.SupersedeTargets);

        Assert.Equal(2, bindings.Count);
        Assert.True(bindings.ContainsKey("chat-worker"));
        Assert.Equal("claude", bindings["chat-worker"].Adapter);
        Assert.Equal("Opening prompt", bindings["chat-worker"].PromptTemplate);
        Assert.True(bindings.ContainsKey(InteractiveSessionMaterializer.AnchorWorkerName));
        Assert.Equal(NoOpWorkerAdapter.AdapterName, bindings[InteractiveSessionMaterializer.AnchorWorkerName].Adapter);

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
    public async Task ClaudeWorkerAdapter_DiscoverCapabilities_ReturnsModelAliasesAndCompactCommand()
    {
        var claudeAdapter = new ClaudeWorkerAdapter();
        var claudeCaps = await claudeAdapter.DiscoverCapabilitiesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("claude", claudeCaps.Vendor);
        // Claude Code has no "list models" subcommand — `--model` only documents alias examples in
        // --help, so this is a deliberately hardcoded, CLI-independent list (unlike Gemini below).
        Assert.Contains("sonnet", claudeCaps.Models);
        Assert.Contains(claudeCaps.Items, item => item.Name == "/compact");
    }

    [Fact]
    public async Task GeminiWorkerAdapter_DiscoverCapabilities_DoesNotFabricateDataWhenAgyUnavailable()
    {
        // agy is a real vendor CLI coincidentally present on some hosts (never assumed present in
        // CI — see CLAUDE.md's live-vendor-smoke-test rule). This only asserts the parts that don't
        // depend on the CLI being installed: it must never throw, and it must never report a
        // model/agent/plugin list it didn't actually observe from `agy models`/`agy agent`/
        // `agy plugin list`.
        var geminiAdapter = new GeminiWorkerAdapter();
        var geminiCaps = await geminiAdapter.DiscoverCapabilitiesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("gemini", geminiCaps.Vendor);
        Assert.Contains(geminiCaps.Items, item => item.Name == "/compact");
        Assert.Contains(geminiCaps.Items, item => item.Name == "accept-edits" && item.Kind == "mode");
        Assert.DoesNotContain(geminiCaps.Models, m => string.IsNullOrWhiteSpace(m));
    }

    [Fact]
    public async Task MaterializeToDirectoryAsync_RejectsASecondSessionAtTheSameDirectoryInsteadOfOverwriting()
    {
        var testPath = Path.Combine(Path.GetTempPath(), "test-aer-session-collision-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                "sess-first", testPath, "claude", cancellationToken: TestContext.Current.CancellationToken);

            var ex = await Assert.ThrowsAsync<TaskDirectoryAlreadyExistsException>(() =>
                InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                    "sess-second", testPath, "claude", cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains(testPath, ex.Message);

            // The rejected second attempt must not have clobbered the first session's metadata.
            var metadataPath = Path.Combine(testPath, ".aer", "session.json");
            var stillThere = await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath, TestContext.Current.CancellationToken);
            Assert.NotNull(stillThere);
            Assert.Equal(first.SessionId, stillThere.SessionId);
        }
        finally
        {
            if (Directory.Exists(testPath))
            {
                Directory.Delete(testPath, true);
            }
        }
    }

    [Fact]
    public async Task Archiving_MarksTheDirectoryButStillBlocksNameReuse_OnlyDeleteFreesTheName()
    {
        var testPath = Path.Combine(Path.GetTempPath(), "test-aer-session-archive-" + Guid.NewGuid().ToString("N"));
        try
        {
            await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                "sess-archived", testPath, "claude", cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(TaskLifecycle.IsArchived(testPath));
            await TaskLifecycle.ArchiveAsync(testPath, TestContext.Current.CancellationToken);
            Assert.True(TaskLifecycle.IsArchived(testPath));

            // Archiving alone does not free the name -- workflow.json is still on disk.
            var ex = await Assert.ThrowsAsync<TaskDirectoryAlreadyExistsException>(() =>
                InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                    "sess-second", testPath, "claude", cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("archived", ex.Message, StringComparison.OrdinalIgnoreCase);

            await TaskLifecycle.UnarchiveAsync(testPath);
            Assert.False(TaskLifecycle.IsArchived(testPath));

            // Only a real delete frees the name for reuse (M24 Phase 5 regression, #278).
            Directory.Delete(testPath, recursive: true);
            var recreated = await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                "sess-third", testPath, "claude", cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(recreated);
        }
        finally
        {
            if (Directory.Exists(testPath))
            {
                Directory.Delete(testPath, true);
            }
        }
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
