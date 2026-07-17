using Aer.Ui.Core;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Aer.Ui.Views;

/// <summary>Home (M19 Phase 2, #187): a thin Avalonia skin over <c>MainWindowViewModel.Home</c> — all state and refresh logic live in <c>Aer.Ui.Core</c>; the fallback open-row's wiring stays with the shell (<c>MainWindow</c>), which owns the session.</summary>
public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    /// <summary>The empty state's one action (Phase 5, #190): straight to the guided New Workflow flow.</summary>
    private void OnCreateWorkflowClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.CurrentSection = ShellSection.Author;
        }
    }
}
