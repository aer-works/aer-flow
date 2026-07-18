using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace Aer.Ui;

public class ExitConfirmationWindow : Window
{
    private bool? _cancelTask;

    public ExitConfirmationWindow(bool hasRunningTasks)
    {
        Title = "Exit AER Flow";
        Width = 520;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        
        FontFamily = new FontFamily("Inter, Outfit, Roboto, system-ui");

        // Bind Window properties dynamically to support dark and light theme resource switching
        this.Bind(BackgroundProperty, this.GetResourceObservable("Color.Background"));
        this.Bind(ForegroundProperty, this.GetResourceObservable("Color.Text"));

        // Content stack
        var mainStack = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        
        var messageText = hasRunningTasks
            ? "An active task is running. Cancel task and exit, or quit UI only (leaving task running)?"
            : "Close AER Flow? You can stop the background daemon and exit, or quit the UI only (leaving the daemon running).";

        var message = new TextBlock
        {
            Text = messageText,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            FontSize = 13
        };
        message.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Color.TextSecondary"));
        mainStack.Children.Add(message);

        var buttonsStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 12
        };

        var cancelAndExitText = hasRunningTasks ? "Cancel Task & Exit" : "Stop Daemon & Exit";
        var cancelAndExitButton = new Button
        {
            Content = cancelAndExitText,
            Padding = new Thickness(16, 8),
            FontWeight = FontWeight.Medium,
            FontSize = 12
        };
        cancelAndExitButton.Bind(Button.BackgroundProperty, this.GetResourceObservable("Color.Accent"));
        cancelAndExitButton.Foreground = Brushes.White;
        
        var quitUiOnlyButton = new Button
        {
            Content = "Quit UI Only",
            Padding = new Thickness(16, 8),
            FontWeight = FontWeight.Medium,
            FontSize = 12
        };

        var keepRunningButton = new Button
        {
            Content = "Keep Running",
            Padding = new Thickness(16, 8),
            FontWeight = FontWeight.Medium,
            FontSize = 12
        };

        cancelAndExitButton.Click += (s, e) => { _cancelTask = true; Close(); };
        quitUiOnlyButton.Click += (s, e) => { _cancelTask = false; Close(); };
        keepRunningButton.Click += (s, e) => { _cancelTask = null; Close(); };

        buttonsStack.Children.Add(cancelAndExitButton);
        buttonsStack.Children.Add(quitUiOnlyButton);
        buttonsStack.Children.Add(keepRunningButton);

        mainStack.Children.Add(buttonsStack);
        
        Content = mainStack;
    }

    public static async Task<bool?> ShowPromptAsync(Window parent, bool hasRunningTasks)
    {
        var dialog = new ExitConfirmationWindow(hasRunningTasks);
        await dialog.ShowDialog(parent);
        return dialog._cancelTask;
    }
}
