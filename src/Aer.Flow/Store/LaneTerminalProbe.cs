using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Flow.Templates;

namespace Aer.Flow.Store;

/// <summary>
/// The read-only, at-READ-time probe of a held-work lane directory feeding
/// <see cref="RoomWakeDerivation"/> (#799). Mirrors <c>Aer.Cli.StatusCommand</c>'s own
/// snapshot.json + flow.jsonl read exactly — same files, same
/// <see cref="StateProjector.Project(System.Collections.Generic.IReadOnlyList{FlowEvent},WorkflowDefinitionSnapshot)"/>
/// terminal authority (post-#811 <c>DeriveWorkflowStatus</c>) — never a second reading of what
/// "terminal" means. Takes no <see cref="Aer.Flow.Concurrency.ConcurrencyGuard"/>: this can run
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
            // The #774 pattern itself: dispatch recorded, lane never started (or its journal has
            // not appeared yet for some other reason) — indistinguishable from the journal alone,
            // which is exactly why this is its own named wake kind rather than folded into "not
            // terminal".
            return new LaneProbeResult(JournalExists: false, IsTerminal: false);
        }

        var snapshotPath = Path.Combine(laneDirectoryPath, SnapshotFileName);
        if (!File.Exists(snapshotPath))
        {
            // flow.jsonl written before snapshot.json is not a state this probe can observe from
            // RunCommand's own bind-then-run ordering, but treating it as "journal exists, not yet
            // terminal" rather than throwing keeps a probe mid-write from ever crashing the bridge.
            return new LaneProbeResult(JournalExists: true, IsTerminal: false);
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        var reader = new FlowEventLogReader(logPath);
        var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var state = StateProjector.Project(events, snapshot);

        return new LaneProbeResult(JournalExists: true, IsTerminal: state.Status == WorkflowStatus.Terminal);
    }
}
