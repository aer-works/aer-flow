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
/// turn from the same daemon process is a genuine, expected race (#533). Measured under
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
/// <para>
/// <b>Why the write is skipped when the content already matches (#667):</b> that same determinism
/// means a rewrite on every resolve buys nothing and costs a race. The reader that pays for it is
/// not another writer but the vendor CLI, which opens <c>--settings</c> once at spawn with no retry
/// of its own; a claude that cannot load its settings file loads no <c>PreToolUse</c> hook, which
/// <c>gate.broken-hook-fails-open</c> measures as an allow. Measured before the skip: under four
/// concurrent resolvers, 4239 of 424091 unretried reads of <c>claude-settings.json</c> failed with a
/// sharing violation, while reads of the never-rewritten <c>claude-mcp.json</c> in the same directory
/// failed none.
/// </para>
/// <para>
/// <b>This bounds the window rather than closing it.</b> The first resolve against a fresh or drifted
/// file still writes, and a spawn inside that window can still lose its read; the retry above is what
/// covers it, and is why it stays. What the skip removes is every resolve after the first — the whole
/// population in the daemon case, where the file is canonical long before two sessions start a turn
/// together.
/// </para>
/// </remarks>
internal static class AtomicLaunchConfigWriter
{
    private const int MaxAttempts = 5;

    public static void Write(string path, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(content);

        if (AlreadyHolds(path, content))
        {
            return;
        }

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

    /// <summary>
    /// True when <paramref name="path"/> already holds exactly <paramref name="content"/>, so there
    /// is nothing to write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Existence-only would be wrong here: #543 reversed #533's "never overwrite existing content"
    /// precisely because a stale file -- a pre-#543 <c>{}</c>, or content a worker tampered with
    /// through its own <c>--add-dir</c> grant -- would otherwise stay installed forever with the gate
    /// silently disabled. Comparing content keeps that reversal intact while removing the writes that
    /// change nothing.
    /// </para>
    /// <para>
    /// <b>An unreadable file counts as differing.</b> This probe can take the very sharing violation
    /// it exists to spare the vendor CLI, and the safe response to "I could not tell" is to write:
    /// the cost of a redundant write is contention, and the cost of a skipped one is a worker running
    /// with no gate. It must never throw -- a failed probe is not a failed write.
    /// </para>
    /// </remarks>
    private static bool AlreadyHolds(string path, string content)
    {
        try
        {
            return File.Exists(path)
                && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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
