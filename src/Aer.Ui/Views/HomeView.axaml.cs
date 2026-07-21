using Aer.Ui.Core;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System;
using System.IO;

namespace Aer.Ui.Views;

/// <summary>Home (M19 Phase 2, #187): a thin Avalonia skin over <c>MainWindowViewModel.Home</c> — all state and refresh logic live in <c>Aer.Ui.Core</c>; the fallback open-row's wiring stays with the shell (<c>MainWindow</c>), which owns the session.</summary>
public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    /// <summary>The empty state's action to launch the template picker window (M22 Phase 3).</summary>
    private async void OnStartTemplateClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this) as MainWindow;
        var picker = new TemplatePickerWindow(topLevel);
        if (topLevel != null)
        {
            await picker.ShowDialog(topLevel);
        }

        if (picker.MaterializedTaskDirectoryPath is { } taskPath)
        {
            TaskDirectoryPathBox.Text = taskPath;
            if (topLevel != null)
            {
                var workflowPath = System.IO.Path.Combine(taskPath, "workflow.json");
                var bindingsPath = System.IO.Path.Combine(taskPath, "bindings.json");
                await topLevel.RunAsync(taskPath, workflowPath, bindingsPath);
            }
        }
    }

    /// <summary>The empty state's one action (Phase 5, #190): straight to the guided New Workflow flow.</summary>
    private void OnCreateWorkflowClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.CurrentSection = ShellSection.Author;
        }
    }

    /// <summary>
    /// #212: a folder picker for "Open a task" — the same <see cref="AuthorView.OnChooseWorkspaceClick"/>
    /// pattern (write the picked path into the visible text box, never a hidden field), so Open
    /// still reads from <see cref="TaskDirectoryPathBox"/> exactly as it always has.
    /// <para>
    /// Owner feedback: asked for a default task directory on Home. Recent tasks already have their
    /// own one-click cards above (<see cref="MainWindowViewModel.Home"/>'s <c>TaskCards</c>) — the
    /// best "default" for a task you've already run. What was missing was a starting point for a
    /// task you haven't opened yet: this picker now opens in the same
    /// <c>Documents/AER Flow</c> workspace root <see cref="NewWorkflowViewModel.EffectiveWorkspacePath"/>
    /// writes guided-flow output under, instead of wherever the OS last remembered — that's the one
    /// place a fresh task is actually likely to be.
    /// </para>
    /// </summary>
    private async void OnBrowseTaskDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storageProvider)
        {
            return;
        }

        var suggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(DefaultWorkspaceDirectoryPath);

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a task folder",
            AllowMultiple = false,
            SuggestedStartLocation = suggestedStartLocation,
        });

        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } localPath)
        {
            TaskDirectoryPathBox.Text = localPath;
        }
    }

    private static string DefaultWorkspaceDirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AER Flow");
}
