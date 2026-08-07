using Aer.Adapters;
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

        if (picker.MaterializedRoomDirectoryPath is { } roomPath)
        {
            RoomDirectoryPathBox.Text = roomPath;
            if (topLevel != null)
            {
                // A record becomes visible the moment it exists on disk, not when its first
                // execution happens to succeed. Materializing a workflow used to leave it listed
                // nowhere: Run registers the room only on a 2xx (RoomClient.RunAsync's
                // _reopenRoomAsync call), so a run that was refused — "something else is already
                // running against this directory" — created a real room directory that no surface
                // knew about. Found by running the app: a freshly created room was reachable only
                // through the folder picker.
                await topLevel.RefreshRecordListsAsync();
                if (await InteractiveSessionMaterializer.ReadRoomKindAsync(roomPath) == RoomKind.Interactive)
                {
                    // A chat/codebase session's initial turn is already dispatched (or about to be)
                    // by the daemon's own fire-and-forget background task -- Open, not Run, so this
                    // doesn't start a second, competing execution against the same room directory
                    // (M24 Phase 1 desktop chat UI, issue #262). Open also routes to the dedicated
                    // Chat view once it detects the interactive room marker, which Run never did.
                    await topLevel.OpenAsync(roomPath);
                }
                else
                {
                    var workflowPath = System.IO.Path.Combine(roomPath, "workflow.json");
                    var bindingsPath = System.IO.Path.Combine(roomPath, "bindings.json"); // vocabulary-ok: technical file path
                    await topLevel.RunAsync(roomPath, workflowPath, bindingsPath);
                }
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
    /// #212: a folder picker for "Open a room" — the same <see cref="AuthorView.OnChooseWorkspaceClick"/>
    /// pattern (write the picked path into the visible text box, never a hidden field), so Open
    /// still reads from <see cref="RoomDirectoryPathBox"/> exactly as it always has.
    /// <para>
    /// Owner feedback: asked for a default room directory on Home. Recent rooms already have their
    /// own one-click cards above (<see cref="MainWindowViewModel.Home"/>'s <c>RoomCards</c>) — the
    /// best "default" for a room you've already run. What was missing was a starting point for a
    /// room you haven't opened yet: this picker now opens in the same
    /// <c>Documents/Baton</c> workspace root <see cref="NewWorkflowViewModel.EffectiveWorkspacePath"/>
    /// writes guided-flow output under, instead of wherever the OS last remembered — that's the one
    /// place a fresh room is actually likely to be.
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
