using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Aer.Ui;

/// <summary>Shown once, on startup, only when <c>Documents/AER Flow</c> and <c>Documents/Baton</c>
/// both exist (#823) — nothing was moved automatically, so the user needs to know both paths are
/// real and neither was touched.</summary>
public class WorkspaceMigrationNoticeWindow : Window
{
    public WorkspaceMigrationNoticeWindow(string message)
    {
        Title = "Two workspace folders found";
        Width = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.Height;
        CanResize = false;

        FontFamily = new FontFamily("Inter, Outfit, Roboto, system-ui");

        this.Bind(BackgroundProperty, this.GetResourceObservable("Color.Background"));
        this.Bind(ForegroundProperty, this.GetResourceObservable("Color.Text"));

        var mainStack = new StackPanel { Margin = new Thickness(20), Spacing = 16 };

        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            FontSize = 13
        };
        messageText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Color.TextSecondary"));
        mainStack.Children.Add(messageText);

        var okButton = new Button
        {
            Content = "OK",
            Padding = new Thickness(16, 8),
            FontWeight = FontWeight.Medium,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        okButton.Bind(Button.BackgroundProperty, this.GetResourceObservable("Color.Accent"));
        okButton.Foreground = Brushes.White;
        okButton.Click += (_, _) => Close();

        mainStack.Children.Add(okButton);

        Content = mainStack;
    }
}
