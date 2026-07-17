using System.Collections.ObjectModel;
using Aer.Flow.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui.Core;

/// <summary>
/// One step's editable state within <see cref="TemplateEditorViewModel.Steps"/> (M16 Phase 2, issue
/// #151; extended for Phase 3, issue #152) — <c>StepId</c>/<c>Worker</c>/<c>RetryPolicy.MaxAttempts</c>
/// as direct text boxes; <c>Inputs</c>/<c>Outputs</c> as comma-separated text (parsed at
/// candidate-build time, the same text-not-typed-value shape
/// <see cref="TemplateEditorViewModel.TemplateVersionText"/> established so a half-typed value stays
/// representable while editing); <c>DependsOn</c> as a checkbox per <see cref="DependsOnOptions"/>
/// entry rather than free text — candidates come from the template's other declared steps only, never
/// invented (§8; the authoring counterpart of M15's send-back discipline).
/// <c>PausePoint</c>/<c>SupersedeTargets</c> follow the identical shape (Phase 3): <see cref="HasPausePoint"/>
/// toggles whether this step declares one at all, and <see cref="SupersedeTargetOptions"/> offers a
/// checkbox per this step's actual transitive ancestor in the current in-edit graph — never a
/// free-form entry that could fail the validator's ancestry rule (§17.1).
/// </summary>
public sealed partial class StepEditorViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private readonly Action<StepEditorViewModel> _onRemove;

    [ObservableProperty]
    private string stepId = string.Empty;

    [ObservableProperty]
    private string worker = string.Empty;

    /// <summary>Comma-separated <c>Inputs</c> — parsed at candidate-build time, empty entries dropped.</summary>
    [ObservableProperty]
    private string inputsText = string.Empty;

    /// <summary>Comma-separated <c>Outputs</c> — parsed at candidate-build time, empty entries dropped.</summary>
    [ObservableProperty]
    private string outputsText = string.Empty;

    /// <summary>Kept as text, not an <see cref="int"/>, for the same half-typed-value reason <see cref="TemplateEditorViewModel.TemplateVersionText"/> is.</summary>
    [ObservableProperty]
    private string maxAttemptsText = "1";

    /// <summary>
    /// One entry per every *other* declared step (never this one — a step cannot depend on itself,
    /// impossible by construction rather than a validator rule to trip) — rebuilt by
    /// <see cref="TemplateEditorViewModel"/> whenever the step list's membership or any <see cref="StepId"/>
    /// changes, so labels always reflect the template's current declared steps.
    /// </summary>
    public ObservableCollection<DependsOnOptionViewModel> DependsOnOptions { get; } = [];

    /// <summary>
    /// The authoritative selected-<c>DependsOn</c> set, keyed by target <see cref="StepId"/> text —
    /// survives <see cref="DependsOnOptions"/> being rebuilt (a rename elsewhere only drops a selection
    /// if the old text no longer names any declared step; see <see cref="TemplateEditorViewModel.RefreshDependsOnOptions"/>).
    /// </summary>
    internal HashSet<string> SelectedDependsOn { get; } = [];

    /// <summary>Whether this step declares a <c>PausePoint</c> at all (Phase 3) — toggling this off sets <c>PausePoint</c> to <see langword="null"/> regardless of <see cref="SelectedSupersedeTargets"/>'s contents.</summary>
    [ObservableProperty]
    private bool hasPausePoint;

    /// <summary>
    /// One entry per this step's actual transitive <c>DependsOn</c> ancestor in the current in-edit
    /// graph (Phase 3) — computed via <c>WorkflowDefinitionValidator.ComputeTransitiveAncestors</c>,
    /// never re-implemented; only refreshed while the graph is fully valid (an ancestor walk assumes
    /// acyclic, fully-resolvable input), so it can go briefly stale while an unrelated edit leaves the
    /// graph temporarily invalid — see <see cref="TemplateEditorViewModel.RefreshSupersedeTargetOptions"/>.
    /// </summary>
    public ObservableCollection<SupersedeTargetOptionViewModel> SupersedeTargetOptions { get; } = [];

    /// <summary>
    /// The authoritative selected-<c>SupersedeTargets</c> set, keyed by target <see cref="StepId"/>
    /// text — survives <see cref="SupersedeTargetOptions"/> being rebuilt or going stale, the same way
    /// <see cref="SelectedDependsOn"/> does. A graph edit that orphans an already-selected target (it
    /// stops being a transitive ancestor) is not silently dropped here: it rides into
    /// <see cref="TemplateEditorViewModel.BuildCandidate"/> unchanged and surfaces as a live
    /// <c>WorkflowDefinitionValidator</c> violation instead (§17.1's own ancestry rule remains the
    /// backstop, exactly as the phase plan requires).
    /// </summary>
    internal HashSet<string> SelectedSupersedeTargets { get; } = [];

    public StepEditorViewModel(Action onChanged, Action<StepEditorViewModel> onRemove, PausePoint? originalPausePoint = null)
    {
        _onChanged = onChanged;
        _onRemove = onRemove;

        if (originalPausePoint is not null)
        {
            // Direct backing-field assignment, not the property setter — construction must not fire
            // _onChanged before this instance is even reachable from the owner's Steps collection.
            hasPausePoint = true;
            foreach (var target in originalPausePoint.SupersedeTargets)
            {
                SelectedSupersedeTargets.Add(target.Value);
            }
        }
    }

    [RelayCommand]
    private void Remove() => _onRemove(this);

    /// <summary>Toggles <paramref name="targetStepId"/>'s membership in <see cref="SelectedDependsOn"/> — called from a <see cref="DependsOnOptionViewModel"/>'s own checkbox binding.</summary>
    internal void SetDependsOnSelected(string targetStepId, bool isSelected)
    {
        if (isSelected)
        {
            SelectedDependsOn.Add(targetStepId);
        }
        else
        {
            SelectedDependsOn.Remove(targetStepId);
        }

        _onChanged();
    }

    /// <summary>Toggles <paramref name="targetStepId"/>'s membership in <see cref="SelectedSupersedeTargets"/> — called from a <see cref="SupersedeTargetOptionViewModel"/>'s own checkbox binding.</summary>
    internal void SetSupersedeTargetSelected(string targetStepId, bool isSelected)
    {
        if (isSelected)
        {
            SelectedSupersedeTargets.Add(targetStepId);
        }
        else
        {
            SelectedSupersedeTargets.Remove(targetStepId);
        }

        _onChanged();
    }

    partial void OnStepIdChanged(string value) => _onChanged();

    partial void OnWorkerChanged(string value) => _onChanged();

    partial void OnInputsTextChanged(string value) => _onChanged();

    partial void OnOutputsTextChanged(string value) => _onChanged();

    partial void OnMaxAttemptsTextChanged(string value) => _onChanged();

    partial void OnHasPausePointChanged(bool value) => _onChanged();
}

