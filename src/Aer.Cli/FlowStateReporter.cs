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
                var pausePoint = pausePointByStepId[step.StepId]!;
                var supersedeTargets = pausePoint.SupersedeTargets;
                var supersedeText = supersedeTargets.Count == 0
                    ? "none"
                    : string.Join(", ", supersedeTargets.Select(target => target.Value));

                // #334: report which human act the pause demands, not just "Paused" — a terminal user
                // triaging pauses needs the same needs-input/ready-for-review distinction the clients show.
                var pausedLabel = pausePoint.Kind switch
                {
                    PausePointKind.NeedsInput => "Paused — awaiting input",
                    _ => "Paused — awaiting review",
                };

                output.WriteLine(
                    $"  {step.StepId}: {pausedLabel} (execution={step.LatestExecutionId}, outcome={step.PausedOutcome}, " +
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
