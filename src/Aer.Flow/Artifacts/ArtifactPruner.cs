using Aer.Flow.Domain;
using Aer.Flow.Store;

namespace Aer.Flow.Artifacts;

/// <summary>
/// Implements artifact pruning for completed runs (ADR 0009 Scope 3, #973).
/// <para>
/// <b>Pruning is NOT deletion:</b> Moves completed run artifact directories from active path
/// (<c>{artifacts}/execution_{id}</c>) to recoverable location (<c>{artifacts}/pruned/execution_{id}</c>).
/// </para>
/// <para>
/// <b>Scope:</b> Completed runs only (<see cref="WorkflowStatus.Terminal"/>). Live or paused runs are untouched.
/// <b>Keep exempts:</b> A run marked keep (<see cref="KeepMarker.IsKept"/>) is never pruned.
/// <b>Crash-safe &amp; idempotent:</b> Uses <see cref="RetryingFileMove.MoveDirectory"/>. Pruning twice is a no-op.
/// <b>Derivable:</b> Provenance is derivable from the Event Store alone — no side-table.
/// </para>
/// </summary>
public static class ArtifactPruner
{
    /// <summary>
    /// Prunes artifacts for the run/task at <paramref name="taskDirectoryPath"/> if it is terminal and not marked keep.
    /// Returns <c>true</c> if any artifact directory was pruned (moved), or <c>false</c> otherwise.
    /// </summary>
    public static async Task<bool> PruneAsync(
        string taskDirectoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskDirectoryPath);

        var artifactsRootPath = Path.Combine(taskDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
        return await PruneTaskArtifactsAsync(taskDirectoryPath, artifactsRootPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Prunes active execution artifact directories under <paramref name="artifactsRootPath"/> for the task at
    /// <paramref name="taskDirectoryPath"/> if the run is terminal and not marked keep.
    /// </summary>
    public static async Task<bool> PruneTaskArtifactsAsync(
        string taskDirectoryPath,
        string artifactsRootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        if (!Directory.Exists(artifactsRootPath))
        {
            return false;
        }

        if (KeepMarker.IsKept(taskDirectoryPath))
        {
            return false;
        }

        var probeResult = await LaneTerminalProbe.ProbeAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(false);
        if (!probeResult.IsTerminal)
        {
            return false;
        }

        var executionDirs = Directory.GetDirectories(artifactsRootPath, "execution_*", SearchOption.TopDirectoryOnly);
        if (executionDirs.Length == 0)
        {
            return false;
        }

        var prunedAny = false;
        foreach (var execDir in executionDirs)
        {
            var dirName = Path.GetFileName(execDir);
            var targetDir = Path.Combine(artifactsRootPath, ArtifactManager.PrunedDirectoryName, dirName);

            PruneDirectory(execDir, targetDir);
            prunedAny = true;
        }

        return prunedAny;
    }

    /// <summary>
    /// Atomically moves an active execution directory <paramref name="sourceDir"/> to <paramref name="targetDir"/>.
    /// Idempotent: if source does not exist and target exists, treats as already pruned.
    /// </summary>
    public static void PruneDirectory(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            // Already pruned or missing - no-op
            return;
        }

        if (Directory.Exists(targetDir))
        {
            // Target already exists (e.g. partial previous attempt) - clean target before move
            Directory.Delete(targetDir, true);
        }

        RetryingFileMove.MoveDirectory(sourceDir, targetDir);
    }
}
