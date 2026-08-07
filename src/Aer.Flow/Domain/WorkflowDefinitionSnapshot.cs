namespace Aer.Flow.Domain;

/// <summary>
/// A frozen copy of a <see cref="WorkflowDefinition"/> template as it existed when a room was
/// created (spec §11.2). A running or historical room is permanently bound to the snapshot it was
/// created from; Flow never mutates or patches a snapshot once a room is bound to it.
/// record-once-ok: #443 spec/aer-flow-behavioral-spec-v1.0.md
/// </summary>
public sealed record WorkflowDefinitionSnapshot(
    WorkflowDefinitionSnapshotId WorkflowDefinitionSnapshotId,
    WorkflowTemplateId WorkflowTemplateId,
    int WorkflowTemplateVersion,
    IReadOnlyList<WorkflowStepDefinition> Steps);
