using Avalonia.Controls;

namespace Aer.Ui.Views;

/// <summary>The Task view (M19 Phase 2, #187): every per-task surface M14–M18 built, re-homed behavior-preserving — rendering is driven by the shell (<c>MainWindow</c>), which owns the session; Phase 3 redesigns this view's internals around the DAG.</summary>
public partial class TaskView : UserControl
{
    public TaskView() => InitializeComponent();
}
