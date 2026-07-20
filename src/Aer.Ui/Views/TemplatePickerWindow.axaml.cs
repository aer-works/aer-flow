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
            SecondaryVendorPanel.IsVisible = ReviewRunRadio.IsChecked == true;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private async void OnStartClick(object? sender, RoutedEventArgs e)
    {
        var templateId = ReviewRunRadio.IsChecked == true ? "review-run" : "solo-run";
        var primaryVendor = PrimaryVendorCombo.SelectedItem?.ToString() ?? "claude";
        var secondaryVendor = ReviewRunRadio.IsChecked == true ? (SecondaryVendorCombo.SelectedItem?.ToString() ?? primaryVendor) : null;
        var taskName = string.IsNullOrWhiteSpace(TaskNameBox.Text) ? $"task-{DateTime.UtcNow:yyyyMMddHHmmss}" : TaskNameBox.Text.Trim();
        var customPrompt = string.IsNullOrWhiteSpace(CustomPromptBox.Text) ? null : CustomPromptBox.Text.Trim();

        var baseTasksDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aer", "tasks");
        var taskDirectoryPath = Path.GetFullPath(Path.Combine(baseTasksDir, taskName));
        if (!taskDirectoryPath.StartsWith(Path.GetFullPath(baseTasksDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            Close(false);
            return;
        }

        try
        {
            await BuiltInWorkflowTemplates.MaterializeToDirectoryAsync(
                templateId,
                primaryVendor,
                secondaryVendor,
                taskDirectoryPath,
                customPrompt).ConfigureAwait(true);

            MaterializedTaskDirectoryPath = taskDirectoryPath;
            Close(true);
        }
        catch
        {
            Close(false);
        }
    }
}
