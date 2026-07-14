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
/// Drives the real <see cref="MainWindow"/> — not a plain-text renderer standing in for it — inside
/// a headless Avalonia session (<see cref="TestAppBuilder"/>), so the phase's "renders that task's
/// per-step statuses" claim is proven against actual rendered controls, not just the projection
/// data <see cref="TaskProjectionLoaderTests"/> already covers.
/// </summary>
public class MainWindowTests
{
    [AvaloniaFact]
    public async Task Renders_workflow_status_and_each_steps_status_from_a_real_task_directory()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-window-{Guid.NewGuid():N}");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(taskDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

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
                    new WorkflowId("wf-ui-window-e2e"),
                    taskDirectory,
                    snapshot,
                    bindings,
                    Path.Combine(taskDirectory, "artifacts"),
                    reader,
                    writer,
                    dispatcher,
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            var window = new MainWindow();
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            var statusText = window.FindControl<TextBlock>("StatusText")!;
            var stepsPanel = window.FindControl<StackPanel>("StepsPanel")!;

            Assert.Equal("Workflow status: Terminal", statusText.Text);
            var stepLines = stepsPanel.Children.OfType<TextBlock>().Select(block => block.Text).ToList();
            Assert.Equal(["architect: Succeeded", "critic: Succeeded", "publisher: Succeeded"], stepLines);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Renders_the_error_message_for_a_directory_with_no_snapshot()
    {
        var notATaskDirectory = Path.Combine(Path.GetTempPath(), $"ui-window-not-a-task-{Guid.NewGuid():N}");
        Directory.CreateDirectory(notATaskDirectory);
        try
        {
            var window = new MainWindow();
            await window.LoadAsync(notATaskDirectory, TestContext.Current.CancellationToken);

            var statusText = window.FindControl<TextBlock>("StatusText")!;
            var stepsPanel = window.FindControl<StackPanel>("StepsPanel")!;

            Assert.Contains(notATaskDirectory, statusText.Text);
            Assert.Empty(stepsPanel.Children);
        }
        finally
        {
            Directory.Delete(notATaskDirectory, recursive: true);
        }
    }
}
