using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Aer.Flow.Templates;

namespace Aer.Ui.Core;

/// <summary>
/// One task/session directory's lightweight status (M24 Phase 5, #278's fleet list) — friendly
/// name, a template id or "interactive session" label, a plain status line, paused-step count, and
/// archived state. Deliberately not a <see cref="TaskProjection"/>: a fleet list showing every
/// known task/session at once can't afford <see cref="TaskProjectionLoader.LoadAsync"/>'s full
/// per-execution history/artifact-lineage projection cost for every item.
/// </summary>
public sealed record TaskFleetItem(
    string TaskDirectoryPath,
    string FriendlyName,
    string TypeLabel,
    string StatusText,
    int PausedStepCount,
    bool IsArchived);

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

        var snapshotPath = Path.Combine(taskDirectoryPath, SnapshotFileName);
        if (!File.Exists(snapshotPath))
        {
            // A materialized interactive session with no initial message never actually runs (a
            // known quirk, not an error -- see DaemonIntegrationTests' WebSocketSnapshot_* remarks).
            // A DAG task directory with no snapshot yet shouldn't exist by construction, but is
            // represented the same defensive way rather than thrown on.
            return new TaskFleetItem(
                taskDirectoryPath, friendlyName, isSession ? "interactive session" : "workflow",
                isSession ? "Not yet run" : "Not yet run", PausedStepCount: 0, isArchived);
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        var typeLabel = isSession ? "interactive session" : snapshot.WorkflowTemplateId.Value;

        var logPath = Path.Combine(taskDirectoryPath, LogFileName);
        var reader = new FlowEventLogReader(logPath);
        var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);

        var state = StateProjector.Project(events, snapshot);
        var pausedStepCount = state.Steps.Count(s => s.Status == StepStatus.Paused);

        return new TaskFleetItem(taskDirectoryPath, friendlyName, typeLabel, state.Status.ToString(), pausedStepCount, isArchived);
    }
}
