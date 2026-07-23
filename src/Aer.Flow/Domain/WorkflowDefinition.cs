namespace Aer.Flow.Domain;

/// <summary>
/// Declarative structure only — no loops, no conditionals, no runtime logic (spec §11.1). Editable
/// and versionable; not itself bound to any running task. <see cref="WorkflowTemplateVersion"/>
/// increments on every edit that is instantiated from.
/// </summary>
public sealed record WorkflowDefinition(
    WorkflowTemplateId WorkflowTemplateId,
    int WorkflowTemplateVersion,
    IReadOnlyList<WorkflowStepDefinition> Steps);

/// <summary>A single step in a <see cref="WorkflowDefinition"/> template (spec §11.1).</summary>
public sealed record WorkflowStepDefinition(
    StepId StepId,
    string Worker,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<StepId> DependsOn,
    RetryPolicy RetryPolicy,
    PausePoint? PausePoint = null);

/// <summary>Governs whether a failure triggers a new <see cref="ExecutionRequest"/> (spec §10).</summary>
public sealed record RetryPolicy(int MaxAttempts);

/// <summary>
/// Distinguishes <em>why</em> a <see cref="PausePoint"/> stopped the DAG, so the two human acts a
/// pause can demand — answering a question versus approving finished work — render and filter as the
/// separate states they are (spec §17.1, issue #334). A pause's kind is a static property of the step
/// that declares the pause point: it is invariant per declaration, never a per-execution worker
/// signal (execution outcomes carry no done/needs-input flag — see <see cref="FlowEvent.ExecutionSucceeded"/>).
/// It is therefore derived from the bound <see cref="WorkflowDefinitionSnapshot"/> at projection time
/// and carried by no <see cref="FlowEvent"/>; the snapshot is itself part of the durable, write-once
/// record, so no event-format change or replay migration is required.
/// </summary>
public enum PausePointKind
{
    /// <summary>
    /// The step ran to a terminal outcome and its result awaits human review/approval before the DAG
    /// proceeds — the approval gate. The historical meaning of every pause, and deliberately the
    /// zero value: a snapshot serialized before this field existed omits it, and STJ materializes the
    /// missing value as <c>default(PausePointKind)</c>, which must land here for replay to stay correct.
    /// </summary>
    ReadyForReview = 0,

    /// <summary>
    /// The step is an interactive turn paused ready for the operator's next message. It is not
    /// "awaiting approval," it is "awaiting input" — an ordinary chat turn, which must not demand a
    /// review decision. Declared only by interactive-session steps (see
    /// <c>Aer.Adapters.InteractiveSessionMaterializer</c>), never inferred from conversation content
    /// (Architecture Rule 1).
    /// </summary>
    NeedsInput = 1,
}

/// <summary>
/// Declared on a step to have Flow append <see cref="FlowEvent.WorkflowPaused"/> instead of immediately
/// evaluating downstream readiness when the step reaches a terminal outcome (spec §17.1).
/// </summary>
/// <param name="SupersedeTargets">
/// The set of earlier <see cref="StepId"/>s a <see cref="DecisionType.Supersede"/> decision made at
/// this pause point may target. Every entry shall be a unique, transitive ancestor of the step
/// declaring this pause point. Empty means this pause point supports
/// <see cref="DecisionType.Resume"/>/<see cref="DecisionType.Reject"/>/<see cref="DecisionType.RetryWithRevision"/>
/// only.
/// </param>
/// <param name="Kind">
/// Which human act this pause demands (issue #334). Defaults to <see cref="PausePointKind.ReadyForReview"/>
/// so every authored review gate and every pause persisted before this field existed keeps its
/// original approval-gate meaning; only interactive-session steps opt into
/// <see cref="PausePointKind.NeedsInput"/>.
/// </param>
public sealed record PausePoint(
    IReadOnlyList<StepId> SupersedeTargets,
    PausePointKind Kind = PausePointKind.ReadyForReview);
