using System.Text.Json;
using Aer.Flow.Domain;

namespace Aer.Flow.Store;

/// <summary>
/// Reads the combined <c>flow.jsonl</c> back into an ordered list of <see cref="FlowEvent"/>s
/// (spec §5.1), silently skipping <see cref="LogEntry.CoreLogEntry"/> lines the Core Dispatcher
/// wrote (M7 Phase 6) — the State Projector (§12) has no use for Core lifecycle events, only for
/// Flow's own classifications, so this keeps <see cref="IEventLogReader"/>'s contract unchanged
/// rather than pushing the filtering onto every caller. Pairs with <see cref="FlowEventLogWriter"/>,
/// which guarantees each entry is a single, complete, newline-terminated line (§5.3).
/// </summary>
public sealed class FlowEventLogReader(string logFilePath) : IEventLogReader
{
    public async Task<IReadOnlyList<FlowEvent>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(logFilePath))
        {
            return [];
        }

        string text;
        await using (var stream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        // Only lines terminated by '\n' are complete entries (§5.3); a dangling suffix with no
        // terminator is a write still in flight (or a crash mid-append) and is not yet observable.
        var lastNewline = text.LastIndexOf('\n');
        var completeText = lastNewline >= 0 ? text[..(lastNewline + 1)] : string.Empty;
        var lines = completeText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var events = new List<FlowEvent>(lines.Length);
        foreach (var line in lines)
        {
            LogEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<LogEntry>(line);
            }
            catch (JsonException ex)
            {
                throw new FlowEventLogReadException($"Malformed flow.jsonl line: {line}", ex);
            }

            switch (entry)
            {
                case LogEntry.FlowLogEntry flowLogEntry:
                    events.Add(flowLogEntry.Event);
                    break;
                case LogEntry.CoreLogEntry:
                    break;
                default:
                    throw new FlowEventLogReadException($"flow.jsonl line deserialized to null: {line}");
            }
        }

        return events;
    }
}