/// <summary>
/// One <see cref="StepEditorViewModel.SupersedeTargetOptions"/> entry — a candidate
/// <c>SupersedeTargets</c> target, offered as a checkbox (M16 Phase 3, issue #152). The same thin
/// child-view-model shape as <see cref="DependsOnOptionViewModel"/>, kept as its own type rather than
/// shared: the two represent different candidate sources (declared steps vs. computed transitive
/// ancestors) even though their rendering shape is identical today.
/// </summary>
public sealed partial class SupersedeTargetOptionViewModel : ObservableObject
{
    private readonly StepEditorViewModel _owner;

    public string StepId { get; }

    [ObservableProperty]
    private bool isSelected;

    public SupersedeTargetOptionViewModel(string stepId, bool isSelected, StepEditorViewModel owner)
    {
        StepId = stepId;
        _owner = owner;
        this.isSelected = isSelected;
    }

    partial void OnIsSelectedChanged(bool value) => _owner.SetSupersedeTargetSelected(StepId, value);
}

/// <summary>
/// One <see cref="StepEditorViewModel.DependsOnOptions"/> entry — a candidate target step, offered as
/// a checkbox (M16 Phase 2, issue #151). A thin child view model rather than a bound command with a
/// parameter, the same reasoning <c>SendBackTargetViewModel</c> used (M15 Phase 3).
/// </summary>
public sealed partial class DependsOnOptionViewModel : ObservableObject
{
    private readonly StepEditorViewModel _owner;

    public string StepId { get; }

    [ObservableProperty]
    private bool isSelected;

    public DependsOnOptionViewModel(string stepId, bool isSelected, StepEditorViewModel owner)
    {
        StepId = stepId;
        _owner = owner;
        this.isSelected = isSelected;
    }

    partial void OnIsSelectedChanged(bool value) => _owner.SetDependsOnSelected(StepId, value);
}
