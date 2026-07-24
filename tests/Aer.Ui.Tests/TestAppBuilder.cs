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
/// <remarks>
/// This cannot call <c>Program.BuildAvaloniaApp</c> — it has to substitute the headless platform
/// for <c>UsePlatformDetect</c> — so anything configured on the builder has to be shared
/// deliberately or it silently diverges. <c>WithAerFonts</c> (#456) is the first such thing: without
/// it these tests would render in the platform default face while the shipped app renders in Source
/// Sans 3, which would make every font-sensitive assertion here a statement about the harness.
/// </remarks>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .WithAerFonts()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
