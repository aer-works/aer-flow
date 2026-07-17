using Aer.Ui.Core;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Aer.Ui.Views;

/// <summary>The Task view (M19 Phase 3, #188): needs-you-first inline decisions, the DAG as the primary surface with per-step drill-in, plain-language primary text, and the full precise record in the Details disclosure. Rendering is driven by the shell (<c>MainWindow</c>), which owns the session.</summary>
public partial class TaskView : UserControl
{
    public TaskView() => InitializeComponent();

    /// <summary>
    /// The feedback-file picker (the phase's "picker, not a typed path" requirement) — a real OS
    /// file dialog writing into <see cref="PausedStepViewModel.RevisionFilePath"/>, the same
    /// property the visible text box binds (still swappable by hand, and what headless tests set
    /// directly — a dialog cannot be driven headlessly).
    /// </summary>
    private async void OnChooseFeedbackFileClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not PausedStepViewModel pausedStep ||
            TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storageProvider)
        {
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the feedback file",
            AllowMultiple = false,
        });

        if (files.Count == 1 && files[0].TryGetLocalPath() is { } localPath)
        {
            pausedStep.RevisionFilePath = localPath;
        }
    }
}
