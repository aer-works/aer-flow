using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Aer.Ui.Tests;

/// <summary>
/// M16 Phase 5 (issue #154), the milestone's completion gate: the three authoring round trips
/// named by the milestone, each driven end to end through the real <see cref="MainWindow"/> and
/// wired into the same default CI test run every other <c>Aer.Ui.Tests</c> class already runs on
/// (no dedicated workflow — the M14/M15 precedent that <c>Aer.Ui.Tests</c> is already a leaf of
/// the default <c>pixi run test</c> matrix). None of Phases 1-4's own tests exercise
/// <see cref="MainWindow.RunAsync"/> or <see cref="MainWindow.CompareToTemplateAsync"/> — this is
/// what actually proves the authoring surfaces (Phases 1-4) interoperate with M15's mutation seam
/// and M14's diff view, not just that each surface is independently correct.
/// </summary>
public class AuthoringRoundTripTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");

    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> ShellAdapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-authoring-round-trip-config-{Guid.NewGuid():N}", "recent-task-directories.json");

    private static List<string> TextsOf(StackPanel panel) => panel.Children.OfType<TextBlock>().Select(block => block.Text!).ToList();

    private static string WriteFileCommand(string outputName, string content) => OperatingSystem.IsWindows()
        ? $"echo {content}>%AER_OUTPUT_DIR%\\{outputName}"
        : $"echo {content} > \"$AER_OUTPUT_DIR/{outputName}\"";

    private static string CopyFirstInputCommand(string outputName) => OperatingSystem.IsWindows()
        ? $"type %AER_INPUT_0% >%AER_OUTPUT_DIR%\\{outputName}"
        : $"cat \"$AER_INPUT_0\" > \"$AER_OUTPUT_DIR/{outputName}\"";

    private static WorkflowDefinition ArchitectCriticTemplate(int version = 1) => new(
        new WorkflowTemplateId("architect-critic"),
        version,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", [], ["plan"], [], new RetryPolicy(1)),
            new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], [Architect], new RetryPolicy(1)),
        ]);

    private static async Task<string> WriteTemplateFileAsync(WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-authoring-round-trip-template-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition), cancellationToken);
        return path;
    }

    private static async Task<string> WriteArchitectCriticBindingsAsync(CancellationToken cancellationToken)
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                WriteFileCommand("plan", "the-plan"),
                TimeSpan.FromSeconds(30)),
            ["critic"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                CopyFirstInputCommand("review"),
                TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(Path.GetTempPath(), $"ui-authoring-round-trip-bindings-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config), cancellationToken);
        return path;
    }

    [AvaloniaFact]
    public async Task A_template_authored_from_blank_is_saved_and_run_to_terminal_over_shell_stub_bindings()
    {
        var templatePath = Path.Combine(Path.GetTempPath(), $"ui-authoring-round-trip-template-{Guid.NewGuid():N}.json");
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-authoring-round-trip-task-{Guid.NewGuid():N}");
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), ShellAdapters);
            var editor = window.ViewModel.TemplateEditor;

            window.NewTemplate();
            editor.TemplateId = "architect-critic";

            editor.AddStep();
            var architect = editor.Steps[0];
            architect.StepId = "architect";
            architect.Worker = "architect";
            architect.OutputsText = "plan";

            editor.AddStep();
            var critic = editor.Steps[1];
            critic.StepId = "critic";
            critic.Worker = "critic";
            critic.InputsText = "plan";
            critic.OutputsText = "review";
            critic.DependsOnOptions.Single(o => o.StepId == "architect").IsSelected = true;

            Assert.Empty(editor.ValidationErrors);

            await window.SaveTemplateAsync(templatePath, TestContext.Current.CancellationToken);
            Assert.Contains("Saved", editor.StatusText);

            var bindingsPath = await WriteArchitectCriticBindingsAsync(TestContext.Current.CancellationToken);

            await window.RunAsync(taskDirectory, templatePath, bindingsPath, TestContext.Current.CancellationToken);

            var statusText = window.FindViewControl<TextBlock>("StatusText")!;
            var runStatusText = window.FindViewControl<TextBlock>("RunStatusText")!;
            var stepsPanel = window.FindViewControl<StackPanel>("StepsPanel")!;

            Assert.Equal("Workflow status: Terminal", statusText.Text);
            Assert.Equal(string.Empty, runStatusText.Text);
            Assert.Equal(["architect: Succeeded", "critic: Succeeded"], TextsOf(stepsPanel));
        }
        finally
        {
            File.Delete(templatePath);
            if (Directory.Exists(taskDirectory))
            {
                DirectoryCleanup.DeleteRecursively(taskDirectory);
            }
        }
    }

    [AvaloniaFact]
    public async Task Editing_a_bound_tasks_template_shows_the_divergence_in_the_diff_view_and_leaves_the_bound_tasks_rendering_unchanged()
    {
        var originalDefinition = ArchitectCriticTemplate();
        var templatePath = await WriteTemplateFileAsync(originalDefinition, TestContext.Current.CancellationToken);
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-authoring-round-trip-diff-task-{Guid.NewGuid():N}");
        try
        {
            var snapshot = SnapshotBinder.Bind(originalDefinition);
            await SnapshotBinder.PersistAsync(
                snapshot, Path.Combine(taskDirectory, "snapshot.json"), TestContext.Current.CancellationToken);
            await using (new FlowEventLogWriter(Path.Combine(taskDirectory, "flow.jsonl")))
            {
            }

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            var stepsBeforeEdit = TextsOf(window.FindViewControl<StackPanel>("StepsPanel")!);
            Assert.NotEmpty(stepsBeforeEdit);

            await window.OpenTemplateInEditorAsync(templatePath, TestContext.Current.CancellationToken);
            var editor = window.ViewModel.TemplateEditor;

            editor.AddStep();
            var publisher = editor.Steps[2];
            publisher.StepId = "publisher";
            publisher.Worker = "publisher";
            publisher.InputsText = "review";
            publisher.OutputsText = "summary";
            publisher.DependsOnOptions.Single(o => o.StepId == "critic").IsSelected = true;

            Assert.Empty(editor.ValidationErrors);

            await window.SaveTemplateAsync(templatePath, TestContext.Current.CancellationToken);
            Assert.Equal(2, editor.Baseline!.WorkflowTemplateVersion);

            await window.CompareToTemplateAsync(templatePath, TestContext.Current.CancellationToken);

            var diffTexts = TextsOf(window.FindViewControl<StackPanel>("DiffPanel")!);
            Assert.Contains(diffTexts, text => text.Contains("+ publisher"));
            Assert.Contains(diffTexts, text => text.Contains("Bound snapshot is template version 1") && text.Contains("version 2"));

            // The diff view is read-only over the bound snapshot (M14 Phase 4's own decision of
            // record) — comparing to a diverged template must never re-render the task's own status.
            var stepsAfterDiff = TextsOf(window.FindViewControl<StackPanel>("StepsPanel")!);
            Assert.Equal(stepsBeforeEdit, stepsAfterDiff);
        }
        finally
        {
            File.Delete(templatePath);
            DirectoryCleanup.DeleteRecursively(taskDirectory);
        }
    }

    [AvaloniaFact]
    public async Task Bindings_authored_entirely_in_the_UI_drive_a_real_run_to_terminal()
    {
        var templatePath = await WriteTemplateFileAsync(ArchitectCriticTemplate(), TestContext.Current.CancellationToken);
        var bindingsPath = Path.Combine(Path.GetTempPath(), $"ui-authoring-round-trip-bindings-{Guid.NewGuid():N}.json");
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-authoring-round-trip-bindings-task-{Guid.NewGuid():N}");
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), ShellAdapters);
            var bindingsEditor = window.ViewModel.BindingsEditor;

            window.NewBindings();

            bindingsEditor.AddEntry();
            var architectEntry = bindingsEditor.Entries[0];
            architectEntry.WorkerName = "architect";
            architectEntry.Adapter = "shell";
            architectEntry.ProducedOutputsJson = "[{\"Name\":\"plan\"}]";
            architectEntry.PromptTemplate = WriteFileCommand("plan", "the-plan");

            bindingsEditor.AddEntry();
            var criticEntry = bindingsEditor.Entries[1];
            criticEntry.WorkerName = "critic";
            criticEntry.Adapter = "shell";
            criticEntry.RequiredInputsText = "plan";
            criticEntry.ProducedOutputsJson = "[{\"Name\":\"review\"}]";
            criticEntry.PromptTemplate = CopyFirstInputCommand("review");

            await window.SaveBindingsAsync(bindingsPath, TestContext.Current.CancellationToken);
            Assert.Contains("Saved", bindingsEditor.StatusText);

            var parsed = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsPath, TestContext.Current.CancellationToken);
            Assert.Equal(2, parsed.Count);

            await window.RunAsync(taskDirectory, templatePath, bindingsPath, TestContext.Current.CancellationToken);

            var statusText = window.FindViewControl<TextBlock>("StatusText")!;
            var runStatusText = window.FindViewControl<TextBlock>("RunStatusText")!;
            var stepsPanel = window.FindViewControl<StackPanel>("StepsPanel")!;

            Assert.Equal("Workflow status: Terminal", statusText.Text);
            Assert.Equal(string.Empty, runStatusText.Text);
            Assert.Equal(["architect: Succeeded", "critic: Succeeded"], TextsOf(stepsPanel));
        }
        finally
        {
            File.Delete(templatePath);
            File.Delete(bindingsPath);
            if (Directory.Exists(taskDirectory))
            {
                DirectoryCleanup.DeleteRecursively(taskDirectory);
            }
        }
    }
}
