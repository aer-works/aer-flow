namespace Aer.Adapters;

/// <summary>
/// Writes an AER-owned worker launch-configuration file (claude's <c>claude-settings.json</c>,
/// agy's <c>.agents/hooks.json</c>) so that a worker starting mid-write never reads a torn file.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="ClaudeWorkerAdapter"/> by #554, when
/// <see cref="GeminiWorkerAdapter"/> needed the same guarantee. It was first written as a local
/// copy whose doc comment claimed to "mirror" the claude implementation and did not: it derived its
/// temp name from <see cref="Environment.ProcessId"/> — constant for the process, so two concurrent
/// writers in one process collide on the same temp path — and carried no retry. Caught by an
/// independent reviewer. Shared rather than duplicated so the claim is structural instead of
/// aspirational; a second copy of a concurrency-sensitive writer is exactly the shape that drifts.
/// </para>
/// <para>
/// <b>Why the temp-plus-rename at all:</b> <see cref="File.Move(string, string, bool)"/>'s overwrite
/// is a same-volume rename and therefore atomic on both Windows and POSIX, which a direct
/// <see cref="File.WriteAllText(string, string)"/> onto the final path does not guarantee when two
/// callers race to rewrite it. On agy the stakes are specific: an unparseable
/// <c>hooks.json</c> is not an error but a **silently ungated worker**
/// (<c>agy.hook-malformed-stdout-fails-open</c> measured that a hook producing nothing is read as an
/// allow), so a torn read is a permission failure rather than a cosmetic one.
/// </para>
/// <para>
/// <b>Why the retry:</b> the rename itself can still collide. Two chat sessions starting their first
/// turn from the same daemon process is a genuine, expected race (#533), and both adapters rewrite
/// their file on <i>every</i> resolve rather than once per fresh <c>~/.aer</c>. Measured under
/// #543's own parallel test run: a concurrent <see cref="File.Move(string, string, bool)"/> onto the
/// same destination throws <see cref="UnauthorizedAccessException"/> on Windows — a transient
/// sharing violation, not a real permissions problem — while another thread's move or read briefly
/// holds the destination open.
/// </para>
/// <para>
/// Retrying is correct here rather than papering over a disagreement: every racing writer in one
/// process produces byte-identical content (a deterministic function of
/// <see cref="AppContext.BaseDirectory"/>, constant for the process's lifetime), so whichever
/// attempt wins, the file ends up holding the one content every writer wanted anyway.
/// </para>
/// </remarks>
internal static class AtomicLaunchConfigWriter
{
    private const int MaxAttempts = 5;

    public static void Write(string path, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(content);

        for (var attempt = 1; ; attempt++)
        {
            // Unique per attempt, never per process: a process-keyed name makes two concurrent
            // writers in one process race for the same temp file, which is the defect this
            // extraction fixed.
            var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, content);
            try
            {
                File.Move(tempPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: TryDeleteTemp can never throw, so it can never replace or mask the
                // exception this catch is handling -- a leftover .tmp file is a far smaller problem
                // than losing the reason a retry was needed.
                TryDeleteTemp(tempPath);
                if (attempt >= MaxAttempts)
                {
                    throw;
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(10 * attempt));
            }
            catch
            {
                TryDeleteTemp(tempPath);
                throw;
            }
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch
        {
            // Best-effort cleanup only -- see this type's own remarks for why a failed delete here
            // must never surface in place of the real exception.
        }
    }
}
