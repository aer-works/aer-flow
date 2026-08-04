using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aer.Daemon;

/// <summary>
/// Throttles and breaker policy configuration for room turns (#992).
/// Reads `{room}/throttles.json` fresh on every call (operator-visible, hand-editable).
/// </summary>
public sealed record RoomTurnThrottles(
    TimeSpan MachineTurnMinimumGap,
    int MachineTurnsPerHour,
    int ConsecutiveFailureLimit)
{
    public static RoomTurnThrottles Defaults { get; } = new(
        TimeSpan.FromSeconds(60),
        10,
        3);

    private sealed record ThrottleDto(
        [property: JsonPropertyName("machineTurnMinimumGapSeconds")] double? MachineTurnMinimumGapSeconds,
        [property: JsonPropertyName("machineTurnMinimumGap")] double? MachineTurnMinimumGap,
        [property: JsonPropertyName("machineTurnsPerHour")] int? MachineTurnsPerHour,
        [property: JsonPropertyName("consecutiveFailureLimit")] int? ConsecutiveFailureLimit);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static (RoomTurnThrottles Values, string? LoadError) Load(string roomDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(roomDirectoryPath))
        {
            return (Defaults, null);
        }

        var filePath = Path.Combine(roomDirectoryPath, "throttles.json");
        if (!File.Exists(filePath))
        {
            return (Defaults, null);
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var dto = JsonSerializer.Deserialize<ThrottleDto>(json, JsonOptions);
            if (dto is null)
            {
                return (Defaults, $"Malformed throttles file at '{filePath}': deserialized to null.");
            }

            var gapSeconds = dto.MachineTurnMinimumGapSeconds ?? dto.MachineTurnMinimumGap ?? Defaults.MachineTurnMinimumGap.TotalSeconds;
            var turnsPerHour = dto.MachineTurnsPerHour ?? Defaults.MachineTurnsPerHour;
            var failureLimit = dto.ConsecutiveFailureLimit ?? Defaults.ConsecutiveFailureLimit;

            if (gapSeconds < 0 || turnsPerHour <= 0 || failureLimit <= 0)
            {
                return (Defaults, $"Malformed throttles file at '{filePath}': values must be positive.");
            }

            return (new RoomTurnThrottles(TimeSpan.FromSeconds(gapSeconds), turnsPerHour, failureLimit), null);
        }
        catch (Exception ex)
        {
            return (Defaults, $"Malformed throttles file at '{filePath}': {ex.Message}");
        }
    }
}
