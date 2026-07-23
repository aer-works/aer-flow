using Aer.Journeys.Tests;
using Aer.Ui;
using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Aer.Journeys.Tests;

/// <summary>
/// The headless Avalonia session every desktop-leg <c>[AvaloniaFact]</c> journey test runs
/// inside — the real <see cref="App"/>, rendered offscreen (no display server, matching the
/// win/linux/mac CI matrix). Identical to <c>Aer.Ui.Tests</c>' bootstrap: a journey's desktop leg
/// drives the same view tree the shipped app renders, so a defect in view composition — output
/// under the wrong tab, a control that never appears — fails the journey rather than hiding behind
/// a green view-model test.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
