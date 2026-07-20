using Aer.Workers.Dialogue;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui.Core;

/// <summary>
/// One row of a <see cref="WorkerBindingEntryViewModel.DialogueParticipants"/> list — a single side
/// of an N-party exchange (M23 Phase 1, #270), editable structurally rather than as a hand-typed
/// sidecar file entry. <see cref="Vendor"/> is offered from <see cref="DialogueParticipantPresets.KnownVendors"/>
/// only — the same "reflect, don't invent" seam <see cref="WorkerBindingEntryViewModel.AdapterCandidates"/>
/// already establishes for adapter names — so <see cref="WorkerBindingEntryViewModel.TryBuildDialogueConfig"/>
/// can always resolve a participant's <c>Command</c>/<c>Args</c> via
/// <see cref="DialogueParticipantPresets.For"/> without inventing vendor-CLI flag knowledge here.
/// </summary>
public sealed partial class DialogueParticipantEditorViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private readonly Action<DialogueParticipantEditorViewModel> _onRemove;

    [ObservableProperty]
    private string role = string.Empty;

    [ObservableProperty]
    private string vendor = DialogueParticipantPresets.KnownVendors[0];

    [ObservableProperty]
    private string model = string.Empty;

    [ObservableProperty]
    private string preamble = string.Empty;

    public IReadOnlyList<string> VendorCandidates { get; } = DialogueParticipantPresets.KnownVendors;

    public DialogueParticipantEditorViewModel(Action onChanged, Action<DialogueParticipantEditorViewModel> onRemove)
    {
        _onChanged = onChanged;
        _onRemove = onRemove;
    }

    [RelayCommand]
    private void Remove() => _onRemove(this);

    partial void OnRoleChanged(string value) => _onChanged();

    partial void OnVendorChanged(string value) => _onChanged();

    partial void OnModelChanged(string value) => _onChanged();

    partial void OnPreambleChanged(string value) => _onChanged();
}
