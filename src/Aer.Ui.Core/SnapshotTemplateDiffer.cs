using Aer.Flow.Domain;

namespace Aer.Ui.Core;

/// <summary>
/// A pure structural comparison between a task's bound <see cref="WorkflowDefinitionSnapshot"/> and
/// a live <see cref="WorkflowDefinition"/> template (UI spec §5; M14 Phase 4, issue #121) — never a
/// mutation, never consulted by anything with execution authority. Field-by-field with
/// <c>SequenceEqual</c> on every list-typed member, deliberately not the records' own generated
/// <c>Equals</c>: <see cref="WorkflowStepDefinition"/>'s <c>Inputs</c>/<c>Outputs</c>/<c>DependsOn</c>
/// are <c>IReadOnlyList&lt;T&gt;</c>, which C#'s positional-record equality compares with
/// <see cref="EqualityComparer{T}.Default"/> — reference equality for a list — so two structurally
/// identical steps loaded from separate files (always distinct list instances) would otherwise
/// compare unequal on every field, every time.
/// </summary>
public static class SnapshotTemplateDiffer
{
    public static SnapshotTemplateDiff Diff(WorkflowDefinitionSnapshot snapshot, WorkflowDefinition template)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(template);

        if (snapshot.WorkflowTemplateId != template.WorkflowTemplateId)
        {
            return new SnapshotTemplateDiff(
                TemplateIdMismatch: true,
                HasDiverged: false,
                snapshot.WorkflowTemplateVersion,
                template.WorkflowTemplateVersion,
                AddedStepIds: [],
                RemovedStepIds: [],
                ChangedSteps: []);
        }

        var snapshotStepById = snapshot.Steps.ToDictionary(step => step.StepId);
        var templateStepById = template.Steps.ToDictionary(step => step.StepId);

        var addedStepIds = templateStepById.Keys.Except(snapshotStepById.Keys).ToList();
        var removedStepIds = snapshotStepById.Keys.Except(templateStepById.Keys).ToList();

        var changedSteps = new List<StepDiff>();
        foreach (var stepId in snapshotStepById.Keys.Intersect(templateStepById.Keys))
        {
            var snapshotStep = snapshotStepById[stepId];
            var templateStep = templateStepById[stepId];

            var diff = new StepDiff(
                stepId,
                WorkerChanged: snapshotStep.Worker != templateStep.Worker,
                InputsChanged: !snapshotStep.Inputs.SequenceEqual(templateStep.Inputs),
                OutputsChanged: !snapshotStep.Outputs.SequenceEqual(templateStep.Outputs),
                DependsOnChanged: !snapshotStep.DependsOn.SequenceEqual(templateStep.DependsOn),
                RetryPolicyChanged: snapshotStep.RetryPolicy.MaxAttempts != templateStep.RetryPolicy.MaxAttempts,
                PausePointChanged: !PausePointsEqual(snapshotStep.PausePoint, templateStep.PausePoint));

            if (diff.WorkerChanged || diff.InputsChanged || diff.OutputsChanged || diff.DependsOnChanged ||
                diff.RetryPolicyChanged || diff.PausePointChanged)
            {
                changedSteps.Add(diff);
            }
        }

        var hasDiverged = addedStepIds.Count > 0 || removedStepIds.Count > 0 || changedSteps.Count > 0;

        return new SnapshotTemplateDiff(
            TemplateIdMismatch: false,
            hasDiverged,
            snapshot.WorkflowTemplateVersion,
            template.WorkflowTemplateVersion,
            addedStepIds,
            removedStepIds,
            changedSteps);
    }

    private static bool PausePointsEqual(PausePoint? snapshotPausePoint, PausePoint? templatePausePoint)
    {
        if (snapshotPausePoint is null || templatePausePoint is null)
        {
            return snapshotPausePoint is null && templatePausePoint is null;
        }

        return snapshotPausePoint.SupersedeTargets.SequenceEqual(templatePausePoint.SupersedeTargets);
    }
}
