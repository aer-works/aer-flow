namespace Aer.Ui.Tests.TestSupport;

/// <summary>
/// Reads a file that another live process is still writing to, which is what makes it different from
/// <c>File.ReadAllText</c> and why it exists at all.
/// <para>
/// These files are appended to by a still-live writer while a test polls them, so a default-share
/// read races the writer's handle on Windows (share violation, #839, caught on PR #838's CI). Both
/// halves below are needed and that is measured, not cautious: tolerant share flags alone are NOT
/// enough, because Windows PowerShell 5.1's <c>Add-Content</c> takes a momentary open that does not
/// share <c>Read</c> at all -- the flags-only version of this reader failed the same race in its own
/// branch's gates run. So a transient open failure is also retried until
/// <see cref="ShareRetryBudget"/> runs out, and then rethrown loudly rather than swallowed.
/// </para>
/// <para>
/// #872: it lives here, as one reusable piece, because the defect that produced it was a *second*
/// reader in one test class re-deriving only one of the two halves. A reader that reaches for this
/// gets both or neither.
/// </para>
/// </summary>
internal static class LiveFileReader
{
    /// <summary>
    /// How long a read keeps retrying a sharing violation before rethrowing. The appender holds the
    /// file for microseconds per line; two seconds is orders of magnitude of headroom for a loaded
    /// CI runner without hiding a genuinely stuck handle.
    /// </summary>
    internal static readonly TimeSpan ShareRetryBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Line-wise read, empty lines dropped. A torn line can at worst be the final one, which the
    /// callers' &gt;=-then-settle pattern already tolerates.
    /// </summary>
    internal static List<string> ReadLines(string path) =>
        Read(path, reader =>
        {
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                if (line.Length > 0)
                {
                    lines.Add(line);
                }
            }

            return lines;
        });

    /// <summary>Whole-file read, for a caller that wants the text rather than the lines.</summary>
    internal static string ReadText(string path) => Read(path, reader => reader.ReadToEnd());

    /// <summary>
    /// Polls until <paramref name="filePath"/> exists and has non-blank content, then returns it;
    /// returns empty if that has not happened within <paramref name="timeout"/>. The caller's own
    /// assertion is what turns that into a failure, not this method.
    /// </summary>
    internal static async Task<string> WaitForContentAsync(string filePath, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(filePath))
            {
                var content = ReadText(filePath);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    return content;
                }
            }

            await Task.Delay(100);
        }

        return string.Empty;
    }

    /// <summary>
    /// The one place the tolerant open and its retry live, so the readers above cannot drift apart.
    /// </summary>
    private static T Read<T>(string path, Func<StreamReader, T> read)
    {
        var deadline = DateTime.UtcNow + ShareRetryBudget;
        while (true)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return read(reader);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(25));
            }
        }
    }
}
