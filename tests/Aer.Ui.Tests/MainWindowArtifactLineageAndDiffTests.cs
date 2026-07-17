using System.Text.Json;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Aer.Ui.Tests;

/// <summary>
/// Drives the real <see cref="MainWindow"/>'s artifact lineage and snapshot-vs-template diff
/// surfaces (M14 Phase 4, issue #121) through their actual rendered controls — the same
/// headless-Avalonia approach <see cref="MainWindowDagTests"/> established (Phase 3), including its
/// real-<c>MutationInterface</c>-pump fixture for the lineage tests, since only a real run produces
/// real artifact files to browse.
/// </summary>
public class MainWindowArtifactLineageAndDiffTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");
    private static readonly StepId Publisher = new("publisher");

    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-lineage-config-{Guid.NewGuid():N}", "recent-task-directories.json");

    private static List<string> TextsOf(StackPanel panel) => panel.Children.OfType<TextBlock>().Select(block => block.Text!).ToList();

    private static async Task<string> CreatePumpedTaskDirectoryAsync(CancellationToken cancellationToken)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-lineage-window-{Guid.NewGuid():N}");

        var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, cancellationToken);
        var snapshot = SnapshotBinder.Bind(definition);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(taskDirectory, "snapshot.json"), cancellationToken);

        var bindings = new Dictionary<string, WorkerBinding>
        {
            ["architect"] = new WorkerBinding.Process(
                new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                ShellWorkerCommands.WriteFile("plan", "the-plan"),
                TimeSpan.FromSeconds(30)),
            ["critic"] = new WorkerBinding.Process(
                new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                ShellWorkerCommands.CopyFirstInputTo("review"),
                TimeSpan.FromSeconds(30)),
            ["publisher"] = new WorkerBinding.Process(
                new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                ShellWorkerCommands.CopyFirstInputTo("summary"),
                TimeSpan.FromSeconds(30)),
        };

        var logPath = Path.Combine(taskDirectory, "flow.jsonl");
        await using (var writer = new FlowEventLogWriter(logPath))
        {
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-ui-lineage-window-e2e"),
                taskDirectory,
                snapshot,
                bindings,
                Path.Combine(taskDirectory, "artifacts"),
                reader,
                writer,
                dispatcher,
                cancellationToken: cancellationToken);
        }

        return taskDirectory;
    }

    [AvaloniaFact]
    public async Task LoadAsync_renders_every_executions_output_files_and_resolved_input_links()
    {
        var taskDirectory = await CreatePumpedTaskDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            var lineagePanel = window.FindViewControl<StackPanel>("LineagePanel")!;
            var texts = TextsOf(lineagePanel);

            Assert.Contains(texts, t => t.StartsWith("architect —"));
            Assert.Contains(texts, t => t.StartsWith("critic —"));
            Assert.Contains(texts, t => t.StartsWith("publisher —"));
            Assert.Contains(texts, t => t.Contains("input 'plan' <- architect"));
            Assert.Contains(texts, t => t.Contains("input 'review' <- critic"));

            var fileButtons = lineagePanel.Children.OfType<WrapPanel>()
                .SelectMany(panel => panel.Children.OfType<Button>())
                .Select(button => (string)button.Content!)
                .ToList();
            Assert.Contains("plan", fileButtons);
            Assert.Contains("review", fileButtons);
            Assert.Contains("summary", fileButtons);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ShowArtifactPreviewAsync_reads_the_real_output_files_content()
    {
        var taskDirectory = await CreatePumpedTaskDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var projection = await TaskProjectionLoader.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);
            var architectExecutionId = projection.Lineage.Executions.Single(e => e.StepId == Architect).ExecutionId;
            var filePath = Path.Combine(taskDirectory, "artifacts", $"execution_{architectExecutionId}", "plan");

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));

            await window.ShowArtifactPreviewAsync(filePath, TestContext.Current.CancellationToken);

            Assert.Contains("the-plan", window.FindViewControl<TextBox>("ArtifactPreviewBox")!.Text);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Clicking_a_rendered_artifact_button_shows_its_content_via_the_directory_LoadAsync_was_given()
    {
        // Proves the real wiring end to end: LoadAsync (not OpenAsync) is what renders these
        // buttons, so the closure each button captures must resolve against LoadAsync's own
        // taskDirectoryPath parameter, never the OpenAsync-only _currentTaskDirectoryPath field.
        var taskDirectory = await CreatePumpedTaskDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            var lineagePanel = window.FindViewControl<StackPanel>("LineagePanel")!;
            var planButton = lineagePanel.Children.OfType<WrapPanel>()
                .SelectMany(panel => panel.Children.OfType<Button>())
                .Single(button => (string)button.Content! == "plan");

            planButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            // ShowArtifactPreviewAsync is fired and forgotten by the click handler (Avalonia click
            // handlers cannot be async void-awaited by the caller), so the file read genuinely races
            // the assertion — a single Task.Yield() only proved out on Windows CI's scheduling and
            // flaked on Linux/macOS. Poll the actual rendered result instead of the scheduler.
            var previewBox = window.FindViewControl<TextBox>("ArtifactPreviewBox")!;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (string.IsNullOrEmpty(previewBox.Text) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.Contains("the-plan", previewBox.Text);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ShowArtifactPreviewAsync_truncates_a_very_large_file_defensively()
    {
        var largeFilePath = Path.Combine(Path.GetTempPath(), $"ui-artifact-preview-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(largeFilePath, new string('x', 50_000), TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));

            await window.ShowArtifactPreviewAsync(largeFilePath, TestContext.Current.CancellationToken);

            var previewText = window.FindViewControl<TextBox>("ArtifactPreviewBox")!.Text!;
            Assert.True(previewText.Length < 50_000);
            Assert.Contains("truncated", previewText);
        }
        finally
        {
            File.Delete(largeFilePath);
        }
    }

    [AvaloniaFact]
    public async Task ShowArtifactPreviewAsync_reports_a_missing_file_as_a_message_not_a_crash()
    {
        var missingFilePath = Path.Combine(Path.GetTempPath(), $"ui-artifact-preview-missing-{Guid.NewGuid():N}.txt");
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));

        await window.ShowArtifactPreviewAsync(missingFilePath, TestContext.Current.CancellationToken);

        Assert.Contains("Cannot preview", window.FindViewControl<TextBox>("ArtifactPreviewBox")!.Text);
    }

    private static WorkflowDefinition BaselineTemplate(int version = 1) => new(
        new WorkflowTemplateId("architect-critic"),
        version,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(3)),
            new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
        ]);

    private static async Task<string> CreateBoundTaskDirectoryAsync(WorkflowDefinitionSnapshot snapshot, CancellationToken cancellationToken)
    {
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-diff-task-{Guid.NewGuid():N}");
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(taskDirectory, "snapshot.json"), cancellationToken);
        await using (new FlowEventLogWriter(Path.Combine(taskDirectory, "flow.jsonl")))
        {
        }

        return taskDirectory;
    }

    private static async Task<string> WriteTemplateFileAsync(WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-diff-template-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition), cancellationToken);
        return path;
    }

    [AvaloniaFact]
    public async Task CompareToTemplateAsync_before_any_task_is_open_reports_that_a_task_must_be_open_first()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        var templatePath = await WriteTemplateFileAsync(BaselineTemplate(), TestContext.Current.CancellationToken);
        try
        {
            await window.CompareToTemplateAsync(templatePath, TestContext.Current.CancellationToken);

            Assert.Equal(
                ["Open a task directory before comparing it to a template."],
                TextsOf(window.FindViewControl<StackPanel>("DiffPanel")!));
        }
        finally
        {
            File.Delete(templatePath);
        }
    }

    [AvaloniaFact]
    public async Task CompareToTemplateAsync_reports_no_divergence_for_a_structurally_identical_template()
    {
        var snapshot = SnapshotBinder.Bind(BaselineTemplate());
        var taskDirectory = await CreateBoundTaskDirectoryAsync(snapshot, TestContext.Current.CancellationToken);
        var templatePath = await WriteTemplateFileAsync(BaselineTemplate(), TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            await window.CompareToTemplateAsync(templatePath, TestContext.Current.CancellationToken);

            var texts = TextsOf(window.FindViewControl<StackPanel>("DiffPanel")!);
            Assert.Contains("No divergence: the bound snapshot matches the current template.", texts);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
            File.Delete(templatePath);
        }
    }

    [AvaloniaFact]
    public async Task CompareToTemplateAsync_reports_a_mismatch_for_an_unrelated_template_never_a_diff()
    {
        var snapshot = SnapshotBinder.Bind(BaselineTemplate());
        var taskDirectory = await CreateBoundTaskDirectoryAsync(snapshot, TestContext.Current.CancellationToken);
        var unrelatedTemplate = BaselineTemplate() with { WorkflowTemplateId = new WorkflowTemplateId("something-else") };
        var templatePath = await WriteTemplateFileAsync(unrelatedTemplate, TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            await window.CompareToTemplateAsync(templatePath, TestContext.Current.CancellationToken);

            var texts = TextsOf(window.FindViewControl<StackPanel>("DiffPanel")!);
            Assert.Single(texts);
            Assert.Contains("mismatch, not a divergence", texts[0]);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
            File.Delete(templatePath);
        }
    }

    [AvaloniaFact]
    public async Task CompareToTemplateAsync_reports_added_removed_and_changed_steps()
    {
        var snapshot = SnapshotBinder.Bind(BaselineTemplate());
        var taskDirectory = await CreateBoundTaskDirectoryAsync(snapshot, TestContext.Current.CancellationToken);

        var editedTemplate = new WorkflowDefinition(
            new WorkflowTemplateId("architect-critic"),
            2,
            Steps:
            [
                new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(5)),
                new WorkflowStepDefinition(
                    Publisher, "publisher", ["plan"], ["summary"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
            ]);
        var templatePath = await WriteTemplateFileAsync(editedTemplate, TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            await window.CompareToTemplateAsync(templatePath, TestContext.Current.CancellationToken);

            var texts = TextsOf(window.FindViewControl<StackPanel>("DiffPanel")!);
            Assert.Contains(texts, t => t.StartsWith("+ publisher"));
            Assert.Contains(texts, t => t.StartsWith("- critic"));
            Assert.Contains(texts, t => t.StartsWith("~ architect changed: retryPolicy"));
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
            File.Delete(templatePath);
        }
    }
}
