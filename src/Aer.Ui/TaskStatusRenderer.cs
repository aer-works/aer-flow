namespace Aer.Ui;

/// <summary>
/// Renders a <see cref="TaskProjection"/>'s per-step statuses as plain text. Deliberately minimal
/// per issue #118's scope ("any styling worth defending" is excluded from this phase) — the point
/// is that every displayed line traces directly to a projected <see cref="Aer.Flow.Domain.StepState"/>
/// (UI spec §12's transparency rule), not the presentation. Execution detail, the DAG, artifacts,
/// and snapshot-vs-template diffing are later phases (#119-#121).
/// </summary>
public static class TaskStatusRenderer
{
    public static void Render(TextWriter output, TaskProjection projection)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(projection);

        output.WriteLine($"Workflow status: {projection.State.Status}");
        foreach (var step in projection.State.Steps)
        {
            output.WriteLine($"  {step.StepId}: {step.Status}");
        }
    }
}
