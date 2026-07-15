using System.Collections.ObjectModel;
using Aer.Flow.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui;

/// <summary>
/// One step's editable state within <see cref="TemplateEditorViewModel.Steps"/> (M16 Phase 2, issue
/// #151) — <c>StepId</c>/<c>Worker</c>/<c>RetryPolicy.MaxAttempts</c> as direct text boxes;
/// <c>Inputs</c>/<c>Outputs</c> as comma-separated text (parsed at candidate-build time, the same
/// text-not-typed-value shape <see cref="TemplateEditorViewModel.TemplateVersionText"/> established
/// so a half-typed value stays representable while editing); <c>DependsOn</c> as a checkbox per
/// <see cref="DependsOnOptions"/> entry rather than free text — candidates come from the template's
/// other declared steps only, never invented (§8; the authoring counterpart of M15's send-back
/// discipline). <see cref="OriginalPausePoint"/> carries a loaded step's <c>PausePoint</c> through
/// every save untouched: Phase 2 does not offer pause-point editing at all (Phase 3, issue #152).
/// </summary>
public sealed partial class StepEditorViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private readonly Action<StepEditorViewModel> _onRemove;

    /// <summary>
    /// A loaded step's <c>PausePoint</c> (<see langword="null"/> if it never had one), carried through
    /// every candidate this step contributes to <see cref="TemplateEditorViewModel.BuildCandidate"/> —
    /// Phase 2 never constructs or edits one itself.
    /// </summary>
    internal PausePoint? OriginalPausePoint { get; }

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

    public StepEditorViewModel(Action onChanged, Action<StepEditorViewModel> onRemove, PausePoint? originalPausePoint = null)
    {
        _onChanged = onChanged;
        _onRemove = onRemove;
        OriginalPausePoint = originalPausePoint;
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

    partial void OnStepIdChanged(string value) => _onChanged();

    partial void OnWorkerChanged(string value) => _onChanged();

    partial void OnInputsTextChanged(string value) => _onChanged();

    partial void OnOutputsTextChanged(string value) => _onChanged();

    partial void OnMaxAttemptsTextChanged(string value) => _onChanged();
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
