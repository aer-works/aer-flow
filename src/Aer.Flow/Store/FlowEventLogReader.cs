using System.Text.Json;
using Aer.Flow.Domain;

namespace Aer.Flow.Store;

/// <summary>
/// Reads the combined <c>flow.jsonl</c> back into ordered event lists (spec §5.1):
/// <see cref="ReadAllAsync"/> for Flow's own half, which the State Projector (§12) consumes,
/// <see cref="ReadAllCoreEventsAsync"/> for the Core Dispatcher's half (M7 Phase 6), which M10
/// Phase 3's crash reconciliation reads back for §6's causal link, and <see cref="ReadSnapshotAsync"/>
/// for a caller needing both from a single read pass. Pairs with <see cref="FlowEventLogWriter"/>,
/// which guarantees each entry is a single, complete, newline-terminated line (§5.3).
/// </summary>
public sealed class FlowEventLogReader(string logFilePath) : IEventLogReader
{
    public async Task<IReadOnlyList<FlowEvent>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        var entries = await ReadAllEntriesAsync(cancellationToken).ConfigureAwait(false);

        var events = new List<FlowEvent>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is LogEntry.FlowLogEntry flowLogEntry)
            {
                events.Add(flowLogEntry.Event);
            }
        }

        return events;
    }

    public async Task<IReadOnlyList<CoreEvent>> ReadAllCoreEventsAsync(CancellationToken cancellationToken = default)
    {
        var entries = await ReadAllEntriesAsync(cancellationToken).ConfigureAwait(false);

        var events = new List<CoreEvent>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is LogEntry.CoreLogEntry coreLogEntry)
            {
                events.Add(coreLogEntry.Event);
            }
        }

        return events;
    }

    public async Task<EventLogSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var entries = await ReadAllEntriesAsync(cancellationToken).ConfigureAwait(false);

        var flowEvents = new List<FlowEvent>(entries.Count);
        var coreEvents = new List<CoreEvent>(entries.Count);
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case LogEntry.FlowLogEntry flowLogEntry:
                    flowEvents.Add(flowLogEntry.Event);
                    break;
                case LogEntry.CoreLogEntry coreLogEntry:
                    coreEvents.Add(coreLogEntry.Event);
                    break;
            }
        }

        return new EventLogSnapshot(flowEvents, coreEvents);
    }

    private async Task<IReadOnlyList<LogEntry>> ReadAllEntriesAsync(CancellationToken cancellationToken)
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

        var entries = new List<LogEntry>(lines.Length);
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

            if (entry is null)
            {
                throw new FlowEventLogReadException($"flow.jsonl line deserialized to null: {line}");
            }

            entries.Add(entry);
        }

        return entries;
    }
}
