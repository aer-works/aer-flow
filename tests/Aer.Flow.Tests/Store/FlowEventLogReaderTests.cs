using System.Text;
using System.Text.Json;
using Aer.Flow.Domain;
using Aer.Flow.Store;

namespace Aer.Flow.Tests.Store;

public class FlowEventLogReaderTests
{
    private static FlowEvent.ExecutionSucceeded MakeEvent(string id) => new(new ExecutionId(id));

    [Fact]
    public async Task ReadAllAsync_returns_an_empty_list_for_a_nonexistent_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        var reader = new FlowEventLogReader(path);

        var events = await reader.ReadAllAsync();

        Assert.Empty(events);
    }

    [Fact]
    public async Task ReadAllAsync_reads_back_appended_events_in_append_order()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var writer = new FlowEventLogWriter(path))
            {
                await writer.AppendAsync(MakeEvent("exec-1"));
                await writer.AppendAsync(MakeEvent("exec-2"));
                await writer.AppendAsync(MakeEvent("exec-3"));
            }

            var events = await new FlowEventLogReader(path).ReadAllAsync();

            var ids = events.Cast<FlowEvent.ExecutionSucceeded>().Select(e => e.ExecutionId.Value);
            Assert.Equal(new[] { "exec-1", "exec-2", "exec-3" }, ids);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAllAsync_excludes_a_trailing_line_with_no_newline_terminator()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var completeLine = JsonSerializer.Serialize(new LogEntry.FlowLogEntry(MakeEvent("exec-1")), typeof(LogEntry));
            var tornLine = JsonSerializer.Serialize(new LogEntry.FlowLogEntry(MakeEvent("exec-2")), typeof(LogEntry))[..5];
            await File.WriteAllTextAsync(path, $"{completeLine}\n{tornLine}", Encoding.UTF8);

            var events = await new FlowEventLogReader(path).ReadAllAsync();

            var succeeded = Assert.Single(events);
            Assert.Equal("exec-1", Assert.IsType<FlowEvent.ExecutionSucceeded>(succeeded).ExecutionId.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAllAsync_throws_a_FlowEventLogReadException_for_a_complete_but_malformed_line()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            await File.WriteAllTextAsync(path, "{ not valid json }\n", Encoding.UTF8);

            await Assert.ThrowsAsync<FlowEventLogReadException>(() => new FlowEventLogReader(path).ReadAllAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAllAsync_skips_core_owned_lines_and_returns_only_flow_events()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var writer = new FlowEventLogWriter(path))
            {
                await writer.AppendAsync(MakeEvent("exec-1"));
                await writer.AppendAsync(new CoreEvent.ExecutionStarted(new ExecutionId("exec-1"), Pid: 42));
                await writer.AppendAsync(
                    new CoreEvent.ExecutionExited(new ExecutionId("exec-1"), ExitCode: 0, CoreExitReason.Natural));
                await writer.AppendAsync(MakeEvent("exec-2"));
            }

            var events = await new FlowEventLogReader(path).ReadAllAsync();

            var ids = events.Cast<FlowEvent.ExecutionSucceeded>().Select(e => e.ExecutionId.Value);
            Assert.Equal(new[] { "exec-1", "exec-2" }, ids);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
