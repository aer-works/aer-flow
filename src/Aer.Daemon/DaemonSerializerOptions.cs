using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aer.Daemon;

internal static class DaemonSerializerOptions
{
    public static JsonSerializerOptions Rest { get; } = Configure(new(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions WebSocket { get; } = Configure(new());

    /// <summary>
    /// The one definition of the daemon's wire serialization settings. The live REST pipeline
    /// (Program.cs) and the fixture generator both call this — if they configured themselves
    /// independently, the fixtures could stay green while the wire drifted.
    /// </summary>
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        return options;
    }
}
