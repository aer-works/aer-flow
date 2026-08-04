using System.Text;
using System.Text.Json;
using Aer.Flow.Domain;

namespace Aer.Flow.Store;

/// <summary>
/// Reads the combined <c>flow.jsonl</c> back into ordered event lists (spec §5.1):
/// <see cref="ReadAllAsync"/> for Flow's own half, which the State Projector (§12) consumes,
/// <see cref="ReadAllCoreEventsAsync"/> for the Core Dispatcher's half (M7 Phase 6), which M10
/// Phase 3's crash reconciliation reads back for §6's causal link, <see cref="ReadSnapshotAsync"/>
/// for a caller needing both from a single read pass, and <see cref="ReadAllEntriesWithTimestampsAsync"/>
/// for callers that need entries with their writer-stamped timestamps (#745) — used by status
/// reporting to display per-step times. Pairs with <see cref="FlowEventLogWriter"/>, which guarantees
/// each entry is a single, complete, newline-terminated line (§5.3).
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
        return await ReadSnapshotFromOffsetAsync(0, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EventLogSnapshot> ReadSnapshotFromOffsetAsync(long seekByteOffset, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(logFilePath))
        {
            return new EventLogSnapshot([], [], 0);
        }

        if (seekByteOffset <= 0)
        {
            return await ReadFullSnapshotInternalAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var stream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var fileLength = stream.Length;

            if (seekByteOffset > fileLength)
            {
                Console.Error.WriteLine(
                    $"[ProjectionCheckpoint] Fallback to full replay LOUDLY: Checkpoint ByteOffset ({seekByteOffset}) exceeds log length ({fileLength}).");
                return await ReadFullSnapshotInternalAsync(cancellationToken).ConfigureAwait(false);
            }

            if (seekByteOffset == fileLength)
            {
                return new EventLogSnapshot([], [], fileLength);
            }

            // Boundary validation: check that byte at seekByteOffset - 1 is '\n'
            stream.Seek(seekByteOffset - 1, SeekOrigin.Begin);
            int prevByte = stream.ReadByte();
            if (prevByte != '\n')
            {
                Console.Error.WriteLine(
                    $"[ProjectionCheckpoint] Fallback to full replay LOUDLY: Checkpoint ByteOffset ({seekByteOffset}) does not land on a record boundary (previous byte 0x{prevByte:X2} != '\\n').");
                return await ReadFullSnapshotInternalAsync(cancellationToken).ConfigureAwait(false);
            }

            // Seek to tail start
            stream.Seek(seekByteOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            var lastNewline = text.LastIndexOf('\n');
            var completeText = lastNewline >= 0 ? text[..(lastNewline + 1)] : string.Empty;
            var completeByteCount = Encoding.UTF8.GetByteCount(completeText);
            var lines = completeText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var flowEvents = new List<FlowEvent>(lines.Length);
            var coreEvents = new List<CoreEvent>(lines.Length);

            foreach (var line in lines)
            {
                LogEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<LogEntry>(line, FlowEventLogJson.Options);
                }
                catch (JsonException ex)
                {
                    Console.Error.WriteLine(
                        $"[ProjectionCheckpoint] Fallback to full replay LOUDLY: Mid-line corruption or unparseable line at seek target: {ex.Message}");
                    return await ReadFullSnapshotInternalAsync(cancellationToken).ConfigureAwait(false);
                }

                if (entry is null)
                {
                    Console.Error.WriteLine(
                        "[ProjectionCheckpoint] Fallback to full replay LOUDLY: Line at seek target deserialized to null.");
                    return await ReadFullSnapshotInternalAsync(cancellationToken).ConfigureAwait(false);
                }

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

            return new EventLogSnapshot(flowEvents, coreEvents, seekByteOffset + completeByteCount);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[ProjectionCheckpoint] Fallback to full replay LOUDLY: Exception during seek-to-tail read: {ex.Message}");
            return await ReadFullSnapshotInternalAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads all log entries, including their writer-stamped timestamps. Used by status reporting
    /// to display per-step times derived from event log timestamps.
    /// </summary>
    public async Task<IReadOnlyList<LogEntry>> ReadAllEntriesWithTimestampsAsync(CancellationToken cancellationToken = default)
    {
        return await ReadAllEntriesAsync(cancellationToken).ConfigureAwait(false);
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

        var lastNewline = text.LastIndexOf('\n');
        var completeText = lastNewline >= 0 ? text[..(lastNewline + 1)] : string.Empty;
        var lines = completeText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var result = new List<LogEntry>(lines.Length);
        foreach (var line in lines)
        {
            LogEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<LogEntry>(line, FlowEventLogJson.Options);
            }
            catch (JsonException ex)
            {
                throw new FlowEventLogReadException($"Malformed line in the ledger: {line}", ex);
            }

            if (entry is null)
            {
                throw new FlowEventLogReadException($"Line in the ledger deserialized to null: {line}");
            }

            result.Add(entry);
        }

        return result;
    }

    private async Task<EventLogSnapshot> ReadFullSnapshotInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(logFilePath))
        {
            return new EventLogSnapshot([], [], 0, IsFallbackToFull: true);
        }

        string text;
        await using (var stream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        {
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var lastNewline = text.LastIndexOf('\n');
        var completeText = lastNewline >= 0 ? text[..(lastNewline + 1)] : string.Empty;
        var completeByteCount = Encoding.UTF8.GetByteCount(completeText);
        var lines = completeText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var flowEvents = new List<FlowEvent>(lines.Length);
        var coreEvents = new List<CoreEvent>(lines.Length);

        foreach (var line in lines)
        {
            LogEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<LogEntry>(line, FlowEventLogJson.Options);
            }
            catch (JsonException ex)
            {
                throw new FlowEventLogReadException($"Malformed line in the ledger: {line}", ex);
            }

            if (entry is null)
            {
                throw new FlowEventLogReadException($"Line in the ledger deserialized to null: {line}");
            }

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

        return new EventLogSnapshot(flowEvents, coreEvents, completeByteCount, IsFallbackToFull: true);
    }
}
