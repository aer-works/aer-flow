using Aer.Flow.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui;

/// <summary>
/// One paused step's §7 action surface (M15 Phase 2, issue #138): human-facing "Approve"/"Reject"
/// labels on top of Flow's closed <see cref="DecisionType"/> set — <c>Approve</c> records
/// <see cref="DecisionType.Resume"/>, <c>Reject</c> records <see cref="DecisionType.Reject"/>,
/// never a UI-invented decision type (UI spec §6). Rebuilt from <see cref="TaskProjection"/> on every
/// load — a projected fact, not retained handler state, the same "re-derived, not remembered"
/// discipline the rest of <see cref="MainWindow"/>'s rendering already follows.
/// </summary>
public sealed partial class PausedStepViewModel : ObservableObject
{
    private readonly Func<StepId, ExecutionId, DecisionType, Task> _decide;

    public StepId StepId { get; }

    public ExecutionId ExecutionId { get; }

    public string Label { get; }

    /// <summary>
    /// Whether this step's actions may be invoked — false while the UI's own pump holds the task's
    /// lock for any mutation (this decision or another one), driven by <see cref="MainWindowViewModel.IsMutationInFlight"/>.
    /// A <see cref="RelayCommandAttribute"/> <c>CanExecute</c> predicate, not a plain field, so the
    /// bound buttons' enabled state updates the moment it changes rather than only on the next render.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RejectCommand))]
    private bool isEnabled = true;

    public PausedStepViewModel(StepId stepId, ExecutionId executionId, Func<StepId, ExecutionId, DecisionType, Task> decide)
    {
        StepId = stepId;
        ExecutionId = executionId;
        _decide = decide;
        Label = $"{stepId.Value} ({executionId.Value})";
    }

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private Task ApproveAsync() => _decide(StepId, ExecutionId, DecisionType.Resume);

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private Task RejectAsync() => _decide(StepId, ExecutionId, DecisionType.Reject);
}
