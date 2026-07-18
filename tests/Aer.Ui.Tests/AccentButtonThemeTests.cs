using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Aer.Ui.Tests;

/// <summary>
/// Regression test for issue #209: FluentTheme reserves the class name "accent" on Button for its
/// own <c>SystemAccentColor</c>-based styling (the Windows OS accent color — red or blue depending
/// on the user's Windows settings, not the app's own teal <c>Color.Accent</c> token), and that
/// built-in styling won FluentTheme's specificity fight against setters placed directly on
/// <c>Button.accent</c> under some theme-variant conditions. The fix (<see cref="MainWindow"/>'s
/// <c>Base.axaml</c>) targets <c>Button.accent /template/ ContentPresenter</c> instead. This test
/// forces <see cref="ThemeVariant.Light"/> (the variant screenshots showed rendering the OS red
/// accent before the fix) and asserts the rendered background is Tokens.axaml's light
/// <c>Color.Accent</c> hex, not any system color.
/// </summary>
public class AccentButtonThemeTests
{
    [AvaloniaFact]
    public void Accent_button_background_resolves_to_the_apps_color_token_not_the_os_accent_color_in_light_theme()
    {
        var window = new MainWindow { RequestedThemeVariant = ThemeVariant.Light };
        window.Show();

        var runButton = window.FindViewControl<Button>("RunButton")!;
        runButton.ApplyTemplate();

        var contentPresenter = runButton.GetVisualDescendants().OfType<ContentPresenter>().First();

        // Tokens.axaml's Default (light) dictionary's Color.Accent — not any SystemAccentColor-derived brush.
        Assert.Equal(Color.Parse("#0F7B7B"), ((ISolidColorBrush)contentPresenter.Background!).Color);
        Assert.Equal(Color.Parse("#0F7B7B"), ((ISolidColorBrush)contentPresenter.BorderBrush!).Color);
    }

    [AvaloniaFact]
    public void Accent_button_background_resolves_to_the_apps_color_token_not_the_os_accent_color_in_dark_theme()
    {
        var window = new MainWindow { RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();

        var runButton = window.FindViewControl<Button>("RunButton")!;
        runButton.ApplyTemplate();

        var contentPresenter = runButton.GetVisualDescendants().OfType<ContentPresenter>().First();

        // Tokens.axaml's Dark dictionary's Color.Accent.
        Assert.Equal(Color.Parse("#2CB5B5"), ((ISolidColorBrush)contentPresenter.Background!).Color);
        Assert.Equal(Color.Parse("#2CB5B5"), ((ISolidColorBrush)contentPresenter.BorderBrush!).Color);
    }
}
