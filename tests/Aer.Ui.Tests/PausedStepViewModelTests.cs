using Aer.Flow.Domain;

namespace Aer.Ui.Tests;

/// <summary>
/// M15 Phase 2 (issue #138): the ViewModel layer in isolation, no Avalonia headless session needed —
/// <see cref="MainWindowDecisionTests"/> covers the same surface end to end through the real
/// <see cref="MainWindow"/>. These tests are the plain unit-test level: the §7 label→<see cref="DecisionType"/>
/// mapping, and the shared mutation-in-flight flag disabling every paused step's actions at once.
/// </summary>
public class PausedStepViewModelTests
{
    [Fact]
    public async Task ApproveCommand_records_DecisionType_Resume()
    {
        DecisionType? recorded = null;
        var viewModel = new PausedStepViewModel(
            new StepId("a"), new ExecutionId("exec-1"),
            (_, _, decisionType) =>
            {
                recorded = decisionType;
                return Task.CompletedTask;
            });

        await viewModel.ApproveCommand.ExecuteAsync(null);

        Assert.Equal(DecisionType.Resume, recorded);
    }

    [Fact]
    public async Task RejectCommand_records_DecisionType_Reject()
    {
        DecisionType? recorded = null;
        var viewModel = new PausedStepViewModel(
            new StepId("a"), new ExecutionId("exec-1"),
            (_, _, decisionType) =>
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
            new StepId("critic"), new ExecutionId("exec-critic-1"),
            (stepId, executionId, _) =>
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
        var viewModel = new PausedStepViewModel(new StepId("critic"), new ExecutionId("exec-1"), (_, _, _) => Task.CompletedTask);

        Assert.Equal("critic (exec-1)", viewModel.Label);
    }

    [Fact]
    public void Commands_are_disabled_once_IsEnabled_is_false()
    {
        var viewModel = new PausedStepViewModel(new StepId("a"), new ExecutionId("exec-1"), (_, _, _) => Task.CompletedTask)
        {
            IsEnabled = false,
        };

        Assert.False(viewModel.ApproveCommand.CanExecute(null));
        Assert.False(viewModel.RejectCommand.CanExecute(null));
    }

    [Fact]
    public void MainWindowViewModel_disables_every_paused_step_while_a_mutation_is_in_flight()
    {
        var owner = new MainWindowViewModel();
        var stepA = new PausedStepViewModel(new StepId("a"), new ExecutionId("exec-a"), (_, _, _) => Task.CompletedTask);
        var stepB = new PausedStepViewModel(new StepId("b"), new ExecutionId("exec-b"), (_, _, _) => Task.CompletedTask);
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
}
