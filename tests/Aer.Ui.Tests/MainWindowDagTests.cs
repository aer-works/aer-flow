using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Line = Avalonia.Controls.Shapes.Line;

namespace Aer.Ui.Tests;

/// <summary>
/// Drives the real <see cref="MainWindow"/>'s DAG rendering (M14 Phase 3, issue #120) through
/// <see cref="DagCanvas"/>'s actual rendered controls — the same headless-Avalonia approach
/// <see cref="MainWindowTests"/> established (Phase 1) — over both a bound task directory and a
/// raw, not-yet-instantiated template file, since UI spec §10 requires the graph view to render
/// both.
/// </summary>
public class MainWindowDagTests
{
    [AvaloniaFact]
    public async Task Renders_a_bound_tasks_dag_with_a_status_overlay()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-dag-window-{Guid.NewGuid():N}");
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
                    new WorkflowId("wf-ui-dag-window-e2e"),
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

            var dagCanvas = window.FindViewControl<Canvas>("DagCanvas")!;
            var nodes = dagCanvas.Children.OfType<Border>().ToList();
            var lines = dagCanvas.Children.OfType<Line>().ToList();

            Assert.Equal(3, nodes.Count);
            Assert.Equal(2, lines.Count);
            Assert.All(lines, line => Assert.Null(line.StrokeDashArray));

            var textByStepId = nodes.ToDictionary(
                node => ((TextBlock)node.Child!).Text!.Split('\n')[0],
                node => (TextBlock)node.Child!);

            Assert.Contains("Succeeded", textByStepId["architect"].Text);
            Assert.Contains("Succeeded", textByStepId["critic"].Text);
            Assert.Contains("Succeeded", textByStepId["publisher"].Text);

            // M19 Phase 5 (#190): status renders from the token system, not named framework colors.
            Assert.Equal(
                window.FindResource("Status.SucceededBg"),
                nodes.Single(node => ((TextBlock)node.Child!).Text!.StartsWith("architect")).Background);

            // architect (rank 0) sits directly above critic (rank 1), which sits above publisher (rank 2).
            Assert.Equal(0d, Canvas.GetTop(nodes.Single(node => ((TextBlock)node.Child!).Text!.StartsWith("architect"))));
            Assert.True(Canvas.GetTop(nodes.Single(node => ((TextBlock)node.Child!).Text!.StartsWith("critic"))) > 0);
            Assert.True(Canvas.GetTop(nodes.Single(node => ((TextBlock)node.Child!).Text!.StartsWith("publisher")))
                > Canvas.GetTop(nodes.Single(node => ((TextBlock)node.Child!).Text!.StartsWith("critic"))));
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Renders_a_raw_templates_dag_with_no_status_overlay_and_marks_the_pause_point()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "diamond-workflow-with-pause.json");

        var window = new MainWindow();
        await window.OpenAsync(fixturePath, TestContext.Current.CancellationToken);

        var statusText = window.FindViewControl<TextBlock>("StatusText")!;
        Assert.Contains("Template", statusText.Text);
        Assert.Contains("diamond-with-pause", statusText.Text);

        // A template is not a task: nothing per-task renders alongside the graph.
        var stepsPanel = window.FindViewControl<StackPanel>("StepsPanel")!;
        Assert.Empty(stepsPanel.Children);

        var dagCanvas = window.FindViewControl<Canvas>("DagCanvas")!;
        var nodes = dagCanvas.Children.OfType<Border>().ToList();
        var lines = dagCanvas.Children.OfType<Line>().ToList();

        Assert.Equal(4, nodes.Count);
        Assert.Equal(4, lines.Count);
        Assert.Single(lines, line => line.StrokeDashArray is not null);

        var nodeC = nodes.Single(node => ((TextBlock)node.Child!).Text!.StartsWith("c"));
        Assert.Contains("[pause -> a]", ((TextBlock)nodeC.Child!).Text);
        Assert.DoesNotContain("Succeeded", ((TextBlock)nodeC.Child!).Text);
        Assert.DoesNotContain("Pending", ((TextBlock)nodeC.Child!).Text);

        // M19 Phase 5 (#190): a template node is a plain surface — no status tint to carry.
        Assert.Equal(window.FindResource("Color.Surface"), nodeC.Background);
    }

    [AvaloniaFact]
    public async Task Opening_a_template_does_not_start_the_live_refresh_timer()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");

        var window = new MainWindow();
        await window.OpenAsync(fixturePath, TestContext.Current.CancellationToken);

        Assert.False(window.IsLiveRefreshTimerEnabled);
    }

    /// <summary>
    /// Regression test for a real M19 Phase 5 defect found post-milestone (2026-07-18): the DAG
    /// node's <c>Token</c> helper called <c>this.FindResource(key)</c>, an overload that resolves
    /// against <see cref="ThemeVariant.Default"/> unconditionally rather than the window's actual
    /// active variant — silently matching Tokens.axaml's literal <c>x:Key="Default"</c> dictionary
    /// (the light palette) even when the window is rendering Dark, unlike XAML
    /// <c>DynamicResource</c> bindings, which are <c>ActualThemeVariant</c>-aware. The prior DAG
    /// tests never caught this because they compared <c>Token</c>'s output against
    /// <c>window.FindResource(...)</c> — the same buggy call on both sides of the assertion. This
    /// test forces <see cref="ThemeVariant.Dark"/> and asserts against the literal dark-palette hex
    /// values from Tokens.axaml, so a regression to the Default-only overload fails loudly.
    /// </summary>
    [AvaloniaFact]
    public async Task Dag_node_colors_resolve_against_the_windows_actual_theme_variant_not_always_default()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-dag-theme-{Guid.NewGuid():N}");
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
                    new WorkflowId("wf-ui-dag-theme-e2e"),
                    taskDirectory,
                    snapshot,
                    bindings,
                    Path.Combine(taskDirectory, "artifacts"),
                    reader,
                    writer,
                    dispatcher,
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            var window = new MainWindow { RequestedThemeVariant = ThemeVariant.Dark };
            await window.LoadAsync(taskDirectory, TestContext.Current.CancellationToken);

            var dagCanvas = window.FindViewControl<Canvas>("DagCanvas")!;
            var architectNode = dagCanvas.Children.OfType<Border>()
                .Single(node => ((TextBlock)node.Child!).Text!.StartsWith("architect"));

            // Tokens.axaml's Dark dictionary, not Default's light palette. Values updated for the
            // post-M19 desaturated status palette (issue #206) — was #12331E/#4ADE80.
            Assert.Equal(Color.Parse("#1E2A22"), ((ISolidColorBrush)architectNode.Background!).Color);
            Assert.Equal(Color.Parse("#5FAE7C"), ((ISolidColorBrush)architectNode.BorderBrush!).Color);
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
        }
    }
}
