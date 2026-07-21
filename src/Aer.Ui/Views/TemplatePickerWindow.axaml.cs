using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Aer.Adapters;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Aer.Ui.Views;

public partial class TemplatePickerWindow : Window
{
    public string? MaterializedTaskDirectoryPath { get; private set; }

    public TemplatePickerWindow()
    {
        InitializeComponent();
        PopulateVendors();
    }

    private void PopulateVendors()
    {
        var probed = VendorCliPresence.Probe();
        var available = probed.Where(p => p.IsAvailable).Select(p => p.AdapterName).ToList();
        if (available.Count == 0)
        {
            available = ["claude", "gemini"];
        }

        PrimaryVendorCombo.ItemsSource = available;
        PrimaryVendorCombo.SelectedIndex = 0;

        SecondaryVendorCombo.ItemsSource = available;
        SecondaryVendorCombo.SelectedIndex = available.Count > 1 ? 1 : 0;
    }

    private void OnTemplateChanged(object? sender, RoutedEventArgs e)
    {
        if (SecondaryVendorPanel != null)
        {
            SecondaryVendorPanel.IsVisible = ReviewRunRadio.IsChecked == true || TwoVendorDialogueRadio.IsChecked == true;
        }
        if (ProjectDirectoryPanel != null)
        {
            ProjectDirectoryPanel.IsVisible = CodebaseSessionRadio.IsChecked == true;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private async void OnStartClick(object? sender, RoutedEventArgs e)
    {
        string templateId;
        if (ChatSessionRadio.IsChecked == true) templateId = "chat-session";
        else if (CodebaseSessionRadio.IsChecked == true) templateId = "codebase-session";
        else if (TwoVendorDialogueRadio.IsChecked == true) templateId = "two-vendor-dialogue";
        else if (ReviewRunRadio.IsChecked == true) templateId = "review-run";
        else templateId = "solo-run";

        var primaryVendor = PrimaryVendorCombo.SelectedItem?.ToString() ?? "claude";
        var secondaryVendor = (ReviewRunRadio.IsChecked == true || TwoVendorDialogueRadio.IsChecked == true)
            ? (SecondaryVendorCombo.SelectedItem?.ToString() ?? primaryVendor)
            : null;
        var taskName = string.IsNullOrWhiteSpace(TaskNameBox.Text) ? $"task-{DateTime.UtcNow:yyyyMMddHHmmss}" : TaskNameBox.Text.Trim();
        var customPrompt = string.IsNullOrWhiteSpace(CustomPromptBox.Text) ? null : CustomPromptBox.Text.Trim();
        var secondaryCustomPrompt = string.IsNullOrWhiteSpace(SecondaryCustomPromptBox.Text) ? null : SecondaryCustomPromptBox.Text.Trim();

        var baseTasksDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aer", "tasks");
        var taskDirectoryPath = Path.GetFullPath(Path.Combine(baseTasksDir, taskName));
        if (!taskDirectoryPath.StartsWith(Path.GetFullPath(baseTasksDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            Close(false);
            return;
        }

        try
        {
            if (templateId == "chat-session" || templateId == "codebase-session")
            {
                var workDir = CodebaseSessionRadio.IsChecked == true && !string.IsNullOrWhiteSpace(ProjectDirectoryBox.Text)
                    ? ProjectDirectoryBox.Text.Trim()
                    : null;

                var sessionId = Guid.NewGuid().ToString("N")[..12];
                await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                    sessionId: sessionId,
                    taskDirectoryPath: taskDirectoryPath,
                    adapter: primaryVendor,
                    model: null,
                    workingDirectory: workDir,
                    initialMessage: customPrompt).ConfigureAwait(true);
            }
            else
            {
                await BuiltInWorkflowTemplates.MaterializeToDirectoryAsync(
                    templateId,
                    primaryVendor,
                    secondaryVendor,
                    taskDirectoryPath,
                    customPrompt,
                    secondaryCustomPrompt).ConfigureAwait(true);
            }

            MaterializedTaskDirectoryPath = taskDirectoryPath;
            Close(true);
        }
        catch
        {
            Close(false);
        }
    }
}
