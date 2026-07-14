using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Aer.Ui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            // Deliberately minimal per issue #118: the walking skeleton opens exactly the one task
            // directory it's launched with (aer-ui <task-directory>), never a list, a picker, or a
            // scan — discovery is Phase 2 (#119; UI spec §3.1). A missing/extra argument leaves the
            // window showing its default placeholder text rather than failing to launch, since a
            // GUI app has no stderr/exit-code convention to fail into the way Aer.Cli does.
            var args = desktop.Args ?? [];
            if (args.Length == 1)
            {
                _ = window.LoadAsync(args[0]);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
