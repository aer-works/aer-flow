using System.Collections.Concurrent;
using System.Text.Json;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters.Tests;

/// <summary>
/// #667: a resolve whose launch config would be byte-identical to what is on disk must not write,
/// and concurrent resolves must not cost an unretried reader its read.
/// </summary>
/// <remarks>
/// Why the write is skipped, what the skip does and does not close, and the numbers measured before
/// it all live on <see cref="AtomicLaunchConfigWriter"/>. This file is the instrument, not a second
/// copy of the reasoning.
/// </remarks>
[Collection(ClaudeLaunchConfigCollection.Name)]
public class LaunchConfigRewriteTests
{
    private static readonly WorkerContract ArchitectContract = new(
        "architect", ["goal"], [new ProducedOutput("plan.md")], []);

    private static string SettingsPath =>
        Path.Combine(AerPaths.WorkerLaunchConfig, "claude-settings.json");

    /// <summary>The once-only file, written by <c>EnsureFileExists</c> and never rewritten.</summary>
    private static string McpConfigPath =>
        Path.Combine(AerPaths.WorkerLaunchConfig, "claude-mcp.json");

    private static void Resolve() =>
        new ClaudeWorkerAdapter().Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

    /// <summary>
    /// The direct form of the defect, with no concurrency in it at all: a resolve whose content
    /// matches what is already on disk must not touch the file. Stamping a known past mtime and
    /// requiring it to survive is exact — it does not depend on filesystem timestamp granularity the
    /// way a before/after comparison taken microseconds apart would.
    /// </summary>
    [Fact]
    public void A_resolve_that_would_write_identical_content_does_not_touch_the_file()
    {
        Resolve();

        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(SettingsPath, stamp);

        Resolve();

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(SettingsPath));
    }

    /// <summary>
    /// The reader-side measurement. Readers deliberately carry no retry, because the vendor CLI has
    /// none: it opens <c>--settings</c> once at spawn and a sharing violation there is a worker with
    /// no gate, not a transient it recovers from.
    /// </summary>
    /// <remarks>
    /// Reads the once-only <c>claude-mcp.json</c> as the control arm. It sits in the same directory,
    /// under the same reader code and the same contention, and differs only in not being rewritten.
    /// A control failure means the harness cannot read a quiet file either, so the settings result
    /// says nothing about the product.
    /// </remarks>
    [Fact]
    public async Task Concurrent_resolves_leave_every_unretried_reader_a_readable_settings_file()
    {
        Resolve();

        using var writersDone = new CancellationTokenSource();
        var settingsFailures = new ConcurrentBag<Exception>();
        var controlFailures = new ConcurrentBag<Exception>();
        var settingsReads = 0;
        var controlReads = 0;

        var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(() =>
        {
            var settings = reader % 2 == 0;
            var path = settings ? SettingsPath : McpConfigPath;

            while (!writersDone.IsCancellationRequested)
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    if (settings)
                    {
                        Interlocked.Increment(ref settingsReads);
                    }
                    else
                    {
                        Interlocked.Increment(ref controlReads);
                    }
                }
                catch (Exception ex)
                {
                    (settings ? settingsFailures : controlFailures).Add(ex);
                }
            }
        })).ToArray();

        var writerFailures = new ConcurrentBag<Exception>();
        var writers = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() =>
            {
                for (var round = 0; round < 250; round++)
                {
                    // Collected rather than thrown: a resolve that dies here would abort the test
                    // before the reader measurement below could be reported, and would leave the
                    // reader loops spinning until process exit.
                    try
                    {
                        Resolve();
                    }
                    catch (Exception ex)
                    {
                        writerFailures.Add(ex);
                    }
                }
            }))
            .ToArray();

        try
        {
            await Task.WhenAll(writers);
        }
        finally
        {
            await writersDone.CancelAsync();
            await Task.WhenAll(readers);
        }

        Assert.True(
            settingsReads > 0 && controlReads > 0,
            $"Control: the reader loops observed the files {settingsReads}/{controlReads} times, so an " +
            "absence of failures below would prove nothing about the product.");
        Assert.True(
            controlFailures.IsEmpty,
            $"Control: {controlFailures.Count} reader(s) failed on the never-rewritten claude-mcp.json, " +
            $"so this run measures the harness rather than the rewrite. First: {controlFailures.FirstOrDefault()?.Message}");
        Assert.True(
            settingsFailures.IsEmpty,
            $"{settingsFailures.Count} of {settingsReads + settingsFailures.Count} readers could not load " +
            $"claude-settings.json while resolves ran. First: {settingsFailures.FirstOrDefault()?.Message}");
        Assert.True(
            writerFailures.IsEmpty,
            $"{writerFailures.Count} resolve(s) threw rather than writing. Under enough concurrency the " +
            "writer's own five-attempt retry budget is exhaustible, which a resolve surfaces as a failed " +
            $"dispatch. First: {writerFailures.FirstOrDefault()?.Message}");
    }
}
