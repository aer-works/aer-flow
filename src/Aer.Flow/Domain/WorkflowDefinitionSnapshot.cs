namespace Aer.Flow.Domain;

/// <summary>
/// A frozen copy of a <see cref="WorkflowDefinition"/> template as it existed when a task was
/// created (spec §11.2). A running or historical task is permanently bound to the snapshot it was
/// created from; Flow never mutates or patches a snapshot once a task is bound to it.
/// </summary>
public sealed record WorkflowDefinitionSnapshot(
    WorkflowDefinitionSnapshotId WorkflowDefinitionSnapshotId,
    WorkflowTemplateId WorkflowTemplateId,
    int WorkflowTemplateVersion,
    IReadOnlyList<WorkflowStepDefinition> Steps);
