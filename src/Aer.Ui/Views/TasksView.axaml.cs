using Avalonia.Controls;

namespace Aer.Ui.Views;

/// <summary>Tasks (M24 Phase 5, #278): a thin Avalonia skin over <c>MainWindowViewModel.Tasks</c>; wired to the shell under the same contract <c>ChatView</c> documents.</summary>
public partial class TasksView : UserControl
{
    public TasksView() => InitializeComponent();
}
