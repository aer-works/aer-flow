using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aer.Workers.Dialogue;

/// <summary>
/// Custom JSON converter for <see cref="FinalOutputMode"/> (#736), mirroring
/// <c>Aer.Flow.Domain.BackoffPolicyJsonConverter</c>'s shape: the default
/// <see cref="JsonSerializerOptions"/> this worker's config parses under (no
/// <see cref="JsonStringEnumConverter"/> registered globally, per <see cref="DialogueWorkerConfigParser"/>'s
/// own doc comment) would otherwise reject a string value outright, and an unrecognized enum name
/// would surface a generic framework message instead of one naming the bad value and the valid set.
/// Applied directly to <see cref="DialogueWorkerConfig.FinalOutputMode"/> via a property-level
/// <see cref="JsonConverterAttribute"/> rather than a global option, so it takes effect without
/// touching any other type's serialization.
/// </summary>
public sealed class FinalOutputModeJsonConverter : JsonConverter<FinalOutputMode>
{
    public override FinalOutputMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Dialogue-worker config's 'FinalOutputMode' must be a string, got {reader.TokenType}.");
        }

        var value = reader.GetString();
        return value switch
        {
            nameof(FinalOutputMode.FinalTurn) => FinalOutputMode.FinalTurn,
            nameof(FinalOutputMode.Transcript) => FinalOutputMode.Transcript,
            _ => throw new JsonException(
                $"Dialogue-worker config's 'FinalOutputMode' has unknown value '{value}'. " +
                $"Valid values: {nameof(FinalOutputMode.FinalTurn)}, {nameof(FinalOutputMode.Transcript)}."),
        };
    }

    public override void Write(Utf8JsonWriter writer, FinalOutputMode value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}
