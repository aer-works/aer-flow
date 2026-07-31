using Aer.Ui.Tests.TestSupport;
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

    /// <summary>Paused at critic after one architect failure + success; a-2 and c-1 each have a durable output file.</summary>
    private static async Task<string> CreatePausedTaskDirectoryAsync(CancellationToken cancellationToken)
    {
        var taskDirectory = await CreateTaskDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
                new FlowEvent.ExecutionFailed(
                    new ExecutionId("a-1"),
                    FailureClassification.Retryable,
                    "Contract not satisfied: 'plan' is missing"),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-2"), Architect)),
                new FlowEvent.ExecutionSucceeded(new ExecutionId("a-2")),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("c-1"), Critic)),
                new FlowEvent.ExecutionSucceeded(new ExecutionId("c-1")),
                new FlowEvent.WorkflowPaused(new ExecutionId("c-1"), Critic),
            ],
            cancellationToken);

        var architectOutputDirectory = Path.Combine(taskDirectory, "artifacts", "execution_a-2");
        Directory.CreateDirectory(architectOutputDirectory);
        await File.WriteAllTextAsync(Path.Combine(architectOutputDirectory, "plan"), "The plan.", cancellationToken);

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
                    // #597's polarity pair, on one surface: the failed attempt carries the reason
                    // Flow computed, the succeeded one carries none. A renderer that appended the
                    // suffix unconditionally, or dropped it entirely, fails one row or the other.
                    Assert.Equal(
                        [
                            "Attempt 1 of 2: Failed — can be retried (a-1) — Contract not satisfied: 'plan' is missing",
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
            DirectoryCleanup.DeleteRecursively(taskDirectory);
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
            Assert.True(file.IsSelected);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    /// <summary>
    /// Regression test for issue #211: the preview box used to be pure imperative control state
    /// with nothing hooking <see cref="MainWindowViewModel.SelectedStep"/> changing, so switching
    /// steps left the *previous* step's last-previewed output showing. Now it clears and
    /// auto-loads the newly-selected step's own first output, and the chip that produced the
    /// shown content carries <see cref="ArtifactFileViewModel.IsSelected"/>.
    /// </summary>
    [AvaloniaFact]
    public async Task Switching_the_selected_step_clears_and_reloads_the_output_preview()
    {
        var taskDirectory = await CreatePausedTaskDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            // Needs-you-first auto-selects critic; its own single output auto-loads too.
            var previewBox = window.FindViewControl<TextBox>("ArtifactPreviewBox")!;
            await PollUntilAsync(() => previewBox.Text == "The critique.", TestContext.Current.CancellationToken);

            var critic = window.ViewModel.TaskSteps.Single(step => step.StepId == "critic");
            Assert.True(Assert.Single(critic.OutputFiles).IsSelected);

            // Switching to architect must not keep showing critic's content — it clears, then
            // auto-loads architect's own first output.
            window.ViewModel.SelectStepById("architect");
            await PollUntilAsync(() => previewBox.Text == "The plan.", TestContext.Current.CancellationToken);

            var architect = window.ViewModel.TaskSteps.Single(step => step.StepId == "architect");
            Assert.True(Assert.Single(architect.OutputFiles).IsSelected);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    /// <summary>Polls instead of a fixed delay — the preview load is fired-and-forgotten off a PropertyChanged handler, the same genuine-race shape <see cref="MainWindowArtifactLineageAndDiffTests"/> already documented for the click-handler path.</summary>
    private static async Task PollUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, cancellationToken);
        }

        Assert.True(condition());
    }

    /// <summary>
    /// Issue #292: an ordinary step's durably-captured prompt surfaces via its own PromptFiles slice,
    /// not mixed into OutputFiles' always-visible chips -- reusing the same output-file preview
    /// mechanism (ArtifactFileViewModel/PreviewCommand) rather than a bespoke rendering path.
    /// </summary>
    [AvaloniaFact]
    public async Task A_captured_prompt_file_surfaces_as_PromptFiles_and_is_excluded_from_OutputFiles()
    {
        var taskDirectory = await CreatePausedTaskDirectoryAsync(TestContext.Current.CancellationToken);
        var outputDirectory = Path.Combine(taskDirectory, "artifacts", "execution_c-1");
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "prompt.txt"), "Review the plan.", TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            var critic = window.ViewModel.TaskSteps.Single(step => step.StepId == "critic");

            // Still just the one real output -- prompt.txt never leaks into the output-files chips.
            var outputFile = Assert.Single(critic.OutputFiles);
            Assert.Equal("review.md (c-1)", outputFile.Label);

            var promptFile = Assert.Single(critic.PromptFiles);
            Assert.Equal("Prompt (c-1)", promptFile.Label);
            Assert.True(critic.HasPromptFiles);

            await promptFile.PreviewCommand.ExecuteAsync(null);

            Assert.Equal("Review the plan.", window.FindViewControl<TextBox>("ArtifactPreviewBox")!.Text);
            Assert.True(promptFile.IsSelected);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    /// <summary>
    /// #868: selecting critic fires an auto-preview of its first output (review.md) off the
    /// unawaited <see cref="MainWindow.ShowSelectedStepFirstOutputAsync"/> fire-and-forget
    /// subscription; an explicit preview of a different file (prompt.txt) issued right after used to
    /// race it — whichever <c>File.ReadAllTextAsync</c> finished last won, regardless of which the
    /// user actually asked for last. The original CI failure (#868) caught this only by luck, on two
    /// small files whose read order was not controlled. Made reliably red here by making review.md
    /// large enough that its read is still in flight (or completes only shortly after) the explicit,
    /// tiny prompt.txt read — the same technique #868 itself suggests — so the auto-preview's
    /// completion is what's actually being raced against, not scheduler luck.
    /// </summary>
    [AvaloniaFact]
    public async Task Explicit_preview_of_a_different_file_survives_a_slower_in_flight_auto_preview()
    {
        var taskDirectory = await CreatePausedTaskDirectoryAsync(TestContext.Current.CancellationToken);
        var outputDirectory = Path.Combine(taskDirectory, "artifacts", "execution_c-1");
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "review.md"),
            new string('c', 150_000_000),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "prompt.txt"), "Review the plan.", TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);
            // Needs-you-first auto-selects critic, which fires the (unawaited) auto-preview of the
            // huge review.md off the SelectedStep PropertyChanged handler -- that read is now in
            // flight.

            var critic = window.ViewModel.TaskSteps.Single(step => step.StepId == "critic");
            var promptFile = Assert.Single(critic.PromptFiles);
            var previewBox = window.FindViewControl<TextBox>("ArtifactPreviewBox")!;

            // Issued while the huge auto-preview read is still in flight -- the explicit request.
            await promptFile.PreviewCommand.ExecuteAsync(null);
            Assert.Equal("Review the plan.", previewBox.Text);

            // Give the slower, superseded auto-preview read every chance to complete, and fail the
            // instant it clobbers the newer, explicit result rather than only checking at the end.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                Assert.Equal("Review the plan.", previewBox.Text);
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    /// <summary>
    /// #868's fix polarity check, the other direction: a genuinely newer preview must still win even
    /// though it was issued (and may complete) after an older one -- a fix that simply dropped every
    /// second overlapping request would pass the test above but break ordinary fast clicking. Forces
    /// the ordering with real, unawaited overlap: preview a large file without awaiting it, then
    /// immediately await a preview of a second, different file, and assert the second (truly the
    /// newest request) is what's showing once both have had time to finish.
    /// </summary>
    [AvaloniaFact]
    public async Task A_newer_preview_request_still_wins_even_when_issued_immediately_after_an_older_one()
    {
        var taskDirectory = await CreatePausedTaskDirectoryAsync(TestContext.Current.CancellationToken);
        var outputDirectory = Path.Combine(taskDirectory, "artifacts", "execution_c-1");
        var olderFilePath = Path.Combine(outputDirectory, "older.txt");
        var newerFilePath = Path.Combine(outputDirectory, "newer.txt");
        await File.WriteAllTextAsync(olderFilePath, "Older content.", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(newerFilePath, "Newer content.", TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            var olderPreviewTask = window.ShowArtifactPreviewAsync(olderFilePath, TestContext.Current.CancellationToken);
            var newerPreviewTask = window.ShowArtifactPreviewAsync(newerFilePath, TestContext.Current.CancellationToken);
            await Task.WhenAll(olderPreviewTask, newerPreviewTask);

            Assert.Equal("Newer content.", window.FindViewControl<TextBox>("ArtifactPreviewBox")!.Text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    [AvaloniaFact]
    public async Task A_step_with_no_captured_prompt_reports_no_prompt_files()
    {
        var taskDirectory = await CreatePausedTaskDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            var critic = window.ViewModel.TaskSteps.Single(step => step.StepId == "critic");

            Assert.False(critic.HasPromptFiles);
            Assert.Empty(critic.PromptFiles);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
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
            DirectoryCleanup.DeleteRecursively(taskDirectory);
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
                ["Sent back to architect (decision on c-1) — not carried out yet"],
                critic.DecisionLines);

            // The send-back's target step carries the same decision — it is about that step too.
            var architect = window.ViewModel.TaskSteps.Single(step => step.StepId == "architect");
            Assert.Equal(
                ["Sent back to architect (decision on c-1) — not carried out yet"],
                architect.DecisionLines);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(taskDirectory);
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
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }
}
