using System.Text.Json;
using System.Text.Json.Serialization;
using Aer.Flow.Store;

namespace Aer.Flow.Projection;

/// <summary>
/// Persistence store for engine usage counters under <c>{room}/.aer/turn-usage.json</c> (#778).
/// </summary>
public static class RoomTurnUsageStore
{
    private const string AerDirectoryName = ".aer";
    private const string UsageFileName = "turn-usage.json";

    public static string GetUsageFilePath(string roomDirectoryPath)
        => Path.Combine(roomDirectoryPath, AerDirectoryName, UsageFileName);

    private sealed record UsageDto(
        [property: JsonPropertyName("recentMachineTurnTimestamps")] List<DateTimeOffset>? RecentMachineTurnTimestamps,
        [property: JsonPropertyName("lastMachineTurnAt")] DateTimeOffset? LastMachineTurnAt,
        [property: JsonPropertyName("consecutiveFailedTurns")] int? ConsecutiveFailedTurns);

    private static readonly JsonSerializerOptions ReadOptions = new(FlowEventLogJson.Options)
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Reads usage fresh from <c>{room}/.aer/turn-usage.json</c>.
    /// Missing file → returns <see cref="RoomTurnUsage.Empty"/> silently.
    /// Present-but-invalid (corrupt JSON or negative consecutive failures) → LOUD stderr message + returns <see cref="RoomTurnUsage.Empty"/>.
    /// </summary>
    public static RoomTurnUsage Load(string roomDirectoryPath)
    {
        if (string.IsNullOrEmpty(roomDirectoryPath))
        {
            return RoomTurnUsage.Empty;
        }

        var filePath = GetUsageFilePath(roomDirectoryPath);
        if (!File.Exists(filePath))
        {
            return RoomTurnUsage.Empty;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var dto = JsonSerializer.Deserialize<UsageDto>(json, ReadOptions);
            if (dto is null)
            {
                Console.Error.WriteLine($"[RoomTurnUsage] Loud fallback to empty usage: Usage file '{filePath}' deserialized to null.");
                return RoomTurnUsage.Empty;
            }

            var consecutiveFailed = dto.ConsecutiveFailedTurns ?? 0;
            if (consecutiveFailed < 0)
            {
                Console.Error.WriteLine($"[RoomTurnUsage] Loud fallback to empty usage: Usage file '{filePath}' has negative consecutive failed turns ({consecutiveFailed}).");
                return RoomTurnUsage.Empty;
            }

            var timestamps = dto.RecentMachineTurnTimestamps ?? [];
            return new RoomTurnUsage(
                timestamps.AsReadOnly(),
                dto.LastMachineTurnAt,
                consecutiveFailed);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RoomTurnUsage] Loud fallback to empty usage: Failed to load usage from '{filePath}': {ex.Message}");
            return RoomTurnUsage.Empty;
        }
    }

    /// <summary>
    /// Persists <paramref name="usage"/> atomically to <c>{room}/.aer/turn-usage.json</c>.
    /// </summary>
    public static void Save(string roomDirectoryPath, RoomTurnUsage usage)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(usage);

        var aerDir = Path.Combine(roomDirectoryPath, AerDirectoryName);
        Directory.CreateDirectory(aerDir);

        var filePath = GetUsageFilePath(roomDirectoryPath);
        var dto = new UsageDto(
            usage.RecentMachineTurnTimestamps.ToList(),
            usage.LastMachineTurnAt,
            usage.ConsecutiveFailedTurns);

        var json = JsonSerializer.Serialize(dto, FlowEventLogJson.Options);
        var tempFilePath = filePath + ".tmp." + Guid.NewGuid().ToString("n");
        File.WriteAllText(tempFilePath, json);
        File.Move(tempFilePath, filePath, overwrite: true);
    }
}
