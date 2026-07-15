using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Aer.Adapters;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aer.Ui;

/// <summary>
/// The worker-bindings editor's state (M16 Phase 4, issue #153) — the second authoring surface,
/// riding the same MVVM shape <see cref="TemplateEditorViewModel"/> established (M16 Phase 1):
/// in-memory editing against an explicit baseline with dirty tracking; <c>MainWindow</c>'s
/// New/Open-in-editor/Save bindings actions own all file I/O (the same state-in-VM/IO-in-window
/// split). This editor only ever touches bindings *files* (UI spec §4, §9) — bindings are
/// deliberately not template data and are never persisted in a task directory (M14 Phase 2's
/// decision of record), so nothing here reaches a bound task either.
/// <para>
/// <b>Dirty-tracking decision of record:</b> unlike <see cref="TemplateEditorViewModel"/>, this
/// cannot compare baseline-vs-candidate by record <c>==</c>, because a template save's candidate is
/// built with <c>baseline with { ... }</c> — reusing the same <c>Steps</c> list reference when
/// unchanged, so reference-equal collections make <c>==</c> already correct. A bindings save always
/// rebuilds a fresh <c>Dictionary</c> from the editable rows, so two structurally-identical configs
/// are never reference-equal. <see cref="ConfigEquals"/> is the deliberate manual deep-equality
/// check that replaces the <c>==</c> trick for this editor.
/// </para>
/// </summary>
public sealed partial class BindingsEditorViewModel : ObservableObject
{
    /// <summary>The state the current <see cref="Entries"/> are compared against for dirty tracking — what was last loaded from or saved to disk, or the empty config <see cref="StartNew"/> minted. <see langword="null"/> until an editing session starts.</summary>
    internal IReadOnlyDictionary<string, WorkerBindingConfigEntry>? Baseline { get; private set; }

    /// <summary>Whether <see cref="Baseline"/> reflects a state that exists on disk — mirrors <see cref="TemplateEditorViewModel.BaselineIsPersisted"/>, though bindings have no version field to distinguish a first save on.</summary>
    internal bool BaselineIsPersisted { get; private set; }

    public ObservableCollection<WorkerBindingEntryViewModel> Entries { get; } = [];

    /// <summary>
    /// Advisory-only cross-check display (UI spec §9's worker-bindings grant, never a save gate):
    /// which <c>Worker</c> names the currently-open template declares that have no entry here.
    /// Populated only by <c>MainWindow.RefreshBindingsTemplateCrossCheck</c>, on demand — never
    /// automatically, since keeping it live would mean reaching into the template editor's own
    /// change notifications, which this phase deliberately does not touch (Phases 1-3 own template
    /// editing). Bindings are deliberately not template data (§9), so this can never gate <see cref="Entries"/> or Save.
    /// </summary>
    public ObservableCollection<string> MissingTemplateWorkerNames { get; } = [];

    /// <summary>The candidate adapter names offered to every row (M15 Phase 1's registry, reflected — never invented).</summary>
    private IReadOnlyList<string> _adapterCandidates = [];

    [ObservableProperty]
    private bool isOpen;

    [ObservableProperty]
    private bool isDirty;

    [ObservableProperty]
    private string statusText = string.Empty;

    public BindingsEditorViewModel()
    {
        Entries.CollectionChanged += OnEntriesCollectionChanged;
    }

    /// <summary>Wires the adapter-name candidates every row is constructed with — called once by <c>MainWindow</c>'s constructor, the same "registry is a constructor argument" seam M15 Phase 1 established.</summary>
    internal void SetAdapterCandidates(IReadOnlyList<string> adapterCandidates) => _adapterCandidates = adapterCandidates;

    /// <summary>Starts a fresh editing session over an empty config — nothing touches disk until Save.</summary>
    public void StartNew()
    {
        UnsubscribeAll();
        Baseline = new Dictionary<string, WorkerBindingConfigEntry>();
        BaselineIsPersisted = false;
        Entries.Clear();
        IsOpen = true;
        StatusText = string.Empty;
        RecomputeIsDirty();
    }

