using Aer.Ui.Core;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System;

namespace Aer.Ui.Views;

/// <summary>Home (M19 Phase 2, #187): a thin Avalonia skin over <c>MainWindowViewModel.Home</c> — all state and refresh logic live in <c>Aer.Ui.Core</c>; the fallback open-row's wiring stays with the shell (<c>MainWindow</c>), which owns the session.</summary>
public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    /// <summary>The empty state's action to launch the template picker window (M22 Phase 3) — the
    /// same new-room flow the switcher header's "+ New" runs, shared on <see cref="MainWindow.StartNewRoomFromTemplateAsync"/>.</summary>
    private async void OnStartTemplateClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow topLevel)
        {
            return;
        }

        // The picked path is mirrored into Home's visible directory box so a subsequent manual Open
        // reads the room just created, exactly as this handler always did.
        if (await topLevel.StartNewRoomFromTemplateAsync() is { } roomPath)
        {
            RoomDirectoryPathBox.Text = roomPath;
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
    /// #212: a folder picker for "Open a room" — the same <see cref="AuthorView.OnChooseWorkspaceClick"/>
    /// pattern (write the picked path into the visible text box, never a hidden field), so Open
    /// still reads from <see cref="RoomDirectoryPathBox"/> exactly as it always has.
    /// <para>
    /// Owner feedback: asked for a default room directory. Existing rooms are one click away in the
    /// permanent switcher; what was missing was a starting point for a room you haven't opened yet, so
    /// this picker opens in the same <c>Documents/Baton</c> workspace root
    /// <see cref="NewWorkflowViewModel.EffectiveWorkspacePath"/> writes guided-flow output under,
    /// instead of wherever the OS last remembered — the one place a fresh room is actually likely to be.
    /// </para>
    /// </summary>
    private async void OnBrowseRoomDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storageProvider)
        {
            return;
        }

        var suggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(DefaultWorkspaceDirectoryPath);

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a room folder",
            AllowMultiple = false,
            SuggestedStartLocation = suggestedStartLocation,
        });

        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } localPath)
        {
            RoomDirectoryPathBox.Text = localPath;
        }
    }

    private static string DefaultWorkspaceDirectoryPath => DefaultWorkspace.RootPath;
}
