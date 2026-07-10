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
public sealed record PausePoint(IReadOnlyList<StepId> SupersedeTargets);
