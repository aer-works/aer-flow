using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aer.Flow.Domain;
using Aer.Flow.Store;

namespace Aer.Flow.Tests.Store;

/// <summary>
/// Verifies serialization discipline for <c>room.jsonl</c> entries:
/// required parameter removal failure tests (the #784 pattern) extended to every <see cref="RoomEvent"/> variant.
/// </summary>
public class RoomEventLogJsonTests
{
    private static readonly HeldWorkRef LaneRef = new("lanes/lane-1");
    private const string CitedSubject = "exec-lane-1";

    public static TheoryData<RoomEvent> AllRoomEventVariants() =>
    [
        new RoomEvent.HeldWorkDispatched(LaneRef, "shape-flow", TimeSpan.FromMinutes(15), "operator-decider"),
        new RoomEvent.HeldWorkEscalated(LaneRef, "escalation-target"),
        new RoomEvent.HeldWorkResolved(LaneRef, new HeldWorkCitation(CitedSubject, "executionSucceeded", 0)),
    ];

    [Fact]
    public void Every_RoomEvent_variant_is_covered_by_these_tests()
    {
        var declared = typeof(RoomEvent)
            .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .Select(a => a.DerivedType)
            .ToHashSet();

        var covered = AllRoomEventVariants().Select(row => row.Data.GetType()).ToHashSet();

        Assert.Equal(declared.OrderBy(t => t.Name), covered.OrderBy(t => t.Name));
    }

    [Theory]
    [MemberData(nameof(AllRoomEventVariants))]
    public void A_room_line_that_lost_a_required_member_fails_replay_loudly(RoomEvent original)
    {
        var node = JsonNode.Parse(
            JsonSerializer.Serialize(original, typeof(RoomEvent), FlowEventLogJson.Options))!.AsObject();

        var members = node.Select(pair => pair.Key).Where(k => k != "eventType").ToList();
        Assert.NotEmpty(members);

        foreach (var member in members)
        {
            var damaged = JsonNode.Parse(node.ToJsonString())!.AsObject();
            Assert.True(damaged.Remove(member));

            var json = damaged.ToJsonString();
            var exception = Record.Exception(
                () => JsonSerializer.Deserialize<RoomEvent>(json, FlowEventLogJson.Options));

            if (exception is null)
            {
                var round = JsonSerializer.Deserialize<RoomEvent>(json, FlowEventLogJson.Options);
                Assert.NotNull(round);
                Assert.True(
                    IsOptional(original.GetType(), member),
                    $"{original.GetType().Name}.{member} deserialized while absent but is not an optional "
                    + "parameter — silent corruption path.");
            }
            else
            {
                Assert.IsType<JsonException>(exception);
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllRoomEventVariants))]
    public void An_intact_room_line_round_trips(RoomEvent original)
    {
        var json = JsonSerializer.Serialize(original, typeof(RoomEvent), FlowEventLogJson.Options);
        var deserialized = JsonSerializer.Deserialize<RoomEvent>(json, FlowEventLogJson.Options);

        Assert.Equal(
            json, JsonSerializer.Serialize(deserialized, typeof(RoomEvent), FlowEventLogJson.Options));
    }

    private static bool IsOptional(Type eventType, string memberName) =>
        eventType.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Any(p => string.Equals(p.Name, memberName, StringComparison.OrdinalIgnoreCase)
                && p.HasDefaultValue);
}
