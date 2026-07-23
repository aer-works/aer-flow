using Aer.Ui;
using Aer.Ui.Core;
using Aer.Ui.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Aer.Journeys.Tests;

/// <summary>
/// <b>Journey J8 — "Open it for the first time and know what to do."</b> Desktop leg: on a truly
/// empty first launch, Home must present a real first action, not a blank wall.
/// <para>
/// This leg is <b>green</b> — it drives the shipped <see cref="HomeView"/> over a genuinely empty
/// state (a fresh config with no recents; the test harness already points <c>AER_HOME</c> at an
/// empty temp root) and asserts the "No tasks yet." card renders with working Start-from-template
/// and Create-workflow actions (M19 Phase 5, #190). Because it drives the real view, a regression
/// that hides the empty state behind a still-passing view-model would fail here. J8 overall still
/// reads <b>Fails</b> in <c>spec/journeys.md</c> — its phone leg is the red one
/// (<c>j8_first_run_phone_test.dart</c>); a journey passes only when every leg does.
/// </para>
/// </summary>
[Trait(Journeys.TraitKey, "J8")]
public class J8_DesktopFirstRunTests
{
    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-journeys-j8-{Guid.NewGuid():N}", "recent-task-directories.json");

    [AvaloniaFact]
    public void Empty_first_run_home_offers_real_first_actions_not_a_blank_wall()
    {
        // A fresh config with no recent tasks — the empty first launch.
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // HomeView is a permanent child of the shell (toggled by IsVisible, never lazily built), so
        // its named controls resolve through its namescope whether or not Home is the active tab.
        var home = window.FindControl<HomeView>("HomeViewControl");
        Assert.NotNull(home);

        // The empty-state card is shown — the promise is "not an empty list".
        var emptyState = home.FindControl<Border>("EmptyStateCard");
        Assert.NotNull(emptyState);
        Assert.True(
            emptyState.IsVisible,
            "J8 (desktop): the first-run empty state must be shown, not a blank Home.");

        // "No tasks yet." names what the surface is.
        var heading = home.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == "No tasks yet.");
        Assert.NotNull(heading);

        // ...and it gives two real, live first actions, not a dead-end. Looking each up by its
        // x:Name ties the assertion to the specific affordance the design promises.
        AssertPrimaryAction(home, "StartTemplateButton");
        AssertPrimaryAction(home, "CreateWorkflowButton");
    }

    private static void AssertPrimaryAction(HomeView home, string controlName)
    {
        var button = home.FindControl<Button>(controlName);
        Assert.NotNull(button);
        Assert.True(button.IsEnabled, $"J8 (desktop): the first-run action '{controlName}' must be live, not disabled.");
    }
}
