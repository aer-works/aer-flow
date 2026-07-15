using Aer.Flow.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aer.Ui;

/// <summary>
/// The template editor's state (M16 Phase 1, issue #150) — the first *authoring* surface, and the
/// first two-way-bound one: <see cref="TemplateId"/>/<see cref="TemplateVersionText"/> are edited
/// directly in <c>MainWindow.axaml</c> text boxes, exactly the shape M15's notes-for-future-work
/// entry anticipated the MVVM layer growing into. Editing is in-memory against an explicit
/// baseline with dirty tracking (the phase's editing-model decision of record): this type holds
/// the fields and the <see cref="WorkflowDefinition"/> they were loaded from; <c>MainWindow</c>'s
/// New/Open-in-editor/Save actions own all file I/O, the same split
/// <see cref="PausedStepViewModel"/> established for the decision surface.
/// <para>
/// The editor only ever touches template <em>files</em> (UI spec §4): it is a separate surface
/// from M14 Phase 3's read-only template projection — <c>MainWindow.OpenAsync</c> still routes a
/// template file straight to the read-only DAG view, untouched — and nothing here can reach a
/// bound <c>WorkflowDefinitionSnapshot</c>, which stays immutable and rendered read-only forever
/// (Flow spec §11.2; UI spec §2, §5).
/// </para>
/// </summary>
public sealed partial class TemplateEditorViewModel : ObservableObject
{
    /// <summary>
    /// The state the current fields are compared against for dirty tracking and Save's
    /// no-op/version-bump decisions — what was last loaded from or saved to disk, or the blank
    /// in-memory definition <see cref="StartNew"/> minted. <see langword="null"/> until an editing
    /// session starts. Phase 1 edits metadata only, so <see cref="Baseline"/>'s <c>Steps</c> list
    /// rides through every save untouched (step/graph editing is Phase 2, issue #151).
    /// </summary>
    internal WorkflowDefinition? Baseline { get; private set; }

    /// <summary>
    /// Whether <see cref="Baseline"/> reflects a state that exists on disk (loaded from a file, or
    /// already saved once). Flow spec §11.1's version-increment rule distinguishes two *saved*
    /// states of a template over time — a brand-new template's first save has no predecessor to
    /// distinguish from, so it must not increment; this flag is how Save tells the two apart.
    /// </summary>
    internal bool BaselineIsPersisted { get; private set; }

    /// <summary>Two-way bound to the editor's <c>WorkflowTemplateId</c> box.</summary>
    [ObservableProperty]
    private string templateId = string.Empty;

    /// <summary>
    /// Two-way bound to the editor's <c>WorkflowTemplateVersion</c> box — kept as text, not an
    /// <see cref="int"/>, because a half-typed or invalid value must be representable while
    /// editing; Save is where it either parses or fails as an in-window message.
    /// </summary>
    [ObservableProperty]
    private string templateVersionText = string.Empty;

    /// <summary>True while an editing session is open — what enables the metadata boxes and the Save button at all.</summary>
    [ObservableProperty]
    private bool isOpen;

    /// <summary>
    /// True when the fields differ from <see cref="Baseline"/> — or unconditionally for a
    /// never-yet-saved new template, whose baseline exists nowhere on disk. Re-derived on every
    /// field change; what the Save button's enabled state binds to.
    /// </summary>
    [ObservableProperty]
    private bool isDirty;

    /// <summary>In-window message surface for the editor's outcomes and failures — the same precedent <c>RunStatusText</c> established (M14 Phase 1).</summary>
    [ObservableProperty]
    private string statusText = string.Empty;

    /// <summary>
    /// Starts a fresh editing session over a blank in-memory template: no steps (an empty
    /// <c>Steps</c> list is engine-valid), version 1, id for the user to fill in. Nothing touches
    /// disk until Save.
    /// </summary>
    public void StartNew()
    {
        Baseline = new WorkflowDefinition(new WorkflowTemplateId(string.Empty), WorkflowTemplateVersion: 1, Steps: []);
        BaselineIsPersisted = false;
        TemplateId = string.Empty;
        TemplateVersionText = "1";
        IsOpen = true;
        StatusText = string.Empty;
        RecomputeIsDirty();
    }

    /// <summary>
    /// (Re)anchors the session to <paramref name="definition"/> as the on-disk state — called by
    /// Open-in-editor after a parse, and by Save after a successful write, so dirty tracking always
    /// compares against what the file actually contains.
    /// </summary>
    public void LoadFrom(WorkflowDefinition definition)
    {
        Baseline = definition;
        BaselineIsPersisted = true;
        TemplateId = definition.WorkflowTemplateId.Value;
        TemplateVersionText = definition.WorkflowTemplateVersion.ToString();
        IsOpen = true;
        RecomputeIsDirty();
    }

    /// <summary>Ends the editing session (a failed Open-in-editor must not leave a stale one behind).</summary>
    public void Close()
    {
        Baseline = null;
        BaselineIsPersisted = false;
        TemplateId = string.Empty;
        TemplateVersionText = string.Empty;
        IsOpen = false;
        RecomputeIsDirty();
    }

    partial void OnTemplateIdChanged(string value) => RecomputeIsDirty();

    partial void OnTemplateVersionTextChanged(string value) => RecomputeIsDirty();

    partial void OnIsOpenChanged(bool value) => RecomputeIsDirty();

    private void RecomputeIsDirty()
        => IsDirty = IsOpen && Baseline is { } baseline
            && (!BaselineIsPersisted
                || TemplateId != baseline.WorkflowTemplateId.Value
                || TemplateVersionText != baseline.WorkflowTemplateVersion.ToString());
}
