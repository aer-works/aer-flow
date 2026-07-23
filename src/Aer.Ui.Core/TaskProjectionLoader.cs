using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Aer.Flow.Templates;

namespace Aer.Ui.Core;

/// <summary>
/// One task/session directory's lightweight status (M24 Phase 5, #278's fleet list) — friendly
/// name, a template id or "interactive session" label, a plain status line, paused-step count,
/// archived state, and the creation/last-updated timestamps (#322) that let a client sort by
/// recency and render relative times ("2h ago"). Deliberately not a <see cref="TaskProjection"/>:
/// a fleet list showing every known task/session at once can't afford
/// <see cref="TaskProjectionLoader.LoadAsync"/>'s full per-execution history/artifact-lineage
/// projection cost for every item.
/// </summary>
/// <param name="Created">When this task/session was first created (UTC).</param>
/// <param name="Updated">When this task/session last changed (UTC) — the key the fleet list orders by.</param>
public sealed record TaskFleetItem(
    string TaskDirectoryPath,
    string FriendlyName,
    string TypeLabel,
    string StatusText,
    int PausedStepCount,
    bool IsArchived,
    DateTimeOffset Created,
    DateTimeOffset Updated);

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

    /// <summary>
    /// The fleet list's per-item load (M24 Phase 5, #278): skips <see cref="ExecutionHistoryProjector"/>
    /// and <see cref="ArtifactLineageProjector"/> entirely (the latter does real per-execution
    /// artifact-directory <see cref="File"/> I/O — the actual expensive part) and reads only
    /// <see cref="StateProjector.Project"/>'s status/paused-step count. The <see cref="FlowEventLogReader"/>
    /// read itself still happens — that's unavoidable for a correct status — this only skips the
    /// two additional, more expensive re-folds of the same event list.
    /// </summary>
    public static async Task<TaskFleetItem> LoadFleetStatusAsync(
        string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskDirectoryPath);

        var friendlyName = Path.GetFileName(Path.TrimEndingDirectorySeparator(taskDirectoryPath));
        var isArchived = TaskLifecycle.IsArchived(taskDirectoryPath);
        var isSession = File.Exists(Path.Combine(taskDirectoryPath, ".aer", "session.json"));
        var (created, updated) = await ResolveTimestampsAsync(taskDirectoryPath, isSession, cancellationToken).ConfigureAwait(false);

        var snapshotPath = Path.Combine(taskDirectoryPath, SnapshotFileName);
        if (!File.Exists(snapshotPath))
        {
            // A materialized interactive session with no initial message never actually runs (a
            // known quirk, not an error -- see DaemonIntegrationTests' WebSocketSnapshot_* remarks).
            // A DAG task directory with no snapshot yet shouldn't exist by construction, but is
            // represented the same defensive way rather than thrown on.
            return new TaskFleetItem(
                taskDirectoryPath, friendlyName, isSession ? "interactive session" : "workflow",
                isSession ? "Not yet run" : "Not yet run", PausedStepCount: 0, isArchived, created, updated);
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        var typeLabel = isSession ? "interactive session" : snapshot.WorkflowTemplateId.Value;

        var logPath = Path.Combine(taskDirectoryPath, LogFileName);
        var reader = new FlowEventLogReader(logPath);
        var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);

        var state = StateProjector.Project(events, snapshot);
        var pausedStepCount = state.Steps.Count(s => s.Status == StepStatus.Paused);

        return new TaskFleetItem(taskDirectoryPath, friendlyName, typeLabel, state.Status.ToString(), pausedStepCount, isArchived, created, updated);
    }

    /// <summary>
    /// The <c>created</c>/<c>updated</c> timestamps the fleet contract carries (#322), resolved from
    /// the best available source per entry type.
    /// <para>
    /// An interactive session's <c>.aer/session.json</c> carries serialized <see cref="SessionMetadata.CreatedAt"/>/
    /// <see cref="SessionMetadata.UpdatedAt"/> values -- a genuine durable in-data source (UpdatedAt
    /// is bumped on every turn by Aer.Daemon's turn executor), so it is preferred outright, and it is
    /// present even for a never-run session that has no snapshot yet.
    /// </para>
    /// <para>
    /// A DAG task carries no serialized timestamp anywhere: <see cref="WorkflowDefinitionSnapshot"/>
    /// has no time field and neither the <c>flow.jsonl</c> line envelope nor any <see cref="Aer.Flow.Domain.FlowEvent"/>
    /// records one. It therefore falls back to filesystem times of the task's own data files -- not
    /// the directory's, whose mtime any in-directory write (e.g. writing the <c>.aer/archived</c>
    /// marker) disturbs. <c>snapshot.json</c> is written once at creation and never mutated (spec
    /// §11.2), so its last-write time is a stable <c>created</c>; <c>flow.jsonl</c> is append-only, so
    /// its last-write time is the last-event-appended <c>updated</c>. Last-write time is used over
    /// creation time because birth time is unreliable on some Linux/CI filesystems whereas mtime
    /// always exists. The directory's own times are the last resort when neither file exists.
    /// </para>
    /// </summary>
    private static async Task<(DateTimeOffset Created, DateTimeOffset Updated)> ResolveTimestampsAsync(
        string taskDirectoryPath, bool isSession, CancellationToken cancellationToken)
    {
        if (isSession)
        {
            var sessionMetadataPath = Path.Combine(taskDirectoryPath, ".aer", "session.json");
            var metadata = await InteractiveSessionMaterializer.LoadMetadataAsync(sessionMetadataPath, cancellationToken).ConfigureAwait(false);
            if (metadata is not null)
            {
                return (metadata.CreatedAt, metadata.UpdatedAt);
            }
        }

        var snapshotPath = Path.Combine(taskDirectoryPath, SnapshotFileName);
        var logPath = Path.Combine(taskDirectoryPath, LogFileName);

        var created = File.Exists(snapshotPath)
            ? File.GetLastWriteTimeUtc(snapshotPath)
            : Directory.GetCreationTimeUtc(taskDirectoryPath);

        var updated = File.Exists(logPath)
            ? File.GetLastWriteTimeUtc(logPath)
            : File.Exists(snapshotPath)
                ? File.GetLastWriteTimeUtc(snapshotPath)
                : Directory.GetLastWriteTimeUtc(taskDirectoryPath);

        return (new DateTimeOffset(created), new DateTimeOffset(updated));
    }
}
