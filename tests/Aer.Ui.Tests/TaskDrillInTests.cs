using System.Text.Json;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Aer.Ui.Tests;

/// <summary>
/// M19 Phase 3 (issue #188): the per-step drill-in — <see cref="StepItemViewModel"/> built by
/// <see cref="MainWindowViewModel.RebuildTaskSteps"/> on every load, plain-language primary text,
/// needs-you-first auto-selection, selection surviving refresh, and the outputs/conversation/
/// decisions slices. Task directories built from hand-written <see cref="FlowEvent"/>s, matching
/// <see cref="MainWindowProjectionTests"/>' convention.
/// </summary>
public class TaskDrillInTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");

    private static WorkflowDefinitionSnapshot TwoStepSnapshot() => SnapshotBinder.Bind(new WorkflowDefinition(
        new WorkflowTemplateId("architect-critic"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(3)),
            new WorkflowStepDefinition(
                Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1),
                PausePoint: new PausePoint(SupersedeTargets: [Architect])),
        ]));

    private static ExecutionRequest MakeRequest(ExecutionId executionId, StepId stepId)
        => new(
            executionId,
            new WorkflowId("wf-1"),
            stepId,
            "worker",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-drillin-config-{Guid.NewGuid():N}", "recent-task-directories.json");

    private static async Task<string> CreateTaskDirectoryAsync(
        WorkflowDefinitionSnapshot snapshot, IEnumerable<FlowEvent> events, CancellationToken cancellationToken)
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-drillin-{Guid.NewGuid():N}");
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(taskDirectory, "snapshot.json"), cancellationToken);

        await using (var writer = new FlowEventLogWriter(Path.Combine(taskDirectory, "flow.jsonl")))
        {
            foreach (var flowEvent in events)
            {
                await writer.AppendAsync(flowEvent, cancellationToken);
            }
        }

        return taskDirectory;
    }

    /// <summary>Paused at critic after one architect failure + success; c-1 has a durable output file.</summary>
    private static async Task<string> CreatePausedTaskDirectoryAsync(CancellationToken cancellationToken)
    {
        var taskDirectory = await CreateTaskDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
                new FlowEvent.ExecutionFailed(new ExecutionId("a-1"), FailureClassification.Retryable),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-2"), Architect)),
                new FlowEvent.ExecutionSucceeded(new ExecutionId("a-2")),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("c-1"), Critic)),
                new FlowEvent.ExecutionSucceeded(new ExecutionId("c-1")),
                new FlowEvent.WorkflowPaused(new ExecutionId("c-1"), Critic),
            ],
            cancellationToken);

        var outputDirectory = Path.Combine(taskDirectory, "artifacts", "execution_c-1");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "review.md"), "The critique.", cancellationToken);
        return taskDirectory;
    }

    [AvaloniaFact]
    public async Task LoadAsync_builds_plain_language_step_items_and_auto_selects_the_paused_step()
    {
        var taskDirectory = await CreatePausedTaskDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            Assert.Equal("Waiting for your review", window.ViewModel.TaskHeadlineText);

            Assert.Collection(
                window.ViewModel.TaskSteps,
                architect =>
                {
                    Assert.Equal("architect", architect.StepId);
                    Assert.Equal("Done", architect.PlainStatusText);
                    Assert.Equal(
                        [
                            "Attempt 1 of 2: Failed — can be retried (a-1)",
                            "Attempt 2 of 2: Done (a-2)",
                        ],
                        architect.AttemptLines);
                    Assert.False(architect.IsPaused);
                },
                critic =>
                {
                    Assert.Equal("critic", critic.StepId);
                    Assert.Equal("Waiting for your review", critic.PlainStatusText);
                    Assert.True(critic.IsPaused);
                });

            // Needs-you-first: the paused step's drill-in opens itself, and its inline decision
            // card is the same live VM the M15 decision surface rebuilt — one authority, not two.
            var selected = Assert.IsType<StepItemViewModel>(window.ViewModel.SelectedStep);
            Assert.Equal("critic", selected.StepId);
            Assert.True(selected.IsSelected);
            Assert.Same(Assert.Single(window.ViewModel.PausedSteps), selected.PausedStep);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Output_file_preview_command_renders_into_the_preview_box()
    {
        var taskDirectory = await CreatePausedTaskDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            var critic = window.ViewModel.TaskSteps.Single(step => step.StepId == "critic");
            var file = Assert.Single(critic.OutputFiles);
            Assert.Equal("review.md (c-1)", file.Label);

            await file.PreviewCommand.ExecuteAsync(null);

            Assert.Equal("The critique.", window.FindViewControl<TextBox>("ArtifactPreviewBox")!.Text);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Selection_follows_step_id_across_refresh_and_the_dag_click_entry_point()
    {
        var taskDirectory = await CreatePausedTaskDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(taskDirectory, TestContext.Current.CancellationToken);

            window.ViewModel.SelectStepById("architect");
            Assert.Equal("architect", window.ViewModel.SelectedStep!.StepId);

            await window.RefreshAsync(TestContext.Current.CancellationToken);

            // Items are rebuilt wholesale; the selection re-anchors by step id, not instance.
            Assert.Equal("architect", window.ViewModel.SelectedStep!.StepId);
            Assert.True(window.ViewModel.SelectedStep.IsSelected);

            window.ViewModel.SelectStepById("no-such-step");
            Assert.Equal("architect", window.ViewModel.SelectedStep!.StepId);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Decision_lines_render_in_plain_language_on_the_decided_step()
    {
        var taskDirectory = await CreateTaskDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
                new FlowEvent.ExecutionSucceeded(new ExecutionId("a-1")),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("c-1"), Critic)),
                new FlowEvent.ExecutionSucceeded(new ExecutionId("c-1")),
                new FlowEvent.WorkflowPaused(new ExecutionId("c-1"), Critic),
                new FlowEvent.ExternalDecisionRecorded(
                    new DecisionId("decision-1"), new ExecutionId("c-1"), DecisionType.Supersede, Architect, null),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            var critic = window.ViewModel.TaskSteps.Single(step => step.StepId == "critic");
            Assert.Equal(
                ["Sent back to architect (decision-1 on c-1) — not carried out yet"],
                critic.DecisionLines);

            // The send-back's target step carries the same decision — it is about that step too.
            var architect = window.ViewModel.TaskSteps.Single(step => step.StepId == "architect");
            Assert.Equal(
                ["Sent back to architect (decision-1 on c-1) — not carried out yet"],
                architect.DecisionLines);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task A_recorded_transcript_surfaces_as_the_steps_conversation_and_renders_on_show()
    {
        var taskDirectory = await CreatePausedTaskDirectoryAsync(TestContext.Current.CancellationToken);
        var outputDirectory = Path.Combine(taskDirectory, "artifacts", "execution_c-1");
        var turn = JsonSerializer.Serialize(
            new { Sequence = 1, Role = "initiator", Vendor = "claude", Prompt = "p", Text = "hello" });
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "transcript.jsonl"), turn + "\n", TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            var critic = window.ViewModel.TaskSteps.Single(step => step.StepId == "critic");
            var conversation = Assert.Single(critic.Conversations);
            Assert.Equal("critic — c-1 (worker)", conversation.Label);

            conversation.ShowCommand.Execute(null);

            var conversationPanel = window.FindViewControl<StackPanel>("ConversationPanel")!;
            Assert.True(conversationPanel.Children.Count >= 2);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }
}
