using Avalonia;

namespace Aer.Ui;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Exposed as a static entry point, not inlined into <see cref="Main"/>, so the Avalonia previewer can find it — the standard template shape.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
