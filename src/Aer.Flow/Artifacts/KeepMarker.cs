namespace Aer.Flow.Artifacts;

/// <summary>
/// M24 / ADR 0009: Keep/durable marker file for runs/tasks, following the
/// <c>TaskLifecycle</c> archived idiom (<c>.aer/keep</c>).
/// A run marked with keep is exempt from artifact pruning (§973).
/// </summary>
public static class KeepMarker
{
    public const string KeepFileName = "keep";

    public static string MarkerFilePath(string taskDirectoryPath) =>
        Path.Combine(taskDirectoryPath, ".aer", KeepFileName);

    public static bool IsKept(string taskDirectoryPath) =>
        File.Exists(MarkerFilePath(taskDirectoryPath));

    public static async Task MarkKeepAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        var markerPath = MarkerFilePath(taskDirectoryPath);
        var dir = Path.GetDirectoryName(markerPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(markerPath, DateTimeOffset.UtcNow.ToString("O"), cancellationToken).ConfigureAwait(false);
    }

    public static Task ClearKeepAsync(string taskDirectoryPath)
    {
        var markerPath = MarkerFilePath(taskDirectoryPath);
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
        }

        return Task.CompletedTask;
    }
}
