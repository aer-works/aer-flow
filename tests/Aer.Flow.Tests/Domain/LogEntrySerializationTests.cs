using System.Text.Json;
using Aer.Flow.Domain;

namespace Aer.Flow.Tests.Domain;

public class LogEntrySerializationTests
{
    private static readonly ExecutionId ExecutionId = new("exec-1");

    public static IEnumerable<object[]> AllEntryVariants()
    {
        yield return [new LogEntry.FlowLogEntry(new FlowEvent.ExecutionSucceeded(ExecutionId))];
        yield return [new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(ExecutionId, Pid: 99))];
    }

    [Theory]
    [MemberData(nameof(AllEntryVariants))]
    public void RoundTrips_through_the_LogEntry_base_type_without_data_loss(LogEntry original)
    {
        var json = JsonSerializer.Serialize(original, typeof(LogEntry));

        var deserialized = JsonSerializer.Deserialize<LogEntry>(json);
        Assert.NotNull(deserialized);

        var reserialized = JsonSerializer.Serialize(deserialized, typeof(LogEntry));
        Assert.Equal(json, reserialized);
        Assert.Equal(original.GetType(), deserialized.GetType());
    }

    [Fact]
    public void FlowLogEntry_and_CoreLogEntry_serialize_with_distinct_owner_discriminators()
    {
        var flowJson = JsonSerializer.Serialize(
            new LogEntry.FlowLogEntry(new FlowEvent.ExecutionSucceeded(ExecutionId)), typeof(LogEntry));
        var coreJson = JsonSerializer.Serialize(
            new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(ExecutionId, Pid: 1)), typeof(LogEntry));

        Assert.Contains("\"owner\":\"flow\"", flowJson);
        Assert.Contains("\"owner\":\"core\"", coreJson);
    }

    [Fact]
    public void Deserializing_an_unknown_owner_discriminator_throws()
    {
        const string json = """{"owner":"somethingElse"}""";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LogEntry>(json));
    }
}
