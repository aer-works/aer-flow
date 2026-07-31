using System.Text;
using System.Text.Json;
using Aer.Flow.Domain;
using Aer.Flow.Store;

namespace Aer.Flow.Tests.Store;

public class RoomEventLogReaderWriterTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _roomLogPath;

    public RoomEventLogReaderWriterTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "aer_room_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _roomLogPath = Path.Combine(_tempDirectory, "room.jsonl");
    }

    [Fact]
    public async Task RoundTrips_room_events_through_writer_and_reader()
    {
        var laneRef = new HeldWorkRef("lanes/lane-1");
        var citation = new LaneJournalCitation("lanes/lane-1", new ExecutionId("exec-1"), "executionSucceeded", 0);

        await using (var writer = new RoomEventLogWriter(_roomLogPath))
        {
            await writer.AppendAsync(new RoomEvent.HeldWorkDispatched(laneRef, "shape-1", TimeSpan.FromMinutes(10), "op-1"), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new RoomEvent.HeldWorkEscalated(laneRef, "op-supervisor"), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new RoomEvent.HeldWorkResolved(laneRef, citation), TestContext.Current.CancellationToken);
        }

        var reader = new RoomEventLogReader(_roomLogPath);
        var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, events.Count);
        Assert.IsType<RoomEvent.HeldWorkDispatched>(events[0]);
        Assert.IsType<RoomEvent.HeldWorkEscalated>(events[1]);
        Assert.IsType<RoomEvent.HeldWorkResolved>(events[2]);

        var resolved = (RoomEvent.HeldWorkResolved)events[2];
        Assert.Equal(laneRef, resolved.Ref);
        Assert.Equal(citation, resolved.Citation);
    }

    [Fact]
    public async Task Reading_a_malformed_complete_line_throws_FlowEventLogReadException()
    {
        await File.WriteAllTextAsync(_roomLogPath, "{\"owner\":\"room\",\"eventType\":\"heldWorkDispatched\"}\n", TestContext.Current.CancellationToken);

        var reader = new RoomEventLogReader(_roomLogPath);
        await Assert.ThrowsAsync<FlowEventLogReadException>(async () => await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AppendAsync_stamps_WriterUtcTimestamp_on_room_events()
    {
        var laneRef = new HeldWorkRef("lanes/lane-1");

        using var buffer = new MemoryStream();
        await using var writer = new RoomEventLogWriter(buffer, leaveOpen: true);

        var before = DateTime.UtcNow;
        await writer.AppendAsync(new RoomEvent.HeldWorkDispatched(laneRef, "shape-1", TimeSpan.FromMinutes(10), "op-1"), TestContext.Current.CancellationToken);
        var after = DateTime.UtcNow;

        var text = Encoding.UTF8.GetString(buffer.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var entry = Assert.IsType<LogEntry.RoomLogEntry>(JsonSerializer.Deserialize<LogEntry>(text[0], FlowEventLogJson.Options));

        Assert.NotNull(entry.WriterUtcTimestamp);
        Assert.True(entry.WriterUtcTimestamp >= before && entry.WriterUtcTimestamp <= after);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }
}
