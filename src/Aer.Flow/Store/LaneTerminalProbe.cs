using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Flow.Templates;

namespace Aer.Flow.Store;

/// <summary>
/// The read-only, at-READ-time probe of a held-work lane directory feeding
/// <see cref="RoomWakeDerivation"/> (#799). Mirrors <c>Aer.Cli.StatusCommand</c>'s own
/// snapshot.json + flow.jsonl read exactly — same files, same
/// <see cref="StateProjector.Project(System.Collections.Generic.IReadOnlyList{FlowEvent},WorkflowDefinitionSnapshot,ProjectionCheckpoint)"/>
/// terminal authority (post-#811 <c>DeriveWorkflowStatus</c>) — never a second reading of what
/// "terminal" means. Uses projection checkpoints (#903 Scope 1) when present for bounded O(tail) replay.
/// Takes no <see cref="Aer.Flow.Concurrency.ConcurrencyGuard"/>: this can run
/// concurrently with the lane's own live pump, the same read-only discipline <c>aer status</c>
/// already established.
/// </summary>
public static class LaneTerminalProbe
{
    private const string SnapshotFileName = "snapshot.json";
    private const string LogFileName = "flow.jsonl";

    public static async Task<LaneProbeResult> ProbeAsync(
        string laneDirectoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(laneDirectoryPath);

        var logPath = Path.Combine(laneDirectoryPath, LogFileName);
        if (!File.Exists(logPath))
        {
            return new LaneProbeResult(JournalExists: false, IsTerminal: false);
        }

        var snapshotPath = Path.Combine(laneDirectoryPath, SnapshotFileName);
        if (!File.Exists(snapshotPath))
        {
            return new LaneProbeResult(JournalExists: true, IsTerminal: false);
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        var reader = new FlowEventLogReader(logPath);
        var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var checkpoint = ProjectionCheckpointStore.Load(laneDirectoryPath);
        var state = StateProjector.Project(events, snapshot, checkpoint);

        return new LaneProbeResult(JournalExists: true, IsTerminal: state.Status == WorkflowStatus.Terminal);
    }
}
