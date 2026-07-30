using System.Text.Json;
using Aer.Flow.Domain;
using Aer.Flow.Store;

namespace Aer.Flow.Tests.Domain;

public class RoomEventSerializationTests
{
    private static readonly HeldWorkRef LaneRef = new("lanes/lane-1");
    private static readonly ExecutionId ExecId = new("exec-lane-1");

    public static TheoryData<RoomEvent> AllRoomEventVariants() =>
    [
        new RoomEvent.HeldWorkDispatched(LaneRef, "shape-flow", TimeSpan.FromMinutes(10), "operator-alice"),
        new RoomEvent.HeldWorkEscalated(LaneRef, "operator-bob"),
        new RoomEvent.HeldWorkResolved(LaneRef, new LaneJournalCitation("lanes/lane-1", ExecId, "executionSucceeded", 1)),
    ];

    [Theory]
    [MemberData(nameof(AllRoomEventVariants))]
    public void RoundTrips_through_RoomEvent_base_type_without_data_loss(RoomEvent original)
    {
        var json = JsonSerializer.Serialize(original, typeof(RoomEvent), FlowEventLogJson.Options);
        var deserialized = JsonSerializer.Deserialize<RoomEvent>(json, FlowEventLogJson.Options);

        Assert.NotNull(deserialized);
        var reserialized = JsonSerializer.Serialize(deserialized, typeof(RoomEvent), FlowEventLogJson.Options);

        Assert.Equal(json, reserialized);
        Assert.Equal(original.GetType(), deserialized.GetType());
    }

    [Fact]
    public void Deserializing_unknown_eventType_discriminator_throws()
    {
        const string json = """{"eventType":"unknownRoomEvent"}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RoomEvent>(json, FlowEventLogJson.Options));
    }
}
