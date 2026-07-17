using Aer.Flow.Projection;
using Aer.Flow.Store;
using Aer.Flow.Templates;

namespace Aer.Ui.Core;

/// <summary>
/// The seam this phase exists to prove (issue #118): opens a real task directory using exactly
/// the read-model library calls Flow's own write path uses — <see cref="SnapshotBinder.LoadFromFileAsync"/>
/// for the bound snapshot (AER Flow spec §11.2), <see cref="FlowEventLogReader"/> for the Flow
/// Event Store (§5.1), and <see cref="StateProjector.Project"/> to reconstruct <see cref="Aer.Flow.Domain.FlowState"/>
/// (§12) — never a reimplementation of any of it. <see cref="ExecutionHistoryProjector.Project"/>
/// (M14 Phase 2, issue #119) reads the same event list a second time for the fuller per-execution
/// history <see cref="Aer.Flow.Domain.FlowState"/> alone doesn't carry, and
/// <see cref="ArtifactLineageProjector.Project"/> (M14 Phase 4, issue #121) reads it a third time,
/// plus the artifacts directory, for per-execution artifact provenance. A UI built this way inherits
/// §11's determinism guarantee by construction, per UI spec §11.
/// </summary>
public static class TaskProjectionLoader
{
    private const string SnapshotFileName = "snapshot.json";
    private const string LogFileName = "flow.jsonl";
    private const string ArtifactsDirectoryName = "artifacts";

    /// <exception cref="InvalidTaskDirectoryException">
    /// <paramref name="taskDirectoryPath"/> has no persisted snapshot — UI spec §3.1's
    /// self-describing-directory contract confirmed by contents, not assumed from a path.
    /// </exception>
    /// <exception cref="SnapshotLoadException">The persisted snapshot is malformed.</exception>
    /// <exception cref="FlowEventLogReadException">The persisted Flow Event Store is malformed.</exception>
    public static async Task<TaskProjection> LoadAsync(
        string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskDirectoryPath);

        var snapshotPath = Path.Combine(taskDirectoryPath, SnapshotFileName);
        if (!File.Exists(snapshotPath))
        {
            throw new InvalidTaskDirectoryException(
                $"Not a task directory (no '{SnapshotFileName}' found): '{taskDirectoryPath}'");
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);

        var logPath = Path.Combine(taskDirectoryPath, LogFileName);
        var reader = new FlowEventLogReader(logPath);
        var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);

        var state = StateProjector.Project(events, snapshot);
        var history = ExecutionHistoryProjector.Project(events, snapshot);

        var artifactsRootPath = Path.Combine(taskDirectoryPath, ArtifactsDirectoryName);
        var lineage = ArtifactLineageProjector.Project(events, snapshot, artifactsRootPath);

        return new TaskProjection(snapshot, state, history, lineage);
    }
}
