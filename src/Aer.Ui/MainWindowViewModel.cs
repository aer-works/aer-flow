using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aer.Ui;

/// <summary>
/// <see cref="MainWindow"/>'s ViewModel layer (M15 Phase 2, issue #138) — introduced for exactly the
/// surface M14 Phase 1 named as the potential second concrete need: the paused-step decision buttons,
/// whose enabled state is tied jointly to projected state (<see cref="PausedSteps"/>) and an
/// in-flight mutation (<see cref="IsMutationInFlight"/>). The rest of the window's read-only
/// rendering (DAG, history, lineage, diff) is untouched, still direct code-behind control
/// manipulation — this type does not attempt to own that.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public ObservableCollection<PausedStepViewModel> PausedSteps { get; } = [];

    /// <summary>
    /// True for the duration of any mutation call this UI process itself is driving — a Run or a
    /// decision — the window's own pump holding the task's §15 lock for that call's entire duration.
    /// Every <see cref="PausedSteps"/> entry's <see cref="PausedStepViewModel.IsEnabled"/> mirrors
    /// this, so a second mutation can never be started from this same process while one is already in
    /// flight (a competing *external* process's lock hold instead surfaces as a
    /// <see cref="Aer.Flow.Concurrency.WorkflowLockedException"/> in-window message, per Phase 1's
    /// precedent — this flag does not, and cannot, prevent that one).
    /// </summary>
    [ObservableProperty]
    private bool isMutationInFlight;

    /// <summary>In-window message surface for a decision's outcome or failure — the same precedent <c>RunStatusText</c> established (Phase 1).</summary>
    [ObservableProperty]
    private string decisionStatusText = string.Empty;

    partial void OnIsMutationInFlightChanged(bool value)
    {
        foreach (var step in PausedSteps)
        {
            step.IsEnabled = !value;
        }
    }
}
