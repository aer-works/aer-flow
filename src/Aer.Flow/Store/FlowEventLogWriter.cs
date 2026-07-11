using System.Text;
using System.Text.Json;
using Aer.Flow.Domain;

namespace Aer.Flow.Store;

/// <summary>
/// Appends <see cref="LogEntry"/> lines to the combined <c>flow.jsonl</c> (spec §5.1) with the
/// crash-durability guarantees required by §5.3 and §7:
/// <list type="bullet">
/// <item>Each entry is serialized to one newline-terminated line and written in a single call,
/// so a reader tailing the file can only ever observe a complete line or nothing yet (§5.3) —
/// never a torn one.</item>
/// <item>Every write is fsync'd (or the equivalent durable flush) before either
/// <c>AppendAsync</c> overload returns, so a caller cannot proceed to the next write-sequence
/// step — e.g. dispatching an <see cref="ExecutionRequest"/> to Core — before the preceding
/// intent is durable (§7).</item>
/// </list>
/// Implements both <see cref="IEventLogWriter"/> (Flow's own events) and
/// <see cref="ICoreEventLogWriter"/> (Core-originated lifecycle events, M7 Phase 6) over one
/// shared, gated stream — the single physical file the dual-log-ownership decision requires,
/// with ownership enforced by <see cref="LogEntry"/>'s type split rather than by which writer
/// interface a caller happens to hold.
/// </summary>
public sealed class FlowEventLogWriter : IEventLogWriter, ICoreEventLogWriter, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FlowEventLogWriter(string logFilePath)
        : this(OpenAppendStream(logFilePath))
    {
    }

    /// <summary>Writes to an already-open stream instead of opening a file. Exposed for testing.</summary>
    public FlowEventLogWriter(Stream stream, bool leaveOpen = false)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    private static FileStream OpenAppendStream(string logFilePath)
    {
        var directory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new FileStream(
            logFilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 1,
            useAsync: true);
    }

    public Task AppendAsync(FlowEvent flowEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flowEvent);
        return AppendEntryAsync(new LogEntry.FlowLogEntry(flowEvent), cancellationToken);
    }

    public Task AppendAsync(CoreEvent coreEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coreEvent);
        return AppendEntryAsync(new LogEntry.CoreLogEntry(coreEvent), cancellationToken);
    }

    private async Task AppendEntryAsync(LogEntry entry, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(entry, typeof(LogEntry));
        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (_stream is FileStream fileStream)
            {
                fileStream.Flush(flushToDisk: true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        if (!_leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
