using Aer.Ui.Core;
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
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            // #823: the old Documents/AER Flow folder is real data on real installs. Migrate it to
            // Documents/Baton before the window that reads from that path opens; if both already
            // exist, don't guess which one wins — say so instead.
            // Either non-Migrated outcome carries a message and leaves the data where it was; the
            // window opens on the new path either way (#863).
            var migration = WorkspaceMigration.Run();
            if (migration.Message is { } migrationNotice)
            {
                new WorkspaceMigrationNoticeWindow(migrationNotice).Show();
            }

            var window = new MainWindow();
            desktop.MainWindow = window;

            // Local UI Configuration's recents list (UI spec §3.1, §4) is populated regardless of
            // whether a launch argument was given, so the window is useful on a bare launch too.
            _ = window.InitializeAsync();

            // A launch argument (aer-ui <room-directory>) still opens that directory directly, going
            // through OpenAsync now rather than the bare LoadAsync the Phase 1 walking skeleton used
            // (#118) — this is what makes a directory opened this way get remembered in the recents
            // list exactly like one opened by hand (Phase 2, #119). A missing/extra argument leaves
            // the window showing its default placeholder text rather than failing to launch, since a
            // GUI app has no stderr/exit-code convention to fail into the way Aer.Cli does.
            var args = desktop.Args ?? [];
            if (args.Length == 1)
            {
                _ = window.OpenAsync(args[0]);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void MenuShow_Click(object? sender, System.EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            desktop.MainWindow.Show();
            desktop.MainWindow.Activate();
        }
    }

    public void MenuExit_Click(object? sender, System.EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ConfirmCloseAndExit();
        }
    }
}
