using Aer.Flow.Domain;

namespace Aer.Ui;

/// <summary>
/// Whether — and how — a task's bound <see cref="WorkflowDefinitionSnapshot"/> has diverged from
/// the live template it originated from (UI spec §5; M14 Phase 4, issue #121). A snapshot is frozen
/// at bind time (Flow spec §11.2) and never re-synced, so any edit to the template afterward is
/// invisible to the running task — this type is what makes that gap visible instead of silently
/// rendering the live template in the snapshot's place.
/// </summary>
/// <param name="TemplateIdMismatch">
/// The compared file is a different template entirely (<see cref="WorkflowDefinition.WorkflowTemplateId"/>
/// does not match the snapshot's). This is not "divergence" — divergence means the *same* template
/// changed over time; comparing two unrelated templates and calling it a diff would be exactly the
/// silent-substitution error §5 warns against, so when this is <c>true</c> every other field is
/// empty/<c>false</c> and the caller must render a mismatch message, never a diff.
/// </param>
/// <param name="HasDiverged">
/// <c>true</c> only when the steps themselves differ structurally (<see cref="AddedStepIds"/>,
/// <see cref="RemovedStepIds"/>, or <see cref="ChangedSteps"/> is non-empty). Deliberately not
/// derived from <see cref="SnapshotTemplateVersion"/> vs. <see cref="TemplateVersion"/> — a
/// hand-edited template can diverge without a version bump, and a version bump alone (e.g. from an
/// unrelated earlier edit that was itself later reverted) does not prove the content differs. The
/// version fields are informational only.
/// </param>
public sealed record SnapshotTemplateDiff(
    bool TemplateIdMismatch,
    bool HasDiverged,
    int SnapshotTemplateVersion,
    int TemplateVersion,
    IReadOnlyList<StepId> AddedStepIds,
    IReadOnlyList<StepId> RemovedStepIds,
    IReadOnlyList<StepDiff> ChangedSteps);

/// <summary>
/// A step present in both the snapshot and the template whose declared shape differs. Each field is
/// compared independently, by value (<see cref="IReadOnlyList{T}.SequenceEqual"/> for the list-typed
/// fields — plain record equality on <see cref="WorkflowStepDefinition"/> would compare those lists
/// by reference, since the snapshot and template are deserialized as separate object graphs, and
/// would report every field as changed on every step, always), so the rendered diff can say exactly
/// what changed rather than only that something did.
/// </summary>
public sealed record StepDiff(
    StepId StepId,
    bool WorkerChanged,
    bool InputsChanged,
    bool OutputsChanged,
    bool DependsOnChanged,
    bool RetryPolicyChanged,
    bool PausePointChanged);
