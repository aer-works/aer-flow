using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace Aer.Ui;

public class ExitConfirmationWindow : Window
{
    private bool? _cancelTask;

    public ExitConfirmationWindow()
    {
        Title = "Exit AER Flow";
        Width = 420;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        
        var mainStack = new StackPanel { Margin = new Thickness(20), Spacing = 15 };
        
        var message = new TextBlock
        {
            Text = "An active task is running. Cancel task and exit, or quit UI only (leaving task running)?",
            TextWrapping = TextWrapping.Wrap
        };
        mainStack.Children.Add(message);

        var buttonsStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };

        var cancelAndExitButton = new Button { Content = "Cancel Task & Exit", Width = 150 };
        cancelAndExitButton.Click += (s, e) => { _cancelTask = true; Close(); };
        buttonsStack.Children.Add(cancelAndExitButton);

        var quitUiOnlyButton = new Button { Content = "Quit UI Only", Width = 100 };
        quitUiOnlyButton.Click += (s, e) => { _cancelTask = false; Close(); };
        buttonsStack.Children.Add(quitUiOnlyButton);

        var keepRunningButton = new Button { Content = "Keep Running", Width = 100 };
        keepRunningButton.Click += (s, e) => { _cancelTask = null; Close(); };
        buttonsStack.Children.Add(keepRunningButton);

        mainStack.Children.Add(buttonsStack);
        Content = mainStack;
    }

    public static async Task<bool?> ShowPromptAsync(Window parent)
    {
        var dialog = new ExitConfirmationWindow();
        await dialog.ShowDialog(parent);
        return dialog._cancelTask;
    }
}
