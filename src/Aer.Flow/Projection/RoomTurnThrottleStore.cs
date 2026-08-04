using System.Text.Json;
using System.Text.Json.Serialization;
using Aer.Flow.Store;

namespace Aer.Flow.Projection;

/// <summary>
/// Persistence store for operator-visible turn throttle settings at <c>{room}/turn-throttles.json</c> (#778).
/// <para>
/// <b>Filename choice rationale:</b> Placed at room root as <c>turn-throttles.json</c> alongside
/// <c>room.jsonl</c> and <c>memory/</c>. NOT under <c>.aer/</c> because it is an operator-visible,
/// hand-editable file owned by the operator, whereas <c>.aer/</c> is reserved for engine-managed metadata.
/// </para>
/// </summary>
public static class RoomTurnThrottleStore
{
    public const string ThrottleFileName = "turn-throttles.json";

    public static string GetThrottleFilePath(string roomDirectoryPath)
        => Path.Combine(roomDirectoryPath, ThrottleFileName);

    private sealed record ThrottleDto(
        [property: JsonPropertyName("minMachineTurnIntervalSeconds")] double? MinMachineTurnIntervalSeconds,
        [property: JsonPropertyName("maxMachineTurnsPerHour")] int? MaxMachineTurnsPerHour,
        [property: JsonPropertyName("failedTurnsBeforeDormancy")] int? FailedTurnsBeforeDormancy);

    private static readonly JsonSerializerOptions ReadOptions = new(FlowEventLogJson.Options)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new(FlowEventLogJson.Options)
    {
        WriteIndented = true
    };

    /// <summary>
    /// Reads settings fresh on each call from <c>{room}/turn-throttles.json</c>.
    /// Missing file → returns defaults silently.
    /// Present-but-invalid (corrupt JSON or non-positive numeric values) → LOUD stderr message + returns defaults.
    /// </summary>
    public static RoomTurnThrottles Load(string roomDirectoryPath)
    {
        if (string.IsNullOrEmpty(roomDirectoryPath))
        {
            return RoomTurnThrottles.Default;
        }

        var filePath = GetThrottleFilePath(roomDirectoryPath);
        if (!File.Exists(filePath))
        {
            return RoomTurnThrottles.Default;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var dto = JsonSerializer.Deserialize<ThrottleDto>(json, ReadOptions);
            if (dto is null)
            {
                Console.Error.WriteLine($"[RoomTurnThrottles] Loud fallback to defaults: Throttle file '{filePath}' deserialized to null.");
                return RoomTurnThrottles.Default;
            }

            var intervalSeconds = dto.MinMachineTurnIntervalSeconds ?? RoomTurnThrottles.DefaultMinMachineTurnInterval.TotalSeconds;
            var maxPerHour = dto.MaxMachineTurnsPerHour ?? RoomTurnThrottles.DefaultMaxMachineTurnsPerHour;
            var failedDormancy = dto.FailedTurnsBeforeDormancy ?? RoomTurnThrottles.DefaultFailedTurnsBeforeDormancy;

            if (intervalSeconds <= 0 || maxPerHour <= 0 || failedDormancy <= 0)
            {
                Console.Error.WriteLine(
                    $"[RoomTurnThrottles] Loud fallback to defaults: Throttle file '{filePath}' contains non-positive values (minIntervalSec: {intervalSeconds}, maxPerHour: {maxPerHour}, failedBeforeDormancy: {failedDormancy}).");
                return RoomTurnThrottles.Default;
            }

            return new RoomTurnThrottles(
                TimeSpan.FromSeconds(intervalSeconds),
                maxPerHour,
                failedDormancy);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RoomTurnThrottles] Loud fallback to defaults: Failed to load throttles from '{filePath}': {ex.Message}");
            return RoomTurnThrottles.Default;
        }
    }

    /// <summary>
    /// Atomically persists <paramref name="throttles"/> to <c>{room}/turn-throttles.json</c>.
    /// </summary>
    public static void Save(string roomDirectoryPath, RoomTurnThrottles throttles)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(throttles);

        var filePath = GetThrottleFilePath(roomDirectoryPath);
        var dto = new ThrottleDto(
            throttles.MinMachineTurnInterval.TotalSeconds,
            throttles.MaxMachineTurnsPerHour,
            throttles.FailedTurnsBeforeDormancy);

        var json = JsonSerializer.Serialize(dto, WriteOptions);
        var tempFilePath = filePath + ".tmp." + Guid.NewGuid().ToString("n");
        File.WriteAllText(tempFilePath, json);
        File.Move(tempFilePath, filePath, overwrite: true);
    }
}
