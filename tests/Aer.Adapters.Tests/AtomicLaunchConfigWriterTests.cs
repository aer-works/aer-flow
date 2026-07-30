using System.Collections.Concurrent;
using Aer.Adapters.Tests.TestSupport;

namespace Aer.Adapters.Tests;

/// <summary>
/// #667: direct tests for the writer, against a throwaway directory. Every case here used to be
/// reachable only through <c>ClaudeWorkerAdapter.Resolve</c>, which meant asserting against the one
/// shared <c>claude-settings.json</c> the whole assembly resolves to.
/// </summary>
public sealed class AtomicLaunchConfigWriterTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"launch-config-{Guid.NewGuid():N}");

    private string Path_(string name) => System.IO.Path.Combine(_directory, name);

    public AtomicLaunchConfigWriterTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_directory);

    [Fact]
    public void A_file_that_does_not_exist_yet_is_written()
    {
        var path = Path_("settings.json");

        AtomicLaunchConfigWriter.Write(path, """{"hooks":"canonical"}""");

        Assert.Equal("""{"hooks":"canonical"}""", File.ReadAllText(path));
    }

    /// <summary>
    /// The defect itself, with no concurrency in it. Stamping a known past mtime and requiring it to
    /// survive is exact, where a before/after comparison would depend on timestamp granularity.
    /// </summary>
    [Fact]
    public void A_write_of_the_content_already_on_disk_does_not_touch_the_file()
    {
        var path = Path_("settings.json");
        const string content = """{"hooks":"canonical"}""";
        AtomicLaunchConfigWriter.Write(path, content);

        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        AtomicLaunchConfigWriter.Write(path, content);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    /// <summary>
    /// The polarity arm of the test above, and the #543 invariant the skip must not regress: comparing
    /// content is not "write once". <c>ClaudeWorkerAdapterTests</c> asserts the same correction through
    /// <c>Resolve</c>, which is a claim about the wiring rather than the writer.
    /// </summary>
    [Fact]
    public void A_file_whose_content_has_drifted_is_rewritten()
    {
        var path = Path_("settings.json");
        AtomicLaunchConfigWriter.Write(path, """{"hooks":"canonical"}""");

        const string stale = """{"hooks":{"PreToolUse":[{"stale":"pre-543-content"}]}}""";
        File.WriteAllText(path, stale);

        AtomicLaunchConfigWriter.Write(path, """{"hooks":"canonical"}""");

        var rewritten = File.ReadAllText(path);
        Assert.NotEqual(stale, rewritten);
        Assert.DoesNotContain("stale", rewritten);
    }

    /// <summary>
    /// A file differing only in trailing whitespace is drift, not a match. Guards the comparison
    /// against being loosened to something forgiving later: the canonical content is exact, and
    /// anything else is a file the vendor may parse differently.
    /// </summary>
    [Fact]
    public void A_file_differing_only_in_trailing_whitespace_is_rewritten()
    {
        var path = Path_("settings.json");
        const string content = """{"hooks":"canonical"}""";
        File.WriteAllText(path, content + "\n");

        AtomicLaunchConfigWriter.Write(path, content);

        Assert.Equal(content, File.ReadAllText(path));
    }

    /// <summary>
    /// A file the probe cannot read counts as differing, so the call falls through to the write
    /// instead of throwing out of the comparison. Windows arm.
    /// </summary>
    /// <remarks>
    /// The content is <b>identical</b> to what is on disk, which is what discriminates: a probe that
    /// propagated would throw before the write loop was reached. <see cref="FileShare.None"/> is
    /// enforced on Windows, so both the read and the rename fail and the assertion is on which one
    /// did. Control run, not assumed: removing the catch in <c>AlreadyHolds</c> turns this red.
    /// </remarks>
    [Fact]
    public void A_destination_the_probe_cannot_read_falls_through_to_the_write_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("FileShare.None is advisory off Windows, so this cannot make a read fail here.");
        }

        var path = Path_("settings.json");
        const string content = """{"hooks":"canonical"}""";
        AtomicLaunchConfigWriter.Write(path, content);

        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var thrown = Record.Exception(() => AtomicLaunchConfigWriter.Write(path, content));

        Assert.NotNull(thrown);
        Assert.Contains("Move", thrown.StackTrace ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// #682: enough concurrent cold-start writers -- no file on disk yet, so every one of them is a
    /// first writer -- exhausts <c>MaxAttempts</c> on this machine. Every writer's content is
    /// byte-identical (the real caller's is a deterministic function of
    /// <see cref="AppContext.BaseDirectory"/>; this test fixes that by construction), which is the
    /// premise the fix rests on: a writer that loses the rename does not need to win it, it needs the
    /// file to already hold what it wanted to write.
    /// </summary>
    [Fact]
    public async Task Many_concurrent_cold_start_writers_with_identical_content_do_not_throw()
    {
        var path = Path_("settings.json");
        const string content = """{"hooks":"canonical"}""";
        const int writerCount = 40;

        using var barrier = new Barrier(writerCount);
        var exceptions = new ConcurrentBag<Exception>();

        var writers = Enumerable.Range(0, writerCount).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            try
            {
                AtomicLaunchConfigWriter.Write(path, content);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }));

        await Task.WhenAll(writers);

        Assert.Empty(exceptions);
        Assert.Equal(content, File.ReadAllText(path));
    }

    /// <summary>
    /// The same claim on Unix, where the observable differs: a mode-000 file fails the probe's read
    /// but not the rename, so the call has to <i>succeed</i> rather than throw from somewhere else.
    /// </summary>
    /// <remarks>
    /// Skips when the read succeeds anyway — running as root defeats the permission bits, and a pass
    /// under those conditions would prove nothing.
    /// </remarks>
    [Fact]
    public void A_destination_the_probe_cannot_read_falls_through_to_the_write_on_unix()
    {
        // Guarded with else rather than an early return so CA1416 can see that SetUnixFileMode is
        // unreachable on Windows -- Assert.Skip throws, but nothing tells the analyzer that.
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix mode bits do not apply; the Windows arm covers this platform.");
        }
        else
        {
            var path = Path_("settings.json");
            const string content = """{"hooks":"canonical"}""";
            AtomicLaunchConfigWriter.Write(path, content);
            File.SetUnixFileMode(path, UnixFileMode.None);

            if (Record.Exception(() => File.ReadAllText(path)) is null)
            {
                Assert.Skip("The mode-000 file is still readable (running as root?), so the probe cannot fail.");
            }

            AtomicLaunchConfigWriter.Write(path, content);

            Assert.Equal(content, File.ReadAllText(path));
        }
    }
}
