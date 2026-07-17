using System.Collections.ObjectModel;
using Aer.Flow.Domain;
using Aer.Flow.Templates;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aer.Ui.Core;

/// <summary>
/// The template editor's state (M16 Phase 1, issue #150; extended for Phase 2, issue #151) — the
/// first *authoring* surface, and the first two-way-bound one: <see cref="TemplateId"/>/
/// <see cref="TemplateVersionText"/> and, since Phase 2, every <see cref="Steps"/> entry's fields are
/// edited directly in <c>MainWindow.axaml</c> controls, exactly the shape M15's
/// notes-for-future-work entry anticipated the MVVM layer growing into. Editing is in-memory against
/// an explicit baseline with dirty tracking (Phase 1's editing-model decision of record): this type
/// holds the fields and the <see cref="WorkflowDefinition"/> they were loaded from; <c>MainWindow</c>'s
/// New/Open-in-editor/Save actions own all file I/O, the same split
/// <see cref="PausedStepViewModel"/> established for the decision surface.
/// <para>
/// The editor only ever touches template <em>files</em> (UI spec §4): it is a separate surface
/// from M14 Phase 3's read-only template projection — <c>MainWindow.OpenAsync</c> still routes a
/// template file straight to the read-only DAG view, untouched — and nothing here can reach a
/// bound <c>WorkflowDefinitionSnapshot</c>, which stays immutable and rendered read-only forever
/// (Flow spec §11.2; UI spec §2, §5).
/// </para>
/// <para>
/// <b>Save-validity decision of record (Phase 2):</b> Save stays blocked until
/// <see cref="BuildCandidate"/> both parses and passes <see cref="WorkflowDefinitionValidator"/> —
/// Phase 1's "the writer validates before writing" rule is not loosened. There is no draft-storage
/// concept anywhere else in the stack (a template file is the sole authoring artifact and exactly
/// what instantiation reads), and a blocked Save loses no in-progress work: the in-memory
/// <see cref="Steps"/>/field state persists for the whole editing session regardless of whether it is
/// currently valid, so the user can keep editing across a temporarily-invalid intermediate state and
/// only needs to reach validity once, at the moment they actually choose to save.
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
    /// The §8 structural-editing surface (M16 Phase 2, issue #151): one entry per step in the
    /// in-progress edit, positional (append on <see cref="AddStep"/>, remove on
    /// <see cref="StepEditorViewModel.Remove"/> — no reorder surface). Rebuilt from
    /// <see cref="Baseline"/> whenever a session (re)starts.
    /// </summary>
    public ObservableCollection<StepEditorViewModel> Steps { get; } = [];

    /// <summary>
    /// Every violation <see cref="WorkflowDefinitionValidator.Validate"/> (or field parsing) currently
    /// finds against <see cref="BuildCandidate"/>'s result — collected, not fail-fast, exactly the
    /// shape the validator already returns (Phase 2). Empty exactly when the current in-progress
    /// state may be saved.
    /// </summary>
    public ObservableCollection<string> ValidationErrors { get; } = [];

    /// <summary>
    /// The live DAG preview over the in-progress edit (Phase 2) — <see langword="null"/> whenever
    /// <see cref="ValidationErrors"/> is non-empty, deliberately: <see cref="DagLayoutEngine.Layout"/>
    /// assumes an already-structurally-valid graph (acyclic, every <c>DependsOn</c> reference
    /// resolvable) and does not itself guard against a cycle or a dangling reference, so re-layout is
    /// gated on validation passing rather than attempted against a graph that could hang or throw.
    /// </summary>
    [ObservableProperty]
    private DagLayout? previewLayout;

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
        RebuildStepsFromBaseline();
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
        RebuildStepsFromBaseline();
    }

    /// <summary>Ends the editing session (a failed Open-in-editor must not leave a stale one behind).</summary>
    public void Close()
    {
        Baseline = null;
        BaselineIsPersisted = false;
        TemplateId = string.Empty;
        TemplateVersionText = string.Empty;
        IsOpen = false;
        Steps.Clear();
        RecomputeDerivedState();
    }

    /// <summary>Appends a new step with a default, non-colliding <see cref="StepId"/> and one retry attempt — nothing else pre-filled (Phase 2).</summary>
    public void AddStep()
    {
        var step = new StepEditorViewModel(RecomputeDerivedState, RemoveStep) { StepId = NextDefaultStepId() };
        Steps.Add(step);
        RecomputeDerivedState();
    }

    private void RemoveStep(StepEditorViewModel step)
    {
        Steps.Remove(step);
        RecomputeDerivedState();
    }

    private string NextDefaultStepId()
    {
        var candidate = Steps.Count + 1;
        while (Steps.Any(step => step.StepId == $"step-{candidate}"))
        {
            candidate++;
        }

        return $"step-{candidate}";
    }

    private void RebuildStepsFromBaseline()
    {
        Steps.Clear();

        if (Baseline is { } baseline)
        {
            foreach (var step in baseline.Steps)
            {
                var stepVm = new StepEditorViewModel(RecomputeDerivedState, RemoveStep, step.PausePoint)
                {
                    StepId = step.StepId.Value,
                    Worker = step.Worker,
                    InputsText = string.Join(", ", step.Inputs),
                    OutputsText = string.Join(", ", step.Outputs),
                    MaxAttemptsText = step.RetryPolicy.MaxAttempts.ToString(),
                };

                foreach (var dependency in step.DependsOn)
                {
                    stepVm.SelectedDependsOn.Add(dependency.Value);
                }

                Steps.Add(stepVm);
            }
        }

        RecomputeDerivedState();
    }

    /// <summary>
    /// Rebuilds every step's <see cref="StepEditorViewModel.DependsOnOptions"/> from the template's
    /// current declared steps (excluding the step itself) — candidates always reflect the live edit,
    /// never a stale snapshot (§8; M15's send-back "reflect, don't invent" discipline). A step whose
    /// <see cref="StepEditorViewModel.StepId"/> changes elsewhere simply stops being an offered
    /// candidate under its old text; an existing selection keyed to that old text is not carried
    /// forward under the new one — a rename is a new identity from the reference graph's perspective,
    /// the same way <see cref="WorkflowDefinitionValidator"/> would treat a stale reference as
    /// unresolved rather than silently repaired.
    /// </summary>
    internal void RefreshDependsOnOptions()
    {
        foreach (var step in Steps)
        {
            step.DependsOnOptions.Clear();

            foreach (var other in Steps)
            {
                if (ReferenceEquals(other, step))
                {
                    continue;
                }

                step.DependsOnOptions.Add(
                    new DependsOnOptionViewModel(other.StepId, step.SelectedDependsOn.Contains(other.StepId), step));
            }
        }
    }

    /// <summary>
    /// Builds a candidate <see cref="WorkflowDefinition"/> from the editor's current field state
    /// (Phase 2). <c>Candidate</c> is <see langword="null"/> only when a field cannot even parse
    /// (a non-numeric version or <c>RetryPolicy.MaxAttempts</c>) — otherwise it is always returned,
    /// valid or not, so callers can still use it for dirty comparison against <see cref="Baseline"/>
    /// while mid-edit. <c>Errors</c> carries parse failures and/or
    /// <see cref="WorkflowDefinitionValidator.Validate"/>'s full violation list — never re-implementing
    /// a structural rule, only calling Flow's own validator (the authoring counterpart of "Flow
    /// carries discipline").
    /// </summary>
    internal (WorkflowDefinition? Candidate, IReadOnlyList<string> Errors) BuildCandidate()
    {
        var errors = new List<string>();

        if (!int.TryParse(TemplateVersionText, out var version))
        {
            errors.Add($"Version '{TemplateVersionText}' is not a whole number.");
        }

        var steps = new List<WorkflowStepDefinition>();
        foreach (var stepVm in Steps)
        {
            if (!int.TryParse(stepVm.MaxAttemptsText, out var maxAttempts))
            {
                errors.Add($"Step '{stepVm.StepId}' has a non-numeric RetryPolicy.MaxAttempts '{stepVm.MaxAttemptsText}'.");
                continue;
            }

            steps.Add(new WorkflowStepDefinition(
                new StepId(stepVm.StepId),
                stepVm.Worker,
                ParseList(stepVm.InputsText),
                ParseList(stepVm.OutputsText),
                stepVm.SelectedDependsOn.Select(id => new StepId(id)).ToList(),
                new RetryPolicy(maxAttempts),
                stepVm.HasPausePoint
                    ? new PausePoint(stepVm.SelectedSupersedeTargets.Select(id => new StepId(id)).ToList())
                    : null));
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var candidate = new WorkflowDefinition(new WorkflowTemplateId(TemplateId), version, steps);

        try
        {
            WorkflowDefinitionValidator.Validate(candidate);
        }
        catch (WorkflowDefinitionValidationException ex)
        {
            return (candidate, ex.Errors);
        }

        return (candidate, []);
    }

    private static IReadOnlyList<string> ParseList(string text)
        => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Structural equality between two <see cref="WorkflowDefinition"/>s for dirty-tracking and the
    /// no-op-save check (Phase 2) — record equality alone is insufficient once <see cref="Steps"/> is
    /// editable, because every list-typed member (<c>Steps</c> itself, and each step's
    /// <c>Inputs</c>/<c>Outputs</c>/<c>DependsOn</c>) compares by reference under the default record
    /// <c>Equals</c>, not by content. <c>DependsOn</c> compares as a set (order carries no meaning —
    /// the validator itself never treats it as ordered); every other list compares in sequence.
    /// </summary>
    internal static bool DefinitionsAreEqual(WorkflowDefinition a, WorkflowDefinition b)
    {
        if (a.WorkflowTemplateId != b.WorkflowTemplateId || a.WorkflowTemplateVersion != b.WorkflowTemplateVersion)
        {
            return false;
        }

        if (a.Steps.Count != b.Steps.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Steps.Count; i++)
        {
            if (!StepsAreEqual(a.Steps[i], b.Steps[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool StepsAreEqual(WorkflowStepDefinition a, WorkflowStepDefinition b)
        => a.StepId == b.StepId
            && a.Worker == b.Worker
            && a.Inputs.SequenceEqual(b.Inputs)
            && a.Outputs.SequenceEqual(b.Outputs)
            && a.DependsOn.ToHashSet().SetEquals(b.DependsOn)
            && a.RetryPolicy == b.RetryPolicy
            && PausePointsAreEqual(a.PausePoint, b.PausePoint);

    /// <summary>
    /// <see cref="PausePoint"/> equality by content, not reference (Phase 3) — <see cref="BuildCandidate"/>
    /// constructs a fresh <see cref="PausePoint"/> from <see cref="StepEditorViewModel.HasPausePoint"/>/
    /// <see cref="StepEditorViewModel.SelectedSupersedeTargets"/> on every call, so the pass-through
    /// reference equality Phase 2 relied on (a loaded step's <c>PausePoint</c> rode through untouched)
    /// no longer holds once <c>PausePoint</c> itself is editable. <c>SupersedeTargets</c> compares as a
    /// set, the same reasoning <c>DependsOn</c> uses.
    /// </summary>
    private static bool PausePointsAreEqual(PausePoint? a, PausePoint? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.SupersedeTargets.ToHashSet().SetEquals(b.SupersedeTargets);
    }

    partial void OnTemplateIdChanged(string value) => RecomputeDerivedState();

    partial void OnTemplateVersionTextChanged(string value) => RecomputeDerivedState();

    partial void OnIsOpenChanged(bool value) => RecomputeDerivedState();

    /// <summary>
    /// Rebuilds every step's <see cref="StepEditorViewModel.SupersedeTargetOptions"/> from
    /// <paramref name="validCandidate"/>'s actual transitive-ancestor sets (Phase 3) — reflecting the
    /// live in-edit graph, never a stale or invented candidate (§8; §17.1). <see langword="null"/>
    /// (the current state does not fully validate) leaves every step's existing options untouched
    /// rather than clearing them: <c>WorkflowDefinitionValidator.ComputeTransitiveAncestors</c> assumes
    /// an already-acyclic, fully-resolvable graph, so it is only safe to call once
    /// <see cref="ValidationErrors"/> is empty — the same DAG-layout-safety reasoning
    /// <see cref="PreviewLayout"/> already follows. An option list going briefly stale mid-edit is
    /// harmless: <see cref="StepEditorViewModel.SelectedSupersedeTargets"/> is the authoritative
    /// selection state regardless of whether its offered options are current.
    /// </summary>
    internal void RefreshSupersedeTargetOptions(WorkflowDefinition? validCandidate)
    {
        if (validCandidate is null)
        {
            return;
        }

        var ancestorsByStep = WorkflowDefinitionValidator.ComputeTransitiveAncestors(validCandidate);

        foreach (var stepVm in Steps)
        {
            var ancestors = ancestorsByStep.TryGetValue(new StepId(stepVm.StepId), out var found)
                ? found
                : (IReadOnlySet<StepId>)new HashSet<StepId>();

            stepVm.SupersedeTargetOptions.Clear();
            foreach (var candidateStep in Steps)
            {
                if (!ancestors.Contains(new StepId(candidateStep.StepId)))
                {
                    continue;
                }

                stepVm.SupersedeTargetOptions.Add(new SupersedeTargetOptionViewModel(
                    candidateStep.StepId,
                    stepVm.SelectedSupersedeTargets.Contains(candidateStep.StepId),
                    stepVm));
            }
        }
    }

    /// <summary>
    /// Recomputes every derived surface from the editor's current field state, in the order each
    /// depends on the last: candidate-dependent DAG-candidate labels first, then the candidate itself,
    /// then <see cref="ValidationErrors"/>/<see cref="PreviewLayout"/>/<see cref="IsDirty"/> from it
    /// (Phase 2; extended for Phase 3's <see cref="RefreshSupersedeTargetOptions"/>). Called on every
    /// field change — the same "re-derived, not remembered" discipline the rest of the window already
    /// follows, just reached from a two-way-bound field instead of a re-projection.
    /// </summary>
    private void RecomputeDerivedState()
    {
        RefreshDependsOnOptions();

        var (candidate, errors) = BuildCandidate();

        ValidationErrors.Clear();
        foreach (var error in errors)
        {
            ValidationErrors.Add(error);
        }

        var validCandidate = errors.Count == 0 ? candidate : null;

        PreviewLayout = validCandidate is { } validated ? DagLayoutEngine.Layout(validated.Steps) : null;

        RefreshSupersedeTargetOptions(validCandidate);

        IsDirty = IsOpen && Baseline is { } baseline
            && (!BaselineIsPersisted || candidate is null || !DefinitionsAreEqual(candidate, baseline));
    }
}
