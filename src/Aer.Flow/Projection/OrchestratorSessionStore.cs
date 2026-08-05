using System.Text.Json;
using Aer.Flow.Store;

namespace Aer.Flow.Projection;

/// <summary>
/// Persistence store for engine session metadata under <c>{room}/.aer/orchestrator-session.json</c> (§A).
/// Follows the same loud-fallback posture as <see cref="ProjectionCheckpointStore"/>:
/// missing or corrupt file → loud stderr message + cold start from zero.
/// </summary>
public static class OrchestratorSessionStore
{
    private const string AerDirectoryName = ".aer";
    private const string CursorFileName = "orchestrator-session.json";

    public static string GetCursorFilePath(string roomDirectoryPath)
        => Path.Combine(roomDirectoryPath, AerDirectoryName, CursorFileName);

    /// <summary>
    /// Loads the <see cref="OrchestratorSessionCursor"/> from <paramref name="roomDirectoryPath"/> if present and valid.
    /// If missing, corrupt, or invalid, logs LOUDLY to <see cref="Console.Error"/> and returns <c>null</c> to trigger a cold start.
    /// </summary>
    public static OrchestratorSessionCursor? Load(string roomDirectoryPath)
    {
        if (string.IsNullOrEmpty(roomDirectoryPath))
        {
            return null;
        }

        var filePath = GetCursorFilePath(roomDirectoryPath);
        if (!File.Exists(filePath))
        {
            // Silent by design, matching ProjectionCheckpointStore: a missing cursor is the
            // NORMAL state of every room that has never hosted a turn, not a fault. Only a file
            // that exists and cannot be honored is loud.
            return null;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var cursor = JsonSerializer.Deserialize<OrchestratorSessionCursor>(json, FlowEventLogJson.Options);
            if (cursor is null || cursor.ProcessedEventCount < 0)
            {
                Console.Error.WriteLine($"[OrchestratorSession] Cold start LOUDLY: Cursor file '{filePath}' deserialized to null or negative count.");
                return null;
            }

            return cursor;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[OrchestratorSession] Cold start LOUDLY: Failed to load cursor from '{filePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Persists <paramref name="cursor"/> atomically to <c>.aer/orchestrator-session.json</c> within <paramref name="roomDirectoryPath"/>.
    /// </summary>
    public static void Save(string roomDirectoryPath, OrchestratorSessionCursor cursor)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(cursor);

        var aerDir = Path.Combine(roomDirectoryPath, AerDirectoryName);
        Directory.CreateDirectory(aerDir);

        var filePath = GetCursorFilePath(roomDirectoryPath);
        var json = JsonSerializer.Serialize(cursor, FlowEventLogJson.Options);

        var tempFilePath = filePath + ".tmp." + Guid.NewGuid().ToString("n");
        File.WriteAllText(tempFilePath, json);
        RetryingFileMove.Move(tempFilePath, filePath, overwrite: true);
    }

}
