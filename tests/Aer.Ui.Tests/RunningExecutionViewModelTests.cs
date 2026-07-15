using Aer.Flow.Domain;

namespace Aer.Ui.Tests;

/// <summary>
/// M15 Phase 4 (issue #140): the ViewModel layer in isolation, no Avalonia headless session needed —
/// mirrors <see cref="PausedStepViewModelTests"/>'s split for the decision surface. Real dispatch,
/// end-to-end targeted-cancel and host-stop behavior is <see cref="MainWindowCancelAndStopTests"/>'s.
/// </summary>
public class RunningExecutionViewModelTests
{
    private static RunningExecutionViewModel NewViewModel(
        StepId? stepId = null, bool isLocallyHosted = false, bool cancellationRequested = false, CancelDelegate? cancel = null) =>
        new(
            stepId ?? new StepId("a"), new ExecutionId("exec-1"), isLocallyHosted, cancellationRequested,
            cancel ?? (_ => Task.CompletedTask));

    [Fact]
    public async Task CancelCommand_invokes_cancel_with_this_executions_ExecutionId()
    {
        ExecutionId? recorded = null;
        var viewModel = NewViewModel(cancel: executionId =>
        {
            recorded = executionId;
            return Task.CompletedTask;
        });

        await viewModel.CancelCommand.ExecuteAsync(null);

        Assert.Equal(new ExecutionId("exec-1"), recorded);
    }

    [Fact]
    public void Label_names_the_step_and_its_execution()
    {
        var viewModel = NewViewModel(new StepId("b"));

        Assert.Equal("b (exec-1)", viewModel.Label);
    }

    [Fact]
    public void Label_names_a_step_less_execution_as_supplementary()
    {
        var viewModel = new RunningExecutionViewModel(
            stepId: null, new ExecutionId("exec-1"), isLocallyHosted: false, cancellationRequested: false, _ => Task.CompletedTask);

        Assert.Equal("(supplementary) — exec-1", viewModel.Label);
    }

    [Fact]
    public void CancelCommand_is_disabled_once_CancellationRequested_is_true()
    {
        var viewModel = NewViewModel();
        Assert.True(viewModel.CancelCommand.CanExecute(null));

        viewModel.CancellationRequested = true;

        Assert.False(viewModel.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void CancelCommand_is_disabled_once_IsEnabled_is_false()
    {
        var viewModel = NewViewModel();

        viewModel.IsEnabled = false;

        Assert.False(viewModel.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void UpdateEnabled_keeps_a_locally_hosted_execution_enabled_while_a_mutation_is_in_flight()
    {
        var viewModel = NewViewModel(isLocallyHosted: true);

        viewModel.UpdateEnabled(isMutationInFlight: true);

        Assert.True(viewModel.IsEnabled);
    }

    [Fact]
    public void UpdateEnabled_disables_a_not_locally_hosted_execution_while_a_mutation_is_in_flight()
    {
        var viewModel = NewViewModel(isLocallyHosted: false);

        viewModel.UpdateEnabled(isMutationInFlight: true);
        Assert.False(viewModel.IsEnabled);

        viewModel.UpdateEnabled(isMutationInFlight: false);
        Assert.True(viewModel.IsEnabled);
    }

    [Fact]
    public void MainWindowViewModel_updates_every_running_executions_enabled_state_when_IsMutationInFlight_changes()
    {
        var owner = new MainWindowViewModel();
        var locallyHosted = NewViewModel(isLocallyHosted: true);
        var external = NewViewModel(isLocallyHosted: false);
        owner.RunningExecutions.Add(locallyHosted);
        owner.RunningExecutions.Add(external);

        owner.IsMutationInFlight = true;

        Assert.True(locallyHosted.IsEnabled);
        Assert.False(external.IsEnabled);
        Assert.True(locallyHosted.CancelCommand.CanExecute(null));
        Assert.False(external.CancelCommand.CanExecute(null));

        owner.IsMutationInFlight = false;

        Assert.True(locallyHosted.IsEnabled);
        Assert.True(external.IsEnabled);
    }
}
