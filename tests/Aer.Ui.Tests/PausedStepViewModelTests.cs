using Aer.Flow.Domain;

namespace Aer.Ui.Tests;

/// <summary>
/// M15 Phase 2 (issue #138), extended for Phase 3's artifact-carrying decisions (issue #139): the
/// ViewModel layer in isolation, no Avalonia headless session needed —
/// <see cref="MainWindowDecisionTests"/> covers the same surface end to end through the real
/// <see cref="MainWindow"/>. These tests are the plain unit-test level: the §7 label→<see cref="DecisionType"/>
/// mapping (including Retry→RetryWithRevision and Send back→Supersede), the mandatory-vs-optional
/// supplementary-artifact gating (§7's UI constraints), and the shared mutation-in-flight flag
/// disabling every paused step's actions at once.
/// </summary>
public class PausedStepViewModelTests
{
    private static PausedStepViewModel NewViewModel(
        IReadOnlyList<StepId>? supersedeTargets = null, DecideDelegate? decide = null) => new(
        new StepId("a"), new ExecutionId("exec-1"), supersedeTargets ?? [], decide ?? ((_, _, _, _, _, _, _) => Task.CompletedTask));

    [Fact]
    public async Task ApproveCommand_records_DecisionType_Resume_with_no_target_or_supplementary_artifact()
    {
        DecisionType? recorded = null;
        StepId? recordedTarget = null;
        string? recordedRevisionFilePath = null;
        var viewModel = NewViewModel(decide: (_, _, decisionType, targetStepId, revisionFilePath, _, _) =>
        {
            recorded = decisionType;
            recordedTarget = targetStepId;
            recordedRevisionFilePath = revisionFilePath;
            return Task.CompletedTask;
        });

        await viewModel.ApproveCommand.ExecuteAsync(null);

        Assert.Equal(DecisionType.Resume, recorded);
        Assert.Null(recordedTarget);
        Assert.Null(recordedRevisionFilePath);
    }

    [Fact]
    public async Task RejectCommand_records_DecisionType_Reject()
    {
        DecisionType? recorded = null;
        var viewModel = NewViewModel(decide: (_, _, decisionType, _, _, _, _) =>
        {
            recorded = decisionType;
            return Task.CompletedTask;
        });

        await viewModel.RejectCommand.ExecuteAsync(null);

        Assert.Equal(DecisionType.Reject, recorded);
    }

    [Fact]
    public async Task Decide_is_invoked_with_this_steps_own_StepId_and_ExecutionId()
    {
        (StepId StepId, ExecutionId ExecutionId)? recorded = null;
        var viewModel = new PausedStepViewModel(
            new StepId("critic"), new ExecutionId("exec-critic-1"), [],
            (stepId, executionId, _, _, _, _, _) =>
            {
                recorded = (stepId, executionId);
                return Task.CompletedTask;
            });

        await viewModel.ApproveCommand.ExecuteAsync(null);

        Assert.Equal((new StepId("critic"), new ExecutionId("exec-critic-1")), recorded);
    }

    [Fact]
    public void Label_names_the_step_and_its_paused_execution()
    {
        var viewModel = NewViewModel();

        Assert.Equal("a (exec-1)", viewModel.Label);
    }

    [Fact]
    public void Commands_are_disabled_once_IsEnabled_is_false()
    {
        var viewModel = NewViewModel();
        viewModel.IsEnabled = false;

        Assert.False(viewModel.ApproveCommand.CanExecute(null));
        Assert.False(viewModel.RejectCommand.CanExecute(null));
        Assert.False(viewModel.RetryCommand.CanExecute(null));
    }

    [Fact]
    public void MainWindowViewModel_disables_every_paused_step_while_a_mutation_is_in_flight()
    {
        var owner = new MainWindowViewModel();
        var stepA = NewViewModel();
        var stepB = new PausedStepViewModel(new StepId("b"), new ExecutionId("exec-b"), [], (_, _, _, _, _, _, _) => Task.CompletedTask);
        owner.PausedSteps.Add(stepA);
        owner.PausedSteps.Add(stepB);

        owner.IsMutationInFlight = true;

        Assert.False(stepA.IsEnabled);
        Assert.False(stepB.IsEnabled);
        Assert.False(stepA.ApproveCommand.CanExecute(null));
        Assert.False(stepB.RejectCommand.CanExecute(null));

        owner.IsMutationInFlight = false;

        Assert.True(stepA.IsEnabled);
        Assert.True(stepB.IsEnabled);
    }

    [Fact]
    public async Task RetryCommand_records_DecisionType_RetryWithRevision_with_no_supplementary_artifact_when_no_revision_file_is_given()
    {
        DecisionType? recorded = null;
        string? recordedRevisionFilePath = "unset";
        string? recordedWorker = "unset";
        string? recordedOutputName = "unset";
        var viewModel = NewViewModel(decide: (_, _, decisionType, _, revisionFilePath, worker, outputName) =>
        {
            recorded = decisionType;
            recordedRevisionFilePath = revisionFilePath;
            recordedWorker = worker;
            recordedOutputName = outputName;
            return Task.CompletedTask;
        });

        await viewModel.RetryCommand.ExecuteAsync(null);

        Assert.Equal(DecisionType.RetryWithRevision, recorded);
        Assert.Null(recordedRevisionFilePath);
        Assert.Null(recordedWorker);
        Assert.Null(recordedOutputName);
    }

