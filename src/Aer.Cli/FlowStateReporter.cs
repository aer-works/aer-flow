using Aer.Flow.Domain;

namespace Aer.Cli;

/// <summary>
/// The pause-aware reporting M12 Phase 3 requires: without a paused step's <see cref="ExecutionId"/>
/// and declared <c>PausePoint.SupersedeTargets</c> printed somewhere, a terminal user has no way to
/// know what to pass to <c>aer decide --execution</c>/<c>--target-step</c>. Shared by every command
/// <see cref="Program"/> dispatches to, so <c>aer run</c> and <c>aer decide</c> report paused state
/// identically.
/// </summary>
public static class FlowStateReporter
{
    public static void Report(TextWriter output, CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(result);

        var pausePointByStepId = result.Snapshot.Steps.ToDictionary(step => step.StepId, step => step.PausePoint);

        output.WriteLine($"Workflow status: {result.State.Status}");
        foreach (var step in result.State.Steps)
        {
            if (step.Status == StepStatus.Paused)
            {
                var supersedeTargets = pausePointByStepId[step.StepId]!.SupersedeTargets;
                var supersedeText = supersedeTargets.Count == 0
                    ? "none"
                    : string.Join(", ", supersedeTargets.Select(target => target.Value));

                output.WriteLine(
                    $"  {step.StepId}: Paused (execution={step.LatestExecutionId}, outcome={step.PausedOutcome}, " +
                    $"supersede-targets: {supersedeText})");
            }
            else
            {
                output.WriteLine($"  {step.StepId}: {step.Status}");
            }
        }

        foreach (var stepLess in result.State.StepLessExecutions)
        {
            output.WriteLine($"  (supplementary) {stepLess.Worker}: execution={stepLess.ExecutionId} pending");
        }
    }
}
