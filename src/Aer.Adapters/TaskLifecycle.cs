namespace Aer.Adapters;

/// <summary>
/// M24 Phase 5 (#278): archive/unarchive as a directory-native marker file, the same idiom
/// <see cref="InteractiveSessionMaterializer"/> already uses for <c>.aer/workflow-path</c>/
/// <c>.aer/bindings-path</c> (plain file, existence/content-checked, never a schema field) — applies
/// uniformly to DAG tasks and interactive sessions without either type needing a metadata record.
/// Archiving never touches <c>workflow.json</c>, so the existing collision guard
/// (<see cref="TaskDirectoryAlreadyExistsException"/>) already blocks re-materializing an archived
/// name — only <see cref="Directory.Delete(string, bool)"/> actually frees a name.
/// </summary>
public static class TaskLifecycle
{
    private const string ArchivedMarkerFileName = "archived";

    private static string MarkerFilePath(string taskDirectoryPath) => Path.Combine(taskDirectoryPath, ".aer", ArchivedMarkerFileName);

    public static bool IsArchived(string taskDirectoryPath) => File.Exists(MarkerFilePath(taskDirectoryPath));

    public static async Task ArchiveAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        var markerPath = MarkerFilePath(taskDirectoryPath);
        var dir = Path.GetDirectoryName(markerPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(markerPath, DateTimeOffset.UtcNow.ToString("O"), cancellationToken).ConfigureAwait(false);
    }

    public static Task UnarchiveAsync(string taskDirectoryPath)
    {
        var markerPath = MarkerFilePath(taskDirectoryPath);
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
        }

        return Task.CompletedTask;
    }
}
