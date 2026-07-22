using Avalonia.Controls;

namespace Aer.Ui.Views;

/// <summary>Tasks (M24 Phase 5, #278): a thin Avalonia skin over <c>MainWindowViewModel.Tasks</c> — all state and daemon calls live in <c>Aer.Ui.Core</c>; button wiring stays with the shell (<c>MainWindow</c>), which owns the <c>TaskSession</c> this view's actions need.</summary>
public partial class TasksView : UserControl
{
    public TasksView() => InitializeComponent();
}
