using System.Text;
using System.Text.Json;
using Aer.Flow.Domain;

namespace Aer.Flow.Store;

/// <summary>
/// Appends <see cref="RoomEvent"/> lines to <c>room.jsonl</c> (spec §5.1 / #798) with single-writer
/// discipline and fsync crash durability.
/// </summary>
public sealed class RoomEventLogWriter : IRoomEventLogWriter, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RoomEventLogWriter(string logFilePath)
        : this(OpenAppendStream(logFilePath))
    {
    }

    public RoomEventLogWriter(Stream stream, bool leaveOpen = false)
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

    public Task AppendAsync(RoomEvent roomEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roomEvent);
        return AppendEntryAsync(new LogEntry.RoomLogEntry(roomEvent, DateTime.UtcNow), cancellationToken);
    }

    private async Task AppendEntryAsync(LogEntry entry, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(entry, typeof(LogEntry), FlowEventLogJson.Options);
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
