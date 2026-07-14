using Aer.Flow.Domain;
using Aer.Flow.Templates;

namespace Aer.Ui.Tests;

/// <summary>
/// Unit-level coverage for <see cref="SnapshotTemplateDiffer"/> (UI spec §5; M14 Phase 4, issue #121).
/// Every snapshot/template pair here is built from two independently constructed
/// <see cref="WorkflowStepDefinition"/> lists, never the same in-memory instance reused for both
/// sides — the shape that would hide a positional-record-equality bug (comparing
/// <c>IReadOnlyList&lt;string&gt;</c> members by reference) behind a false-negative "no divergence
/// detected" test.
/// </summary>
public class SnapshotTemplateDifferTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");
    private static readonly WorkflowTemplateId TemplateId = new("architect-critic");

    private static WorkflowDefinitionSnapshot Snapshot(IReadOnlyList<WorkflowStepDefinition> steps, int version = 1) =>
        SnapshotBinder.Bind(new WorkflowDefinition(TemplateId, version, steps));

    private static WorkflowDefinition Template(IReadOnlyList<WorkflowStepDefinition> steps, int version = 1) =>
        new(TemplateId, version, steps);

    private static WorkflowStepDefinition ArchitectStep() =>
        new(Architect, "architect", Inputs: ["goal"], Outputs: ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(3));

    private static WorkflowStepDefinition CriticStep(StepId[]? supersedeTargets = null) =>
        new(
            Critic, "critic", Inputs: ["plan"], Outputs: ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1),
            PausePoint: supersedeTargets is null ? null : new PausePoint(supersedeTargets));

    [Fact]
    public void Structurally_identical_steps_built_as_separate_instances_show_no_divergence()
    {
        // Regression coverage: the snapshot's and template's Inputs/Outputs/DependsOn lists are
        // always distinct List<string> instances once loaded from separate sources — plain record
        // equality (EqualityComparer<T>.Default on a list member) would compare them by reference and
        // report every field changed here, always. SequenceEqual is what makes this pass correctly.
        var snapshot = Snapshot([ArchitectStep(), CriticStep()]);
        var template = Template([ArchitectStep(), CriticStep()]);

        var diff = SnapshotTemplateDiffer.Diff(snapshot, template);

        Assert.False(diff.TemplateIdMismatch);
        Assert.False(diff.HasDiverged);
        Assert.Empty(diff.AddedStepIds);
        Assert.Empty(diff.RemovedStepIds);
        Assert.Empty(diff.ChangedSteps);
    }

    [Fact]
    public void A_different_WorkflowTemplateId_is_reported_as_a_mismatch_never_a_diff()
    {
        var snapshot = Snapshot([ArchitectStep()]);
        var template = new WorkflowDefinition(new WorkflowTemplateId("something-else"), 1, [ArchitectStep()]);

        var diff = SnapshotTemplateDiffer.Diff(snapshot, template);

        Assert.True(diff.TemplateIdMismatch);
        Assert.False(diff.HasDiverged);
        Assert.Empty(diff.AddedStepIds);
        Assert.Empty(diff.RemovedStepIds);
        Assert.Empty(diff.ChangedSteps);
    }

    [Fact]
    public void A_version_bump_alone_with_identical_steps_does_not_count_as_diverged()
    {
        var snapshot = Snapshot([ArchitectStep(), CriticStep()], version: 1);
        var template = Template([ArchitectStep(), CriticStep()], version: 2);

        var diff = SnapshotTemplateDiffer.Diff(snapshot, template);

        Assert.False(diff.HasDiverged);
        Assert.Equal(1, diff.SnapshotTemplateVersion);
        Assert.Equal(2, diff.TemplateVersion);
    }

    [Fact]
    public void A_step_added_in_the_template_is_reported()
    {
        var snapshot = Snapshot([ArchitectStep()]);
        var template = Template([ArchitectStep(), CriticStep()]);

        var diff = SnapshotTemplateDiffer.Diff(snapshot, template);

        Assert.True(diff.HasDiverged);
        Assert.Equal([Critic], diff.AddedStepIds);
        Assert.Empty(diff.RemovedStepIds);
    }

    [Fact]
    public void A_step_removed_from_the_template_is_reported()
    {
        var snapshot = Snapshot([ArchitectStep(), CriticStep()]);
        var template = Template([ArchitectStep()]);

        var diff = SnapshotTemplateDiffer.Diff(snapshot, template);

        Assert.True(diff.HasDiverged);
        Assert.Equal([Critic], diff.RemovedStepIds);
        Assert.Empty(diff.AddedStepIds);
    }

    [Fact]
    public void A_changed_worker_is_flagged_on_the_step()
    {
        var snapshot = Snapshot([ArchitectStep()]);
        var template = Template([ArchitectStep() with { Worker = "architect-v2" }]);

        var diff = SnapshotTemplateDiffer.Diff(snapshot, template);

        var changed = Assert.Single(diff.ChangedSteps);
        Assert.Equal(Architect, changed.StepId);
        Assert.True(changed.WorkerChanged);
        Assert.False(changed.InputsChanged);
        Assert.False(changed.OutputsChanged);
        Assert.False(changed.DependsOnChanged);
        Assert.False(changed.RetryPolicyChanged);
        Assert.False(changed.PausePointChanged);
    }

    [Fact]
    public void A_changed_inputs_list_is_flagged_via_SequenceEqual_not_reference_equality()
    {
        var snapshot = Snapshot([ArchitectStep()]);
        var template = Template([ArchitectStep() with { Inputs = ["goal", "extra-context"] }]);

        var diff = SnapshotTemplateDiffer.Diff(snapshot, template);

        var changed = Assert.Single(diff.ChangedSteps);
        Assert.True(changed.InputsChanged);
        Assert.False(changed.OutputsChanged);
    }

    [Fact]
    public void A_changed_retry_policy_is_flagged()
    {
        var snapshot = Snapshot([ArchitectStep()]);
        var template = Template([ArchitectStep() with { RetryPolicy = new RetryPolicy(5) }]);

        var diff = SnapshotTemplateDiffer.Diff(snapshot, template);

        Assert.True(Assert.Single(diff.ChangedSteps).RetryPolicyChanged);
    }

    [Fact]
    public void Adding_a_pause_point_in_the_template_is_flagged()
    {
        var snapshot = Snapshot([ArchitectStep(), CriticStep()]);
        var template = Template([ArchitectStep(), CriticStep(supersedeTargets: [Architect])]);

        var diff = SnapshotTemplateDiffer.Diff(snapshot, template);

        var changed = Assert.Single(diff.ChangedSteps);
        Assert.Equal(Critic, changed.StepId);
        Assert.True(changed.PausePointChanged);
    }

    [Fact]
    public void A_pause_point_with_identical_supersede_targets_built_separately_is_not_flagged()
    {
        var snapshot = Snapshot([ArchitectStep(), CriticStep(supersedeTargets: [Architect])]);
        var template = Template([ArchitectStep(), CriticStep(supersedeTargets: [Architect])]);

        var diff = SnapshotTemplateDiffer.Diff(snapshot, template);

        Assert.False(diff.HasDiverged);
    }
}
