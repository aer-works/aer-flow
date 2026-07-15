using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui;

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
/// </summary>
public sealed partial class WorkerBindingEntryViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    private readonly Action<WorkerBindingEntryViewModel> _onRemove;

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

    [ObservableProperty]
    private string permissionScope = string.Empty;

    private WorkerBindingEntryViewModel(IReadOnlyList<string> adapterCandidates, Action<WorkerBindingEntryViewModel> onRemove)
    {
        AdapterCandidates = adapterCandidates;
        _onRemove = onRemove;
    }

    /// <summary>A freshly-added blank row (<c>BindingsEditorViewModel.AddEntry</c>) — nothing loaded from any file yet.</summary>
    public static WorkerBindingEntryViewModel Blank(IReadOnlyList<string> adapterCandidates, Action<WorkerBindingEntryViewModel> onRemove) =>
        new(adapterCandidates, onRemove)
        {
            TimeoutText = "00:05:00",
            ProducedOutputsJson = "[]",
        };

    /// <summary>Reconstructs one row from an already-parsed <see cref="WorkerBindingConfigEntry"/> — <c>BindingsEditorViewModel.LoadFrom</c>'s per-entry step.</summary>
    public static WorkerBindingEntryViewModel FromEntry(
        string workerName,
        WorkerBindingConfigEntry entry,
        IReadOnlyList<string> adapterCandidates,
        Action<WorkerBindingEntryViewModel> onRemove) =>
        new(adapterCandidates, onRemove)
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
            PermissionScope = entry.PermissionScope ?? string.Empty,
        };

    [RelayCommand]
    private void Remove() => _onRemove(this);

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
            string.IsNullOrWhiteSpace(PermissionScope) ? null : PermissionScope);
        error = null;
        return true;
    }

    private static IReadOnlyList<string> SplitList(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
