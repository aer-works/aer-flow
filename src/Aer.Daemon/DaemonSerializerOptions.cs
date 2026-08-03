using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aer.Daemon;

internal static class DaemonSerializerOptions
{
    public static JsonSerializerOptions Rest { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonSerializerOptions WebSocket { get; } = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
