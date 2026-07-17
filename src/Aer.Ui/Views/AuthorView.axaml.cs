using Aer.Ui.Core;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Aer.Ui.Views;

/// <summary>The Author view (M19 Phase 4, #189): the guided New Workflow flow — form-first, pickers not typed paths — with the M16 file editors kept below as the advanced disclosure. Flow state and file I/O live on the ViewModels in <c>Aer.Ui.Core</c>.</summary>
public partial class AuthorView : UserControl
{
    public AuthorView() => InitializeComponent();

    /// <summary>The workspace folder picker — writes the same visible, hand-swappable override property headless tests set directly (the Phase 3 feedback-file picker's precedent).</summary>
    private async void OnChooseWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel ||
            TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storageProvider)
        {
            return;
        }

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where this workflow's files live",
            AllowMultiple = false,
        });

        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } localPath)
        {
            viewModel.NewWorkflow.WorkspaceOverridePath = localPath;
        }
    }
}
