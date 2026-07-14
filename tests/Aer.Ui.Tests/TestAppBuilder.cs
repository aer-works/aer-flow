using Aer.Ui;
using Aer.Ui.Tests;
using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Aer.Ui.Tests;

/// <summary>
/// The headless Avalonia session every <c>[AvaloniaFact]</c> test in this project runs inside —
/// the real <see cref="App"/> class, not a test double, configured with
/// <see cref="AvaloniaHeadlessPlatformOptions"/> so it renders offscreen (no display server
/// required, matching the win/linux/mac CI matrix, none of which run with one attached).
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
