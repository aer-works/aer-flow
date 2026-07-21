using System.Collections.ObjectModel;
using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Workers.Dialogue;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui.Core;

/// <summary>
/// One worker-role row in the bindings editor (M16 Phase 4, issue #153) — the two-way-bound
/// per-entry state <see cref="BindingsEditorViewModel"/>'s <c>Entries</c> collection holds, the same
/// per-item child-ViewModel shape <see cref="PausedStepViewModel"/>/<c>SendBackTargetViewModel</c>
/// established (M15 Phase 3).
/// <para>
/// <b>Structured-vs-opaque editing decision of record (the phase's other named open question):</b>
/// <see cref="Adapter"/>, <see cref="PromptTemplate"/>, <see cref="TimeoutText"/>, <see cref="Model"/>,
/// <see cref="PermissionScope"/> — the entry's scalar fields — and
/// <see cref="WorkerContract.RequiredInputs"/>/<see cref="WorkerContract.OptionalMetadata"/> — its
/// two plain-string lists — all get real structured editing (a text box per scalar, a
/// comma-separated text box per string list: <see cref="RequiredInputsText"/>/
/// <see cref="OptionalMetadataText"/>). <see cref="WorkerContract.ProducedOutputs"/> does not: each
/// entry is itself a small record (<c>Name</c> plus an optional <c>OutputCondition</c> carrying a
/// <c>JsonScalar</c> sum type — string/number/bool/null), and a safe small editable surface for that
/// shape (per-item add/remove, a scalar-type picker) is new list-editing machinery this phase's
/// scope doesn't call for. <see cref="ProducedOutputsJson"/> round-trips it opaquely instead — a raw
/// JSON text box using the exact same <see cref="JsonSerializer"/> converters
/// <see cref="WorkerBindingConfigParser"/>/<see cref="WorkerBindingConfigWriter"/> use, so fidelity
/// (including <c>OutputCondition</c>) is guaranteed by construction rather than by a hand-written
/// mapping this phase would otherwise have to get right.
/// </para>
/// <para>
/// <b>M21 Phase 1's builder-vs-Advanced toggle:</b> <see cref="IsAdvancedPermissionScope"/> picks
/// between the pre-existing free-text <see cref="PermissionScope"/> box and the four
/// <c>Grant*</c>/<see cref="ShellCommandPatternsText"/> checkbox-and-list fields
/// <see cref="TryBuildEntry"/> composes into a <see cref="Adapters.PermissionGrant"/>. Exactly one
/// of the two is ever persisted on <see cref="TryBuildEntry"/>'s built entry — never both — mirroring
/// <see cref="WorkerInvocation"/>'s own documented precedence, so a saved file never carries an
/// ambiguous "which one wins" state. <see cref="Blank"/> defaults new rows to the builder (the
/// promoted, primary path per the phase plan); <see cref="FromEntry"/> instead derives the mode from
/// which field the loaded entry actually has set, so opening an existing hand-typed
/// <see cref="PermissionScope"/> round-trips into Advanced mode unchanged. If a hand-edited file
/// somehow has both set, loading lands in Builder mode (structured wins, matching the resolver) and
/// a subsequent Save normalizes the entry to that interpretation.
/// </para>
/// <para>
/// <b>The dialogue worker as a first-class step type (M23 Phase 1, #270):</b> before this, a
/// <c>"dialogue"</c>-adapter row's actual behavior (participants, seed prompt, turn budget, stop
/// sentinel) lived in a config sidecar file only <see cref="NewWorkflowViewModel"/>'s guided wizard
/// knew how to author — opening one here showed only the opaque <see cref="PromptTemplate"/> path
/// pointing at it, no different from any other adapter's prompt text. <see cref="DialogueParticipants"/>
/// plus <see cref="DialogueSeedPromptText"/>/<see cref="DialogueTurnBudgetText"/>/
/// <see cref="DialogueStopSentinelText"/>/<see cref="DialogueFinalOutputNameText"/> give this row the
/// same structured editing the wizard already had, N participants and all (the wizard itself stays
/// fixed at two — Claude initiator, Gemini responder — since it optimizes for the common case, not
/// full generality). <see cref="TryBuildDialogueConfig"/> is deliberately not folded into
/// <see cref="TryBuildEntry"/>: this row's <see cref="PromptTemplate"/> is still just the sidecar's
/// *path* (an ordinary field of the built <see cref="WorkerBindingConfigEntry"/>), while the
/// sidecar's *content* is a second, sibling artifact <see cref="BindingsEditorViewModel.SaveToFileAsync"/>
/// writes separately — the same "structured fields, opaque pointer" split
/// <see cref="ProducedOutputsJson"/>'s own remarks already draw for a different field.
/// </para>
/// </summary>
public sealed partial class WorkerBindingEntryViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    private readonly Action<WorkerBindingEntryViewModel> _onRemove;
    private readonly IReadOnlyDictionary<string, IWorkerAdapter> _adapterRegistry;

    /// <summary>
    /// Candidate adapter names offered from the registry <c>MainWindow</c> was constructed with
    /// (M15 Phase 1's decision of record) — reflect, don't invent (this phase's named open
    /// question). Carried per-entry rather than read from a shared/ancestor binding: inside an
    /// <c>ItemsControl.ItemTemplate</c> the bound <c>DataContext</c> is this entry, so an
    /// item-local <c>ItemsSource</c> binding is what actually resolves in Avalonia. Not a hard
    /// gate — <see cref="Adapter"/> stays a free-editable box seeded with these candidates, since
    /// nothing in <see cref="WorkerBindingConfigParser.Parse"/> validates an entry's <c>Adapter</c>
    /// against any registry either.
    /// </summary>
    public IReadOnlyList<string> AdapterCandidates { get; }

    [ObservableProperty]
    private string workerName = string.Empty;

    [ObservableProperty]
    private string adapter = string.Empty;

    [ObservableProperty]
    private string contractWorkerName = string.Empty;

    [ObservableProperty]
    private string requiredInputsText = string.Empty;

    [ObservableProperty]
    private string optionalMetadataText = string.Empty;

    [ObservableProperty]
    private string producedOutputsJson = "[]";

    [ObservableProperty]
    private string promptTemplate = string.Empty;

    [ObservableProperty]
    private string timeoutText = string.Empty;

    [ObservableProperty]
    private string model = string.Empty;

    /// <summary>
    /// Where this worker role's process should run (M23 Phase 3, #272) — a stopgap text field plus
    /// a folder-browse button (issue TBD, requested directly ahead of M24 Phase 3's fuller Known
    /// Projects picker) so a rooted absolute path can be set from the app instead of by hand-editing
    /// the bindings file. A bare profile name (resolved via <c>AerProfileStore</c> at run time) is
    /// still typeable here too — the browse button only ever writes a rooted path, it never
    /// interprets or validates a typed profile name.
    /// </summary>
    [ObservableProperty]
    private string workingDirectoryText = string.Empty;

    [ObservableProperty]
    private string permissionScope = string.Empty;

    [ObservableProperty]
    private bool isAdvancedPermissionScope;

    [ObservableProperty]
    private bool grantReadFiles;

    [ObservableProperty]
    private bool grantWriteFiles;

    [ObservableProperty]
    private bool grantRunShellCommands;

    [ObservableProperty]
    private string shellCommandPatternsText = string.Empty;

    [ObservableProperty]
    private bool grantNetworkAccess;

    [ObservableProperty]
    private string permissionGrantGapWarning = string.Empty;

    /// <summary>The AuthorView visibility switch for the gap-warning line — matches this file's own established "expose a bool, don't bind on string length" convention (e.g. <c>!IsDialogue</c> elsewhere in AuthorView.axaml).</summary>
    public bool HasPermissionGrantGapWarning => !string.IsNullOrEmpty(PermissionGrantGapWarning);

    /// <summary>
    /// The shell-command-patterns box's own visibility needs both "builder mode" and "shell grant
    /// checked" — a compound condition Avalonia's single-property binding syntax can't express
    /// without a MultiBinding/converter this file's other bindings don't use, so it's a computed
    /// bool here instead (same reasoning as <see cref="HasPermissionGrantGapWarning"/>).
    /// </summary>
    public bool ShowShellCommandPatterns => !IsAdvancedPermissionScope && GrantRunShellCommands;

    /// <summary>Whether this row is bound to the <c>"dialogue"</c> adapter — the AuthorView visibility switch for the structured dialogue section below.</summary>
    public bool IsDialogueAdapter => Adapter == "dialogue";

    [ObservableProperty]
    private string dialogueSeedPromptText = string.Empty;

    [ObservableProperty]
    private string dialogueTurnBudgetText = "4";

    [ObservableProperty]
    private string dialogueStopSentinelText = string.Empty;

    /// <summary>The dialogue config's own declared final output — kept as its own field rather than derived from <see cref="ProducedOutputsJson"/>, the same opaque-vs-structured split this file already draws for that field.</summary>
    [ObservableProperty]
    private string dialogueFinalOutputNameText = string.Empty;

    /// <summary>This row's exchange sides, in speaking order (M23 Phase 1, #270's N-party generalization) — at least two required to build a valid <see cref="DialogueWorkerConfig"/>, no upper bound.</summary>
    public ObservableCollection<DialogueParticipantEditorViewModel> DialogueParticipants { get; } = [];

    private WorkerBindingEntryViewModel(
        IReadOnlyList<string> adapterCandidates,
        IReadOnlyDictionary<string, IWorkerAdapter> adapterRegistry,
        Action<WorkerBindingEntryViewModel> onRemove)
    {
        AdapterCandidates = adapterCandidates;
        _adapterRegistry = adapterRegistry;
        _onRemove = onRemove;
        DialogueParticipants.CollectionChanged += (_, _) => NotifyDialogueParticipantsChanged();
    }

    /// <summary>
    /// Raises a change notification on <see cref="DialogueParticipants"/> itself — the collection's
    /// own membership changing (add/remove) or one participant row's fields changing both need to
    /// reach <see cref="BindingsEditorViewModel.OnEntryPropertyChanged"/>, which listens to this
    /// entry's <see cref="ObservableObject.PropertyChanged"/> broadly to recompute dirty state; an
    /// <c>ObservableCollection&lt;T&gt;</c>'s own <c>CollectionChanged</c> is a different event that
    /// bubbling would otherwise miss.
    /// </summary>
    internal void NotifyDialogueParticipantsChanged() => OnPropertyChanged(nameof(DialogueParticipants));

    [RelayCommand]
    private void AddDialogueParticipant() =>
        DialogueParticipants.Add(new DialogueParticipantEditorViewModel(NotifyDialogueParticipantsChanged, RemoveDialogueParticipant));

    private void RemoveDialogueParticipant(DialogueParticipantEditorViewModel participant) =>
        DialogueParticipants.Remove(participant);

    /// <summary>Seeds the two-party default (Claude initiator / Gemini responder) the guided wizard also uses — only when switching into dialogue mode with no participants authored yet, never overwriting an in-progress edit.</summary>
    private void SeedDefaultDialogueParticipantsIfEmpty()
    {
        if (DialogueParticipants.Count > 0)
        {
            return;
        }

        DialogueParticipants.Add(new DialogueParticipantEditorViewModel(NotifyDialogueParticipantsChanged, RemoveDialogueParticipant)
        {
            Role = "initiator",
            Vendor = "claude",
        });
        DialogueParticipants.Add(new DialogueParticipantEditorViewModel(NotifyDialogueParticipantsChanged, RemoveDialogueParticipant)
        {
            Role = "responder",
            Vendor = "gemini",
        });
    }

    /// <summary>A freshly-added blank row (<c>BindingsEditorViewModel.AddEntry</c>) — nothing loaded from any file yet.</summary>
    public static WorkerBindingEntryViewModel Blank(
        IReadOnlyList<string> adapterCandidates,
        IReadOnlyDictionary<string, IWorkerAdapter> adapterRegistry,
        Action<WorkerBindingEntryViewModel> onRemove) =>
        new(adapterCandidates, adapterRegistry, onRemove)
        {
            TimeoutText = "00:05:00",
            ProducedOutputsJson = "[]",
            IsAdvancedPermissionScope = false,
        };

    /// <summary>
    /// Reconstructs one row from an already-parsed <see cref="WorkerBindingConfigEntry"/> —
    /// <c>BindingsEditorViewModel.LoadFrom</c>'s per-entry step. <paramref name="dialogueConfig"/> is
    /// the sidecar <see cref="DialogueWorkerConfig"/> <c>BindingsEditorViewModel</c> already loaded
    /// from <see cref="WorkerBindingConfigEntry.PromptTemplate"/> when <paramref name="entry"/>'s
    /// <see cref="WorkerBindingConfigEntry.Adapter"/> is <c>"dialogue"</c> — null for every other
    /// adapter, and also null for a dialogue entry whose sidecar failed to load (missing file,
    /// malformed JSON), in which case this row falls back to the same two-party default a brand new
    /// dialogue row gets, rather than losing the ability to author the step at all.
    /// </summary>
    public static WorkerBindingEntryViewModel FromEntry(
        string workerName,
        WorkerBindingConfigEntry entry,
        IReadOnlyList<string> adapterCandidates,
        IReadOnlyDictionary<string, IWorkerAdapter> adapterRegistry,
        Action<WorkerBindingEntryViewModel> onRemove,
        DialogueWorkerConfig? dialogueConfig = null)
    {
        var vm = new WorkerBindingEntryViewModel(adapterCandidates, adapterRegistry, onRemove)
        {
            WorkerName = workerName,
            Adapter = entry.Adapter,
            ContractWorkerName = entry.Contract.WorkerName,
            RequiredInputsText = string.Join(", ", entry.Contract.RequiredInputs),
            OptionalMetadataText = string.Join(", ", entry.Contract.OptionalMetadata),
            ProducedOutputsJson = JsonSerializer.Serialize(entry.Contract.ProducedOutputs, IndentedOptions),
            PromptTemplate = entry.PromptTemplate,
            TimeoutText = entry.Timeout.ToString(),
            Model = entry.Model ?? string.Empty,
            WorkingDirectoryText = entry.WorkingDirectory ?? string.Empty,
            PermissionScope = entry.PermissionScope ?? string.Empty,
            IsAdvancedPermissionScope = entry.PermissionGrant is null,
            GrantReadFiles = entry.PermissionGrant?.ReadFiles ?? false,
            GrantWriteFiles = entry.PermissionGrant?.WriteFiles ?? false,
            GrantRunShellCommands = entry.PermissionGrant?.RunShellCommands ?? false,
            ShellCommandPatternsText = entry.PermissionGrant?.ShellCommandPatterns is { Count: > 0 } patterns
                ? string.Join(", ", patterns)
                : string.Empty,
            GrantNetworkAccess = entry.PermissionGrant?.NetworkAccess ?? false,
        };

        if (dialogueConfig is not null)
        {
            vm.DialogueParticipants.Clear();
            vm.DialogueSeedPromptText = dialogueConfig.SeedPrompt;
            vm.DialogueTurnBudgetText = dialogueConfig.TurnBudget.ToString();
            vm.DialogueStopSentinelText = dialogueConfig.StopSentinel ?? string.Empty;
            vm.DialogueFinalOutputNameText = dialogueConfig.FinalOutputName;
            foreach (var participant in dialogueConfig.Participants)
            {
                vm.DialogueParticipants.Add(new DialogueParticipantEditorViewModel(vm.NotifyDialogueParticipantsChanged, vm.RemoveDialogueParticipant)
                {
                    Role = participant.Role,
                    Vendor = participant.Vendor,
                    Model = participant.Model ?? string.Empty,
                    Preamble = participant.Preamble,
                });
            }
        }

        return vm;
    }

    [RelayCommand]
    private void Remove() => _onRemove(this);

    partial void OnAdapterChanged(string value)
    {
        RecomputePermissionGrantGapWarning();
        OnPropertyChanged(nameof(IsDialogueAdapter));
        if (IsDialogueAdapter)
        {
            SeedDefaultDialogueParticipantsIfEmpty();
        }
    }

    partial void OnIsAdvancedPermissionScopeChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowShellCommandPatterns));
        RecomputePermissionGrantGapWarning();
    }

    partial void OnGrantReadFilesChanged(bool value) => RecomputePermissionGrantGapWarning();

    partial void OnGrantWriteFilesChanged(bool value) => RecomputePermissionGrantGapWarning();

    partial void OnGrantRunShellCommandsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowShellCommandPatterns));
        RecomputePermissionGrantGapWarning();
    }

    partial void OnShellCommandPatternsTextChanged(string value) => RecomputePermissionGrantGapWarning();

    partial void OnGrantNetworkAccessChanged(bool value) => RecomputePermissionGrantGapWarning();

    partial void OnPermissionGrantGapWarningChanged(string value) => OnPropertyChanged(nameof(HasPermissionGrantGapWarning));

    /// <summary>
    /// Live validation the builder UI shows inline (M21 Phase 1's "surface that gap in the UI
    /// explicitly rather than silently dropping or downgrading it") — re-run on every field that
    /// feeds a <see cref="Adapters.PermissionGrant"/>, plus <see cref="Adapter"/> itself since the
    /// gap is adapter-specific. Empty whenever there's nothing to warn about: Advanced mode, an
    /// adapter name not found in the registry (nothing to check yet), or an adapter with no
    /// <see cref="IPermissionGrantTranslator"/> at all (<see cref="PermissionGrantGapWarning"/>
    /// still fires here, but as "no builder support" rather than a per-category refusal).
    /// </summary>
    private void RecomputePermissionGrantGapWarning()
    {
        if (IsAdvancedPermissionScope)
        {
            PermissionGrantGapWarning = string.Empty;
            return;
        }

        if (!_adapterRegistry.TryGetValue(Adapter, out var adapter))
        {
            PermissionGrantGapWarning = string.Empty;
            return;
        }

        if (adapter is not IPermissionGrantTranslator translator)
        {
            PermissionGrantGapWarning = $"'{Adapter}' has no structured permission builder support — use Advanced instead.";
            return;
        }

        var grant = BuildPermissionGrant();
        if (grant.IsEmpty)
        {
            PermissionGrantGapWarning = string.Empty;
            return;
        }

        PermissionGrantGapWarning = translator.TryTranslatePermissionGrant(grant, out _, out var gapReason)
            ? string.Empty
            : gapReason ?? "This adapter cannot express the selected permissions.";
    }

    private PermissionGrant BuildPermissionGrant() => new(
        GrantReadFiles,
        GrantWriteFiles,
        GrantRunShellCommands,
        SplitList(ShellCommandPatternsText),
        GrantNetworkAccess);

    /// <summary>
    /// Builds this row's <see cref="WorkerBindingConfigEntry"/> — the only place a malformed
    /// <see cref="TimeoutText"/> or <see cref="ProducedOutputsJson"/> is caught, since neither can
    /// be represented as the strongly-typed field until it parses (the same half-typed-value
    /// discipline <c>TemplateEditorViewModel.TemplateVersionText</c> established: kept as text while
    /// editing, parsed only at build/Save time). <see cref="WorkerBindingConfigWriter.Serialize"/>
    /// still re-validates <see cref="Adapter"/>/<see cref="PromptTemplate"/> blankness at save time —
    /// this only catches the two fields that would throw before a <see cref="WorkerBindingConfigEntry"/>
    /// could even be constructed.
    /// </summary>
    internal bool TryBuildEntry(out WorkerBindingConfigEntry? entry, out string? error)
    {
        if (!TimeSpan.TryParse(TimeoutText, out var timeout))
        {
            entry = null;
            error = $"Timeout '{TimeoutText}' for '{WorkerName}' is not a valid duration (e.g. 00:05:00).";
            return false;
        }

        List<ProducedOutput>? producedOutputs;
        try
        {
            producedOutputs = JsonSerializer.Deserialize<List<ProducedOutput>>(
                string.IsNullOrWhiteSpace(ProducedOutputsJson) ? "[]" : ProducedOutputsJson);
        }
        catch (JsonException ex)
        {
            entry = null;
            error = $"Produced-outputs JSON for '{WorkerName}' is invalid: {ex.Message}";
            return false;
        }

        if (producedOutputs is null)
        {
            entry = null;
            error = $"Produced-outputs JSON for '{WorkerName}' must be a JSON array.";
            return false;
        }

        string? permissionScope = null;
        PermissionGrant? permissionGrant = null;
        if (IsAdvancedPermissionScope)
        {
            permissionScope = string.IsNullOrWhiteSpace(PermissionScope) ? null : PermissionScope;
        }
        else
        {
            var grant = BuildPermissionGrant();
            if (!grant.IsEmpty)
            {
                // Mirrors PermissionGrantUnsupportedException's precedent (defense in depth at
                // dispatch time) one layer earlier, at Save time, so the gap RecomputePermissionGrantGapWarning
                // already shows inline also blocks persisting a grant the selected adapter can't honor.
                if (_adapterRegistry.TryGetValue(Adapter, out var adapter) && adapter is IPermissionGrantTranslator translator
                    && !translator.TryTranslatePermissionGrant(grant, out _, out var gapReason))
                {
                    entry = null;
                    error = $"Permission grant for '{WorkerName}' can't be saved: {gapReason}";
                    return false;
                }

                permissionGrant = grant;
            }
        }

        var contract = new WorkerContract(
            string.IsNullOrWhiteSpace(ContractWorkerName) ? WorkerName : ContractWorkerName,
            SplitList(RequiredInputsText),
            producedOutputs,
            SplitList(OptionalMetadataText));

        entry = new WorkerBindingConfigEntry(
            Adapter,
            contract,
            PromptTemplate,
            timeout,
            string.IsNullOrWhiteSpace(Model) ? null : Model,
            permissionScope,
            permissionGrant,
            string.IsNullOrWhiteSpace(WorkingDirectoryText) ? null : WorkingDirectoryText);
        error = null;
        return true;
    }

    /// <summary>
    /// Builds this row's sidecar <see cref="DialogueWorkerConfig"/> from the structured dialogue
    /// fields (M23 Phase 1, #270) — the counterpart of <see cref="TryBuildEntry"/> for the second,
    /// sibling artifact <see cref="BindingsEditorViewModel.SaveToFileAsync"/> writes to
    /// <see cref="PromptTemplate"/>'s path. Only meaningful when <see cref="IsDialogueAdapter"/>;
    /// callers are expected to check that first (the same "caller already knows which branch it's
    /// in" shape <see cref="TryBuildEntry"/> itself doesn't need since it's unconditional).
    /// </summary>
    internal bool TryBuildDialogueConfig(out DialogueWorkerConfig? config, out string? error)
    {
        if (string.IsNullOrWhiteSpace(DialogueSeedPromptText))
        {
            config = null;
            error = $"'{WorkerName}' needs the dialogue's opening seed prompt.";
            return false;
        }

        if (!int.TryParse(DialogueTurnBudgetText, out var turnBudget) || turnBudget <= 0)
        {
            config = null;
            error = $"'{WorkerName}' needs a whole positive number of dialogue turns.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(DialogueFinalOutputNameText))
        {
            config = null;
            error = $"'{WorkerName}' needs the dialogue's declared final output file name.";
            return false;
        }

        if (DialogueParticipants.Count < 2)
        {
            config = null;
            error = $"'{WorkerName}' needs at least two dialogue participants.";
            return false;
        }

        var participants = new List<DialogueParticipant>(DialogueParticipants.Count);
        foreach (var participantVm in DialogueParticipants)
        {
            if (string.IsNullOrWhiteSpace(participantVm.Role))
            {
                config = null;
                error = $"'{WorkerName}' has a dialogue participant with no role.";
                return false;
            }

            if (!DialogueParticipantPresets.KnownVendors.Contains(participantVm.Vendor))
            {
                config = null;
                error = $"'{WorkerName}' participant '{participantVm.Role}' has an unknown vendor '{participantVm.Vendor}'.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(participantVm.Preamble))
            {
                config = null;
                error = $"'{WorkerName}' participant '{participantVm.Role}' needs its own preamble.";
                return false;
            }

            participants.Add(DialogueParticipantPresets.For(
                participantVm.Vendor,
                participantVm.Role,
                participantVm.Preamble,
                string.IsNullOrWhiteSpace(participantVm.Model) ? null : participantVm.Model));
        }

        config = new DialogueWorkerConfig(
            DialogueSeedPromptText,
            turnBudget,
            DialogueFinalOutputNameText,
            string.IsNullOrWhiteSpace(DialogueStopSentinelText) ? null : DialogueStopSentinelText,
            participants);
        error = null;
        return true;
    }

    private static IReadOnlyList<string> SplitList(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
