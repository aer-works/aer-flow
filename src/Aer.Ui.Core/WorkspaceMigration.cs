namespace Aer.Ui.Core;

/// <summary>Outcome of a <see cref="WorkspaceMigration.Run"/> call.</summary>
public enum WorkspaceMigrationOutcome
{
    /// <summary>No legacy folder was present — nothing to do.</summary>
    NoLegacyFolder,

    /// <summary>The legacy folder was moved to the current one.</summary>
    Migrated,

    /// <summary>Both folders exist. Nothing was moved or merged; the caller must surface
    /// <see cref="WorkspaceMigrationResult.Message"/> to the user.</summary>
    BothPresentNoAction,

    /// <summary>The move was attempted and failed (a locked file, a permission, a cross-volume
    /// redirect). Nothing was moved; the caller must surface
    /// <see cref="WorkspaceMigrationResult.Message"/> and carry on (#863).</summary>
    CouldNotMove,
}

public readonly record struct WorkspaceMigrationResult(WorkspaceMigrationOutcome Outcome, string? Message);

/// <summary>
/// Moves a real, pre-existing user folder (<c>Documents/AER Flow</c>) to its renamed home
/// (<c>Documents/Baton</c>) on startup (#823). Never merges and never overwrites: if both paths
/// already exist, this is a no-op and the caller is handed a message to show, because silently
/// choosing which of two real folders wins is the failure mode this exists to avoid.
/// </summary>
public static class WorkspaceMigration
{
    public static WorkspaceMigrationResult Run() => Run(DefaultWorkspace.LegacyRootPath, DefaultWorkspace.RootPath);

    public static WorkspaceMigrationResult Run(string legacyPath, string currentPath)
    {
        if (!Directory.Exists(legacyPath))
        {
            return new WorkspaceMigrationResult(WorkspaceMigrationOutcome.NoLegacyFolder, null);
        }

        if (Directory.Exists(currentPath))
        {
            return new WorkspaceMigrationResult(
                WorkspaceMigrationOutcome.BothPresentNoAction,
                $"Both \"{legacyPath}\" and \"{currentPath}\" exist. Nothing was moved or merged automatically — " +
                "move anything you still need out of the old folder yourself, then remove it.");
        }

        try
        {
            Directory.Move(legacyPath, currentPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // #863: this runs during startup, before any window exists. A file left open by an
            // editor or a sync client, a read-only child, or a Known Folder redirect that puts the
            // two paths on different volumes all make Move throw -- and letting that propagate
            // turns "your old folder could not be moved" into "the app does not start", for
            // exactly the people who have data worth migrating. Best-effort, never load-bearing.
            return new WorkspaceMigrationResult(
                WorkspaceMigrationOutcome.CouldNotMove,
                $"\"{legacyPath}\" could not be moved to \"{currentPath}\": {ex.Message} " +
                "Nothing was moved. Your files are untouched where they are — move them yourself, " +
                "or close whatever is using them and restart.");
        }

        return new WorkspaceMigrationResult(WorkspaceMigrationOutcome.Migrated, null);
    }
}