    /// <summary>(Re)anchors the session to <paramref name="config"/> as the on-disk state — called by Open-in-editor after a parse, and by Save after a successful write.</summary>
    public void LoadFrom(IReadOnlyDictionary<string, WorkerBindingConfigEntry> config)
    {
        UnsubscribeAll();
        Baseline = config;
        BaselineIsPersisted = true;
        Entries.Clear();
        foreach (var (workerName, entry) in config)
        {
            Entries.Add(WorkerBindingEntryViewModel.FromEntry(workerName, entry, _adapterCandidates, RemoveEntry));
        }

        IsOpen = true;
        RecomputeIsDirty();
    }

    /// <summary>Ends the editing session (a failed Open-in-editor must not leave a stale one behind).</summary>
    public void Close()
    {
        UnsubscribeAll();
        Baseline = null;
        BaselineIsPersisted = false;
        Entries.Clear();
        IsOpen = false;
        MissingTemplateWorkerNames.Clear();
        RecomputeIsDirty();
    }

    /// <summary>Adds one blank row, ready for editing.</summary>
    public void AddEntry() => Entries.Add(WorkerBindingEntryViewModel.Blank(_adapterCandidates, RemoveEntry));

    private void RemoveEntry(WorkerBindingEntryViewModel entry) => Entries.Remove(entry);

    /// <summary>
    /// Builds a <see cref="WorkerBindingConfigEntry"/> dictionary from <see cref="Entries"/>'
    /// current field state — the row-level counterpart of a save attempt, also used by
    /// <see cref="RecomputeIsDirty"/> so dirty tracking and Save agree on what "current" means.
    /// </summary>
    internal bool TryBuildConfig(out Dictionary<string, WorkerBindingConfigEntry>? config, out string? error)
    {
        var built = new Dictionary<string, WorkerBindingConfigEntry>();

        foreach (var entryVm in Entries)
        {
            if (string.IsNullOrWhiteSpace(entryVm.WorkerName))
            {
                config = null;
                error = "Every entry needs a worker role name.";
                return false;
            }

            if (built.ContainsKey(entryVm.WorkerName))
            {
                config = null;
                error = $"Duplicate worker role name '{entryVm.WorkerName}'.";
                return false;
            }

            if (!entryVm.TryBuildEntry(out var entry, out var entryError))
            {
                config = null;
                error = entryError;
                return false;
            }

            built[entryVm.WorkerName] = entry!;
        }

        config = built;
        error = null;
        return true;
    }

    private void OnEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (WorkerBindingEntryViewModel entry in e.NewItems)
            {
                entry.PropertyChanged += OnEntryPropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (WorkerBindingEntryViewModel entry in e.OldItems)
            {
                entry.PropertyChanged -= OnEntryPropertyChanged;
            }
        }

        RecomputeIsDirty();
    }

    private void OnEntryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RecomputeIsDirty();

    private void UnsubscribeAll()
    {
        foreach (var entry in Entries)
        {
            entry.PropertyChanged -= OnEntryPropertyChanged;
        }
    }

    internal void RecomputeIsDirty()
    {
        if (!IsOpen || Baseline is not { } baseline)
        {
            IsDirty = false;
            return;
        }

        if (!BaselineIsPersisted)
        {
            IsDirty = true;
            return;
        }

        IsDirty = !TryBuildConfig(out var current, out _) || !ConfigEquals(current!, baseline);
    }

    private static bool ConfigEquals(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> a,
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var (key, entry) in a)
        {
            if (!b.TryGetValue(key, out var other) || !EntryEquals(entry, other))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EntryEquals(WorkerBindingConfigEntry a, WorkerBindingConfigEntry b) =>
        a.Adapter == b.Adapter
        && a.Contract.WorkerName == b.Contract.WorkerName
        && a.Contract.RequiredInputs.SequenceEqual(b.Contract.RequiredInputs)
        && a.Contract.ProducedOutputs.SequenceEqual(b.Contract.ProducedOutputs)
        && a.Contract.OptionalMetadata.SequenceEqual(b.Contract.OptionalMetadata)
        && a.PromptTemplate == b.PromptTemplate
        && a.Timeout == b.Timeout
        && a.Model == b.Model
        && a.PermissionScope == b.PermissionScope;
}
