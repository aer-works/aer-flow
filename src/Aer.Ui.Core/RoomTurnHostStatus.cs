using System.Text.Json.Serialization;

namespace Aer.Ui.Core;

public sealed record RoomTurnHostThrottleValues(
    [property: JsonPropertyName("machineTurnMinimumGapSeconds")] double MachineTurnMinimumGapSeconds,
    [property: JsonPropertyName("machineTurnsPerHour")] int MachineTurnsPerHour,
    [property: JsonPropertyName("consecutiveFailureLimit")] int ConsecutiveFailureLimit);

public sealed record RoomTurnHostStatus(
    [property: JsonPropertyName("roomDirectoryPath")] string RoomDirectoryPath,
    [property: JsonPropertyName("throttles")] RoomTurnHostThrottleValues Throttles,
    [property: JsonPropertyName("throttlesSource")] string ThrottlesSource,
    [property: JsonPropertyName("loadError")] string? LoadError,
    [property: JsonPropertyName("machineTurnsInTrailingHour")] string MachineTurnsInTrailingHour,
    [property: JsonPropertyName("turnsInTrailingHourCount")] int TurnsInTrailingHourCount,
    [property: JsonPropertyName("machineTurnsPerHourCap")] int MachineTurnsPerHourCap,
    [property: JsonPropertyName("consecutiveFailures")] int ConsecutiveFailures,
    [property: JsonPropertyName("inFlight")] bool InFlight,
    [property: JsonPropertyName("isDormant")] bool IsDormant,
    [property: JsonPropertyName("dormancyEscalationDetail")] string? DormancyEscalationDetail,
    [property: JsonPropertyName("lastDecisionReason")] string? LastDecisionReason);
