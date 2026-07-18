using Aer.Adapters;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Aer.Ui.Tests;

/// <summary>
/// M19's completion gate (Phase 6, issue #191): the whole non-expert path, headless, through the
/// new UI's actual controls — author a workflow (including a dialogue step) in the guided flow
/// with zero hand-authored config files, run to the review gate over stub CLIs, read the
/// conversation at the gate, send back with a feedback file, and approve to terminal. Vendor
/// adapters are stubbed exactly like every M15 round trip (<see cref="ShellCommandWorkerAdapter"/>
/// for the single-vendor step; a script-backed stub for the dialogue step that produces a real
/// durable transcript, since §10.1 discovery is by artifact content alone — the live dialogue
/// execution itself is M18's already-proven gate, not this one's).
/// </summary>
public class NonExpertPathGateTests
{
    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-gate-config-{Guid.NewGuid():N}", "recent-task-directories.json");

    /// <summary>A stub "dialogue" adapter dispatching a local script that writes a schema-valid transcript plus the declared final output — the worker boundary's shape, none of its vendors.</summary>
    private sealed class StubDialogueScriptAdapter(string scriptPath) : IWorkerAdapter
    {
        public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract) => OperatingSystem.IsWindows()
            ? new CoreDispatchTarget(
                "powershell", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath])
            : new CoreDispatchTarget("sh", [scriptPath]);
    }

    private static string WriteDialogueStubScript(string directory)
    {
        Directory.CreateDirectory(directory);
        const string turnOne = "{\"Sequence\":1,\"Role\":\"initiator\",\"Vendor\":\"claude\",\"Prompt\":\"p1\",\"Text\":\"For.\"}";
        const string turnTwo = "{\"Sequence\":2,\"Role\":\"responder\",\"Vendor\":\"gemini\",\"Prompt\":\"p2\",\"Text\":\"Against.\"}";

        if (OperatingSystem.IsWindows())
        {
            // Single-quoted PowerShell strings take " literally — no escaping needed for JSON content
            // that (like this fixture's) contains no embedded '.
            var scriptPath = Path.Combine(directory, "dialogue-stub.ps1");
            File.WriteAllText(
                scriptPath,
                "$out = $env:AER_OUTPUT_DIR\r\n" +
                $"Set-Content -Path (Join-Path $out 'transcript.jsonl') -Value '{turnOne}', '{turnTwo}'\r\n" +
                "Set-Content -Path (Join-Path $out 'verdict.md') -Value 'The verdict.'\r\n");
            return scriptPath;
        }

        var shPath = Path.Combine(directory, "dialogue-stub.sh");
        File.WriteAllText(
            shPath,
            "#!/bin/sh\n" +
            $"printf '%s\\n' '{turnOne}' '{turnTwo}' > \"$AER_OUTPUT_DIR/transcript.jsonl\"\n" +
            "echo 'The verdict.' > \"$AER_OUTPUT_DIR/verdict.md\"\n");
        return shPath;
    }

    [AvaloniaFact]
    public async Task The_full_non_expert_path_authors_runs_reviews_sends_back_and_finishes_headlessly()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-nonexpert-gate-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(testRoot, "workspace");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var dialogueScriptPath = WriteDialogueStubScript(Path.Combine(testRoot, "scripts"));
            var adapters = new Dictionary<string, IWorkerAdapter>
            {
                ["claude"] = new ShellCommandWorkerAdapter(),
                ["dialogue"] = new StubDialogueScriptAdapter(dialogueScriptPath),
            };
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), adapters);

            // 1. Author in the guided flow — zero hand-authored config files.
            var flow = window.ViewModel.NewWorkflow;
            flow.WorkflowName = "draft-and-debate";
            flow.WorkspaceOverridePath = workspacePath;

            flow.AddStepCommand.Execute(null);
            var draft = flow.Steps[0];
            draft.Name = "draft";
            draft.ProducesFileName = "draft.md";
            draft.Prompt = OperatingSystem.IsWindows()
                ? "echo the-draft>%AER_OUTPUT_DIR%\\draft.md"
                : "echo the-draft > \"$AER_OUTPUT_DIR/draft.md\"";

            flow.AddStepCommand.Execute(null);
            var debate = flow.Steps[1];
            debate.Name = "debate";
            debate.Kind = GuidedStepKind.Dialogue;
            debate.ProducesFileName = "verdict.md";
            debate.SeedPrompt = "Debate the draft.";
            debate.TurnBudgetText = "2";
            debate.InitiatorPreamble = "Argue for.";
            debate.ResponderPreamble = "Argue against.";
            debate.HasReviewGate = true;
            debate.DependsOnOptions.Single(option => option.StepName == "draft").IsSelected = true;

            var paths = await flow.SaveAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(paths);
            Assert.True(File.Exists(Path.Combine(workspacePath, "dialogue-debate.json")));

            // 2. Run to the review gate over the stubs.
            await window.RunAsync(
                taskDirectory, paths.Value.WorkflowFilePath, paths.Value.BindingsFilePath,
                TestContext.Current.CancellationToken);

            Assert.Equal("Waiting for your review", window.ViewModel.TaskHeadlineText);
            var pausedStep = Assert.Single(window.ViewModel.PausedSteps);
            Assert.Equal(new StepId("debate"), pausedStep.StepId);
            var sendBackTarget = Assert.Single(pausedStep.SendBackTargets);
            Assert.Equal("Send back to draft", sendBackTarget.Label);

            // 3. Read the conversation at the gate — the drill-in's per-step slice of M18's view.
            var debateItem = window.ViewModel.TaskSteps.Single(step => step.StepId == "debate");
            var conversation = Assert.Single(debateItem.Conversations);
            conversation.ShowCommand.Execute(null);
            var conversationPanel = window.FindViewControl<StackPanel>("ConversationPanel")!;
            Assert.True(conversationPanel.Children.Count >= 3, "expected the label plus both turns");

            // 4. Send back with a feedback file (the mandatory §7 artifact, via the same property
            //    the picker writes).
            var feedbackFilePath = Path.Combine(testRoot, "feedback.md");
            await File.WriteAllTextAsync(feedbackFilePath, "Tighten the argument.", TestContext.Current.CancellationToken);
            pausedStep.RevisionFilePath = feedbackFilePath;
            pausedStep.SupplementaryWorker = "human";
            pausedStep.SupplementaryOutputName = "feedback";
            await sendBackTarget.SendBackCommand.ExecuteAsync(null);

            var repausedStep = Assert.Single(window.ViewModel.PausedSteps);
            Assert.Equal(new StepId("debate"), repausedStep.StepId);
            Assert.NotEqual(pausedStep.ExecutionId, repausedStep.ExecutionId);

            // 5. Approve to terminal; the finished task reads plainly and its outputs exist.
            await repausedStep.ApproveCommand.ExecuteAsync(null);

            Assert.Equal("Finished", window.ViewModel.TaskHeadlineText);
            var stepsPanel = window.FindViewControl<StackPanel>("StepsPanel")!;
            Assert.Equal(
                ["draft: Succeeded", "debate: Succeeded"],
                stepsPanel.Children.OfType<TextBlock>().Select(block => block.Text).ToList());

            var finishedDebate = window.ViewModel.TaskSteps.Single(step => step.StepId == "debate");
            Assert.Contains(finishedDebate.OutputFiles, file => file.Label.StartsWith("verdict.md"));
            Assert.Equal("Done", finishedDebate.PlainStatusText);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
