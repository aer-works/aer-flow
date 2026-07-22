namespace Aer.Workers.Dialogue.Tests.TestSupport;

/// <summary>
/// Wraps <see cref="Directory.Delete(string, bool)"/> with a short retry/backoff loop for the
/// transient-lock race documented in issue #295: on Windows, Defender (or the search indexer)
/// intermittently holds a brief exclusive handle on a just-written file while scanning it, which
/// surfaces as <see cref="IOException"/> ("being used by another process") or
/// <see cref="UnauthorizedAccessException"/> when a recursive delete runs immediately after a test
/// writes its fixture files. A handful of short retries clears the transient case; a persistent
/// failure still surfaces for real on the final attempt. A directory that's already gone is treated
/// as success rather than retried — <see cref="DirectoryNotFoundException"/> derives from
/// <see cref="IOException"/> but is never the transient-lock race, so retrying it would just waste
/// the whole backoff budget on a foregone conclusion.
/// </summary>
internal static class DirectoryCleanup
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    public static void DeleteRecursively(string path)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }
}
