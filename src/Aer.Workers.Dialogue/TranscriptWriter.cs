using System.Text;
using System.Text.Json;

namespace Aer.Workers.Dialogue;

/// <summary>
/// Appends <see cref="TranscriptTurn"/> lines to <c>transcript.jsonl</c> (M17 Phase 2, #165), one
/// newline-terminated JSON object per turn, written and flushed in a single call so a reader tailing
/// the file only ever observes a complete line or nothing yet — the same shape
/// <c>Aer.Flow.Store.FlowEventLogWriter</c> guarantees for <c>flow.jsonl</c>. Unlike that writer,
/// this file is never read back to resume anything (Flow spec §18.2's crash tradeoff: a crash
/// restarts the whole exchange from turn one), so no fsync-to-disk durability guarantee is made
/// here — a flush to the OS is enough for this file's only real readers, a human inspecting the run
/// and, eventually, M18's conversation view.
/// </summary>
public sealed class TranscriptWriter : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    public TranscriptWriter(string transcriptFilePath)
        : this(OpenAppendStream(transcriptFilePath))
    {
    }

    /// <summary>Writes to an already-open stream instead of opening a file. Exposed for testing.</summary>
    public TranscriptWriter(Stream stream, bool leaveOpen = false)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    private static FileStream OpenAppendStream(string transcriptFilePath)
    {
        var directory = Path.GetDirectoryName(transcriptFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new FileStream(transcriptFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    public async Task AppendAsync(TranscriptTurn turn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);

        var line = JsonSerializer.Serialize(turn);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
