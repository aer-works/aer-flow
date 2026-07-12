using Aer.Flow.Artifacts;
using Aer.Flow.Domain;

namespace Aer.Flow.Tests.Artifacts;

public class ArtifactManagerTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");

    private static WorkflowDefinitionSnapshot TwoStepSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("architect-critic"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
            new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
        ]);

    private static readonly IReadOnlyDictionary<StepId, ExecutionId> NoUpstream = new Dictionary<StepId, ExecutionId>();

    [Fact]
    public void AllocateOutputDirectory_creates_and_returns_the_execution_scoped_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        try
        {
            var directory = ArtifactManager.AllocateOutputDirectory(root, new ExecutionId("exec-1"));

            Assert.Equal(Path.Combine(root, "execution_exec-1"), directory);
            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AllocateOutputDirectory_is_idempotent_for_the_same_ExecutionId()
    {
        var root = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        try
        {
            var first = ArtifactManager.AllocateOutputDirectory(root, new ExecutionId("exec-1"));
            var second = ArtifactManager.AllocateOutputDirectory(root, new ExecutionId("exec-1"));

            Assert.Equal(first, second);
            Assert.True(Directory.Exists(second));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveInputPaths_returns_empty_for_a_step_with_no_declared_inputs()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [new StepState(Architect, StepStatus.Succeeded, new ExecutionId("A1"), NoUpstream)]);

        var paths = ArtifactManager.ResolveInputPaths(
            TwoStepSnapshot().Steps[0], TwoStepSnapshot(), state, "/artifacts");

        Assert.Empty(paths);
    }

    [Fact]
    public void ResolveInputPaths_resolves_an_input_to_its_producing_dependencys_output_directory()
    {
        var snapshot = TwoStepSnapshot();
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [
                new StepState(Architect, StepStatus.Succeeded, new ExecutionId("A1"), NoUpstream),
                new StepState(Critic, StepStatus.Pending, null, NoUpstream),
            ]);

        var paths = ArtifactManager.ResolveInputPaths(snapshot.Steps[1], snapshot, state, "/artifacts");

        Assert.Equal([Path.Combine("/artifacts", "execution_A1", "plan")], paths);
    }

    [Fact]
    public void ResolveInputPaths_throws_when_no_direct_dependency_declares_the_input_name()
    {
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            new WorkflowTemplateId("wf"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
                new WorkflowStepDefinition(Critic, "critic", ["nonexistent"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
            ]);
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [
                new StepState(Architect, StepStatus.Succeeded, new ExecutionId("A1"), NoUpstream),
                new StepState(Critic, StepStatus.Pending, null, NoUpstream),
            ]);

        Assert.Throws<ArtifactResolutionException>(
            () => ArtifactManager.ResolveInputPaths(snapshot.Steps[1], snapshot, state, "/artifacts"));
    }

    [Fact]
    public void ResolveInputPaths_throws_when_the_producing_dependency_has_no_successful_execution_yet()
    {
        var snapshot = TwoStepSnapshot();
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [
                new StepState(Architect, StepStatus.Pending, null, NoUpstream),
                new StepState(Critic, StepStatus.Pending, null, NoUpstream),
            ]);

        Assert.Throws<ArtifactResolutionException>(
            () => ArtifactManager.ResolveInputPaths(snapshot.Steps[1], snapshot, state, "/artifacts"));
    }

    [Fact]
    public void BuildEnvironment_numbers_inputs_in_order_and_appends_AER_OUTPUT_DIR_and_AER_ARTIFACTS_ROOT()
    {
        var variables = ArtifactManager.BuildEnvironment(
            ["/artifacts/execution_A1/plan", "/artifacts/execution_B1/goal"], "/artifacts/execution_C1", "/artifacts");

        Assert.Equal(
            [
                new EnvironmentVariable.AerComputed("AER_INPUT_0", "/artifacts/execution_A1/plan"),
                new EnvironmentVariable.AerComputed("AER_INPUT_1", "/artifacts/execution_B1/goal"),
                new EnvironmentVariable.AerComputed("AER_OUTPUT_DIR", "/artifacts/execution_C1"),
                new EnvironmentVariable.AerComputed("AER_ARTIFACTS_ROOT", "/artifacts"),
            ],
            variables);
    }

    [Fact]
    public void BuildEnvironment_with_no_inputs_still_sets_AER_OUTPUT_DIR_and_AER_ARTIFACTS_ROOT()
    {
        var variables = ArtifactManager.BuildEnvironment([], "/artifacts/execution_C1", "/artifacts");

        Assert.Equal(
            [
                new EnvironmentVariable.AerComputed("AER_OUTPUT_DIR", "/artifacts/execution_C1"),
                new EnvironmentVariable.AerComputed("AER_ARTIFACTS_ROOT", "/artifacts"),
            ],
            variables);
    }

    [Fact]
    public void BuildEnvironment_with_a_supplement_appends_AER_SUPPLEMENTARY_INPUT_after_AER_ARTIFACTS_ROOT()
    {
        var variables = ArtifactManager.BuildEnvironment(
            [], "/artifacts/execution_C1", "/artifacts", "/artifacts/execution_S1");

        Assert.Equal(
            [
                new EnvironmentVariable.AerComputed("AER_OUTPUT_DIR", "/artifacts/execution_C1"),
                new EnvironmentVariable.AerComputed("AER_ARTIFACTS_ROOT", "/artifacts"),
                new EnvironmentVariable.AerComputed("AER_SUPPLEMENTARY_INPUT", "/artifacts/execution_S1"),
            ],
            variables);
    }
}