    [Fact]
    public async Task RetryCommand_carries_the_supplementary_triple_when_a_revision_file_is_given()
    {
        string? recordedRevisionFilePath = null;
        string? recordedWorker = null;
        string? recordedOutputName = null;
        var viewModel = NewViewModel(decide: (_, _, _, _, revisionFilePath, worker, outputName) =>
        {
            recordedRevisionFilePath = revisionFilePath;
            recordedWorker = worker;
            recordedOutputName = outputName;
            return Task.CompletedTask;
        });
        viewModel.RevisionFilePath = "/tmp/revision.txt";
        viewModel.SupplementaryWorker = "human";
        viewModel.SupplementaryOutputName = "revision";

        await viewModel.RetryCommand.ExecuteAsync(null);

        Assert.Equal("/tmp/revision.txt", recordedRevisionFilePath);
        Assert.Equal("human", recordedWorker);
        Assert.Equal("revision", recordedOutputName);
    }

    [Fact]
    public void RetryCommand_is_disabled_when_a_revision_file_is_given_without_a_worker_and_output_name()
    {
        var viewModel = NewViewModel();

        viewModel.RevisionFilePath = "/tmp/revision.txt";

        Assert.False(viewModel.RetryCommand.CanExecute(null));

        viewModel.SupplementaryWorker = "human";
        viewModel.SupplementaryOutputName = "revision";

        Assert.True(viewModel.RetryCommand.CanExecute(null));
    }

    [Fact]
    public void SendBackTargets_has_no_entries_when_the_pause_point_declares_no_SupersedeTargets()
    {
        var viewModel = NewViewModel();

        Assert.Empty(viewModel.SendBackTargets);
    }

    [Fact]
    public void SendBackTargets_has_one_entry_per_declared_SupersedeTargets_entry()
    {
        var viewModel = NewViewModel([new StepId("source"), new StepId("draft")]);

        Assert.Equal(
            [new StepId("source"), new StepId("draft")],
            viewModel.SendBackTargets.Select(target => target.TargetStepId).ToList());
        Assert.Equal("Send back to source", viewModel.SendBackTargets[0].Label);
    }

    [Fact]
    public void SendBackCommand_is_disabled_until_the_revision_file_worker_and_output_name_are_all_filled_in()
    {
        var viewModel = NewViewModel([new StepId("source")]);
        var target = Assert.Single(viewModel.SendBackTargets);

        Assert.False(target.SendBackCommand.CanExecute(null));

        viewModel.RevisionFilePath = "/tmp/revision.txt";
        Assert.False(target.SendBackCommand.CanExecute(null));

        viewModel.SupplementaryWorker = "human";
        Assert.False(target.SendBackCommand.CanExecute(null));

        viewModel.SupplementaryOutputName = "revision";
        Assert.True(target.SendBackCommand.CanExecute(null));
    }

    [Fact]
    public void SendBackCommand_is_disabled_once_the_owning_step_is_disabled_even_with_a_complete_supplementary_triple()
    {
        var viewModel = NewViewModel([new StepId("source")]);
        var target = Assert.Single(viewModel.SendBackTargets);
        viewModel.RevisionFilePath = "/tmp/revision.txt";
        viewModel.SupplementaryWorker = "human";
        viewModel.SupplementaryOutputName = "revision";
        Assert.True(target.SendBackCommand.CanExecute(null));

        viewModel.IsEnabled = false;

        Assert.False(target.SendBackCommand.CanExecute(null));
    }

    [Fact]
    public async Task SendBackCommand_records_DecisionType_Supersede_against_its_own_TargetStepId()
    {
        DecisionType? recorded = null;
        StepId? recordedTarget = null;
        string? recordedRevisionFilePath = null;
        var viewModel = NewViewModel(
            [new StepId("source"), new StepId("draft")],
            (_, _, decisionType, targetStepId, revisionFilePath, _, _) =>
            {
                recorded = decisionType;
                recordedTarget = targetStepId;
                recordedRevisionFilePath = revisionFilePath;
                return Task.CompletedTask;
            });
        viewModel.RevisionFilePath = "/tmp/revision.txt";
        viewModel.SupplementaryWorker = "human";
        viewModel.SupplementaryOutputName = "revision";

        await viewModel.SendBackTargets[1].SendBackCommand.ExecuteAsync(null);

        Assert.Equal(DecisionType.Supersede, recorded);
        Assert.Equal(new StepId("draft"), recordedTarget);
        Assert.Equal("/tmp/revision.txt", recordedRevisionFilePath);
    }
}
