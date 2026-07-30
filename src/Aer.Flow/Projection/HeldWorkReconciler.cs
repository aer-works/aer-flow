using Aer.Flow.Domain;

namespace Aer.Flow.Projection;

/// <summary>
/// Reconciles a <see cref="HeldWorkState"/> at READ time against a lane-directory probe (#774 pattern).
/// Does not mutate or alter the pure <see cref="RoomState"/> projection.
/// </summary>
public static class HeldWorkReconciler
{
    /// <summary>
    /// Renders the reconciled status of <paramref name="state"/>, producing the loud named state
    /// <c>dispatch recorded; lane never started (&lt;probe why&gt;)</c> when a dispatched lane has no journal.
    /// </summary>
    public static string RenderStatus(
        HeldWorkState state,
        Func<string, bool>? laneJournalExistsProbe = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        laneJournalExistsProbe ??= path => File.Exists(Path.Combine(path, "flow.jsonl"));

        if (state.Status != HeldWorkStatus.Resolved && !laneJournalExistsProbe(state.Ref.LaneDirectoryPath))
        {
            return $"dispatch recorded; lane never started (no journal found at {state.Ref.LaneDirectoryPath})";
        }

        return state.Status switch
        {
            HeldWorkStatus.Dispatched => "dispatched",
            HeldWorkStatus.Escalated => $"escalated to {state.EscalatedTo}",
            HeldWorkStatus.Resolved => $"resolved ({state.Citation?.EventType} execution {state.Citation?.ExecutionId})",
            _ => state.Status.ToString(),
        };
    }
}
