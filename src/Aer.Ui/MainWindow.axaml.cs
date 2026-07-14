using Aer.Flow;
using Avalonia.Controls;

namespace Aer.Ui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The seam this phase exists to prove (issue #118), reaching the screen: loads
    /// <paramref name="taskDirectoryPath"/> through <see cref="TaskProjectionLoader"/> and renders
    /// its per-step statuses as plain <see cref="TextBlock"/> rows — deliberately minimal, per this
    /// phase's exclusion of "any styling worth defending". Public and directly awaitable (rather
    /// than fired from the constructor or a <c>Loaded</c> event) so a test can drive it
    /// deterministically without pumping the dispatcher on a timer.
    /// </summary>
    public async Task LoadAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var projection = await TaskProjectionLoader.LoadAsync(taskDirectoryPath, cancellationToken);

            // Unlike Aer.Flow's own library code, this continuation deliberately does not
            // ConfigureAwait(false): it touches UI controls below, which must happen back on
            // Avalonia's UI thread.
            StatusText.Text = $"Workflow status: {projection.State.Status}";
            StepsPanel.Children.Clear();
            foreach (var step in projection.State.Steps)
            {
                StepsPanel.Children.Add(new TextBlock { Text = $"{step.StepId}: {step.Status}" });
            }
        }
        catch (AerFlowException ex)
        {
            // A real GUI has no stderr/exit-code convention to fail into (Aer.Cli's Program.cs
            // boundary) — an invalid task directory or a malformed snapshot/event log renders as an
            // in-window message instead.
            StatusText.Text = ex.Message;
            StepsPanel.Children.Clear();
        }
    }
}
