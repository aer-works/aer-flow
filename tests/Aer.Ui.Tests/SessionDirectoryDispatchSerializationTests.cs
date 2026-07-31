using System.Net.Http.Json;
using System.Text.Json;
using Aer.Adapters;
using Aer.Daemon;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Ui.Core;
using Aer.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// #590: the vendor CLI's own <c>--session-id</c> guard is an existence check, not a lock
/// (vendor-doc-audit.md, "`--session-id` is guarded by an existence check, not a lock") -- two
/// concurrent dispatches of the same persisted vendor session id both succeed and both write.
/// <c>/api/tasks/run</c> and <c>/api/tasks/decide</c> dispatch whatever <c>bindings.json</c> says
/// with no serialisation of their own, unlike the chat pipeline's <c>SessionTurnLockFor</c>
/// (Program.cs).
///
/// <para>
/// Flow's own readiness is monotonic within one directory's pump: once a step dispatches and
/// settles, nothing makes it "ready" again for a second, independent call to pick up. So "two
/// concurrent calls against one directory" can NEVER observe two completions the way "two calls
/// against two different directories" can -- that would be true even with a perfect lock. What the
/// lock changes is whether the second call's request is silently lost to Flow's own
/// <c>ConcurrencyGuard</c> throwing <c>WorkflowLockedException</c> (pre-#590: swallowed by
/// <c>TaskSession.RunAsync</c>/<c>DecideAsync</c>'s <c>catch (AerFlowException)</c>, invisible to the
/// caller since both endpoints already return 200 before dispatch runs) or cleanly waits its turn.
/// Every test below asserts exactly the number of completions the *serialised* pump can produce, and
/// separately asserts no dispatch overlapped (the collision file) and, via #828's dispatch-failure log,
/// that a losing racer was recorded rather than silently dropped where one exists.
/// </para>
///
/// <see cref="SlowCollisionStubAdapter"/>'s dispatched process itself detects overlap (a marker file
/// left in the invocation's working directory), rather than this test measuring wall-clock timing --
/// timing is inherently flaky under CI load, a marker file left by another live process is not.
///
/// Shares the <c>DaemonIntegrationTests</c> collection for the same reason every other class here
/// does: each spins up a real Kestrel daemon and points a config store at the same per-user file.
/// </summary>
[Collection("DaemonIntegrationTests")]
public class SessionDirectoryDispatchSerializationTests : IAsyncLifetime
{
    private static readonly StepId WorkerStep = new("worker-step");

    private DaemonTestInstance? _daemon;
    private string _baseUrl = "";
    private readonly HttpClient _client = new();

    public async ValueTask InitializeAsync()
    {
        IReadOnlyDictionary<string, IWorkerAdapter> stubAdapters = new Dictionary<string, IWorkerAdapter>
        {
            ["slow-collision"] = new SlowCollisionStubAdapter(),
        };

        _daemon = await DaemonTestHost.StartAsync(stubAdapters);
        _baseUrl = _daemon.BaseUrl;

        for (int i = 0; i < 30; i++)
        {
            try
            {
                var response = await _client.GetAsync($"{_baseUrl}/api/version", TestContext.Current.CancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }

        var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
        if (File.Exists(tokenFile))
        {
            var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_daemon != null)
        {
            await _daemon.DisposeAsync();
        }

        _client.Dispose();
    }

    [Fact]
    public async Task ConcurrentRuns_OnTheSameDirectory_NeverDispatchOverlappingWorkers()
    {
        var (taskDirectory, bindingsFilePath) = await CreateReadyTaskDirectoryAsync();

        // No await between the two POSTs: both endpoints return 200 before their fire-and-forget
        // dispatch runs, so this genuinely races the two Task.Run bodies against one another.
        var run1 = _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/run",
            new RunTaskRequest(taskDirectory, null, bindingsFilePath),
            TestContext.Current.CancellationToken);
        var run2 = _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/run",
            new RunTaskRequest(taskDirectory, null, bindingsFilePath),
            TestContext.Current.CancellationToken);

        var responses = await Task.WhenAll(run1, run2);
        foreach (var response in responses)
        {
            Assert.True(response.IsSuccessStatusCode);
        }

        await WaitForCompletionsAsync(taskDirectory, expectedCompletions: 1);

        // Settle grace: readiness is monotonic (see class doc), so only one of the two calls can
        // ever dispatch -- the whichever-arrives-second call finds the step already Succeeded and
        // does nothing. This grace period is what tells "exactly one, ever" apart from "one has
        // landed but a second may still be racing in" -- reading immediately after the first
        // completion lands would only prove the latter.
        await Task.Delay(SlowCollisionStubAdapter.DispatchDelay * 2, TestContext.Current.CancellationToken);

        AssertNoCollision(taskDirectory);
        Assert.Equal(1, ReadCompletionsCount(taskDirectory));
    }

    [Fact]
    public async Task ConcurrentRunAndDecide_OnTheSameDirectory_NeverDispatchOverlappingWorkers()
    {
        // #590 review finding: the original version of this test posted /api/tasks/run twice and
        // never called /api/tasks/decide at all, despite its name and doc comment claiming run+decide
        // coverage -- decide's lock wrapper (Program.cs) has its own pre-lock ArtifactReference branch
        // nothing exercised. This version actually races the two different endpoints.
        var (taskDirectory, executionId) = await CreatePausedFailedTaskDirectoryAsync();
        var bindingsFilePath = Path.Combine(taskDirectory, "bindings.json");

        // Set directly rather than relying on the /api/tasks/run request below to set it as a side
        // effect (Program.cs) -- run and decide are about to fire with no await between them, so that
        // side effect would itself race decide's own read of it.
        DaemonHost.App!.Services.GetRequiredService<BindingsPathHolder>().BindingsFilePath = bindingsFilePath;

        // /api/tasks/run against an already-Paused workflow dispatches nothing (Paused steps are
        // never "ready") -- it still exercises /api/tasks/run's lock wrapper concurrently with
        // decide's, which is the actual coverage gap; it just cannot itself add a completion.
        var run = _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/run",
            new RunTaskRequest(taskDirectory, null, bindingsFilePath),
            TestContext.Current.CancellationToken);
        var decide = _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/decide",
            new DecideTaskRequest(taskDirectory, WorkerStep.Value, executionId, DecisionType.RetryWithRevision),
            TestContext.Current.CancellationToken);

        var responses = await Task.WhenAll(run, decide);
        foreach (var response in responses)
        {
            Assert.True(response.IsSuccessStatusCode);
        }

        await WaitForCompletionsAsync(taskDirectory, expectedCompletions: 1);
        await Task.Delay(SlowCollisionStubAdapter.DispatchDelay * 2, TestContext.Current.CancellationToken);

        AssertNoCollision(taskDirectory);
        Assert.Equal(1, ReadCompletionsCount(taskDirectory));
    }

    [Fact]
    public async Task ConcurrentDecides_OnTheSameDirectory_OnlyOneDispatchesAndTheLoserIsRecorded()
    {
        var (taskDirectory, executionId) = await CreatePausedFailedTaskDirectoryAsync();

        // DecideCommand always loads a bindings file regardless of decision type (Aer.Cli's
        // DecideCommand.cs), read through the daemon's DI-registered BindingsPathHolder -- normally
        // populated as a side effect of /api/tasks/open or /api/tasks/run, neither of which this test
        // calls (see DaemonIntegrationTests.Reject_TriggersASecondWebSocketBroadcast... for the same
        // pattern), so it must be set directly.
        DaemonHost.App!.Services.GetRequiredService<BindingsPathHolder>().BindingsFilePath =
            Path.Combine(taskDirectory, "bindings.json");

        var decide1 = _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/decide",
            new DecideTaskRequest(taskDirectory, WorkerStep.Value, executionId, DecisionType.RetryWithRevision),
            TestContext.Current.CancellationToken);
        var decide2 = _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/decide",
            new DecideTaskRequest(taskDirectory, WorkerStep.Value, executionId, DecisionType.RetryWithRevision),
            TestContext.Current.CancellationToken);

        var responses = await Task.WhenAll(decide1, decide2);
        foreach (var response in responses)
        {
            Assert.True(response.IsSuccessStatusCode);
        }

        await WaitForCompletionsAsync(taskDirectory, expectedCompletions: 1);
        await Task.Delay(SlowCollisionStubAdapter.DispatchDelay * 2, TestContext.Current.CancellationToken);

        AssertNoCollision(taskDirectory);
        Assert.Equal(1, ReadCompletionsCount(taskDirectory));

        // #828: the loser -- the second decide to actually run, once serialised by the #590 lock --
        // finds the execution no longer Paused (ExternalDecisionValidator) and throws
        // InvalidExternalDecisionException. Both endpoints answer 200 before dispatch runs, so before
        // #828 this failure reached Console.Error and nowhere else. Confirms it is now durably
        // recorded rather than silently lost.
        var errorLogPath = Path.Combine(taskDirectory, ".aer", "turn-errors.log");
        var errorLog = await WaitForFileContentAsync(errorLogPath);
        Assert.Contains("/api/tasks/decide", errorLog);
        // TaskSession.DecideAsync's in-process fallback catches InvalidExternalDecisionException
        // itself and returns MutationOutcome(ex.Message) rather than throwing (see Program.cs's
        // #828 comment) -- what's recorded is ExternalDecisionValidator's message text, not the
        // exception type name.
        Assert.Contains("is not the currently paused latest attempt", errorLog);
    }

    [Fact]
    public async Task ARunThatFailsToResolveItsBindings_StillReleasesTheLockForTheNextRun()
    {
        // Exception-safety arm (work item 4): a dispatch that fails must release the per-directory
        // lock so a follow-up dispatch on the same directory still runs. UnknownWorkerAdapterException
        // is an AerFlowException, caught inside TaskSession.RunAsync's own fallback branch and turned
        // into a MutationOutcome rather than an escaping .NET exception -- but Program.cs's
        // turnLock.Release() sits in a `finally` around the ENTIRE `await session.RunAsync(...)` call,
        // not inside a catch keyed to a specific exception type, so it runs identically whether
        // RunAsync swallows the failure internally or lets one escape. That makes this HTTP-level test
        // representative of both cases -- see the commit body for why a second, lock-internal unit
        // test would be redundant rather than additive here.
        var (taskDirectory, goodBindingsFilePath) = await CreateReadyTaskDirectoryAsync();
        var badBindingsFilePath = await WriteUnresolvableBindingsAsync(taskDirectory);

        var badRunResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/run",
            new RunTaskRequest(taskDirectory, null, badBindingsFilePath),
            TestContext.Current.CancellationToken);
        Assert.True(badRunResponse.IsSuccessStatusCode);

        // Wait for the failed dispatch to actually finish (and release the lock) before firing the
        // next one -- #828's error log is the observable signal that the background body completed.
        var errorLogPath = Path.Combine(taskDirectory, ".aer", "turn-errors.log");
        await WaitForFileContentAsync(errorLogPath);

        var goodRunResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/run",
            new RunTaskRequest(taskDirectory, null, goodBindingsFilePath),
            TestContext.Current.CancellationToken);
        Assert.True(goodRunResponse.IsSuccessStatusCode);

        // If the lock were never released, this would hang until WaitForCompletionsAsync's own
        // internal 30s deadline and then fail on the assertion below -- a genuine timeout, not a
        // silent pass.
        await WaitForCompletionsAsync(taskDirectory, expectedCompletions: 1);
        Assert.Equal(1, ReadCompletionsCount(taskDirectory));
    }

    [Fact]
    public async Task ConcurrentRuns_OnDifferentDirectories_StillProceedConcurrently()
    {
        var (directoryA, bindingsA) = await CreateReadyTaskDirectoryAsync();
        var (directoryB, bindingsB) = await CreateReadyTaskDirectoryAsync();

        var runA = _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/run", new RunTaskRequest(directoryA, null, bindingsA), TestContext.Current.CancellationToken);
        var runB = _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/run", new RunTaskRequest(directoryB, null, bindingsB), TestContext.Current.CancellationToken);

        var responses = await Task.WhenAll(runA, runB);
        foreach (var response in responses)
        {
            Assert.True(response.IsSuccessStatusCode);
        }

        await WaitForCompletionsAsync(directoryA, expectedCompletions: 1);
        await WaitForCompletionsAsync(directoryB, expectedCompletions: 1);

        // The discriminator is the GAP between the two dispatches' start stamps, not total wall
        // time -- why, and why the earlier 1.5x-DispatchDelay wall-clock bound failed a genuinely
        // concurrent CI run, is SlowCollisionStubAdapter.StartStampFilePrefix's doc.
        var startA = ReadDispatchStartUtc(directoryA);
        var startB = ReadDispatchStartUtc(directoryB);
        var gap = (startA - startB).Duration();
        Assert.True(gap < SlowCollisionStubAdapter.DispatchDelay,
            $"The two dispatches' start stamps are {gap.TotalMilliseconds:0}ms apart -- looks serialised, not concurrent.");

        AssertNoCollision(directoryA);
        AssertNoCollision(directoryB);
    }

    private static void AssertNoCollision(string taskDirectory)
    {
        var collisionFile = Path.Combine(taskDirectory, SlowCollisionStubAdapter.CollisionFileName);
        Assert.False(File.Exists(collisionFile),
            "Two dispatches against the same task directory overlapped -- the per-directory lock did not serialise them.");
    }

    /// <summary>
    /// #872's discriminating red, and the reason this is a test rather than a comment: the defect
    /// only ever appeared as a flake under machine load, so nothing in the suite could tell a fixed
    /// helper from a lucky one. Here the contended window is created deliberately -- a writer holds
    /// the file with <c>FileShare.None</c>, exactly the momentary open the daemon's own append takes
    /// -- and released while the reader is polling. Before the fix the reader throws the instant it
    /// finds the file; after it, it waits the writer out.
    /// <para>
    /// Windows-only, and scoped to what the mechanism actually is rather than for convenience:
    /// <c>FileShare.None</c> is enforced by the OS on Windows, while POSIX file locking is advisory,
    /// so on Linux/macOS the read simply succeeds and the arm would be green either way -- a test
    /// that passes without discriminating is worse than one that says it did not run. Same scoping
    /// mistake #865 shipped, applied deliberately this time.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_reader_waits_out_a_writer_holding_the_file_without_sharing_read()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("FileShare.None is only OS-enforced on Windows; elsewhere this arm cannot discriminate. See #872.");
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "aer_872_share_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var path = Path.Combine(directory, "turn-errors.log");
            await File.WriteAllTextAsync(path, "the dispatch failure text", TestContext.Current.CancellationToken);

            var exclusive = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            try
            {
                var read = Task.Run(() => WaitForFileContentAsync(path));

                // Long enough that the reader has certainly attempted the open and been refused,
                // and well inside ShareRetryBudget so a correct reader still has retries left.
                await Task.Delay(250, TestContext.Current.CancellationToken);
                exclusive.Dispose();

                Assert.Equal("the dispatch failure text", await read);
            }
            finally
            {
                exclusive.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int ReadCompletionsCount(string taskDirectory)
    {
        var completionsFile = Path.Combine(taskDirectory, SlowCollisionStubAdapter.CompletionsFileName);
        return File.Exists(completionsFile) ? ReadLinesShareTolerant(completionsFile).Count : 0;
    }

    /// <summary>
    /// Line-wise read of a file a live writer may hold; the sharing rules are
    /// <see cref="ReadShareTolerant"/>'s. A torn line can at worst be the final one, which the
    /// callers' &gt;=-then-settle pattern already tolerates.
    /// </summary>
    private static List<string> ReadLinesShareTolerant(string path) =>
        ReadShareTolerant(path, reader =>
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

    /// <summary>
    /// #872: the whole-file counterpart, for a reader that wants the text rather than the lines. It
    /// exists because <see cref="WaitForFileContentAsync"/> read with <c>File.ReadAllTextAsync</c>
    /// and hit the race described above for real -- see that method.
    /// </summary>
    private static string ReadTextShareTolerant(string path) =>
        ReadShareTolerant(path, reader => reader.ReadToEnd());

    /// <summary>
    /// The one place the share-tolerant open and its retry live, so the readers above cannot drift
    /// apart -- and so a reader added later inherits both halves instead of re-deriving one of them,
    /// which is exactly how #872 happened.
    /// <para>
    /// These files are appended to by a still-live writer while the tests poll them, so a
    /// default-share read races the writer's handle on Windows (share violation, #839, caught on PR
    /// #838's CI). Both halves are needed and that is measured, not cautious: tolerant share flags
    /// alone are NOT enough, because Windows PowerShell 5.1's Add-Content takes a momentary open
    /// that does not share Read at all -- the flags-only version of this helper failed the same race
    /// in its own branch's gates run. So a transient open failure is also retried until
    /// <see cref="ShareRetryBudget"/> runs out, and then rethrown loudly rather than swallowed.
    /// </para>
    /// </summary>
    private static T ReadShareTolerant<T>(string path, Func<StreamReader, T> read)
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

    /// <summary>
    /// How long <see cref="ReadShareTolerant"/> keeps retrying a sharing violation before
    /// rethrowing. The appender holds the file for microseconds per line; two seconds is orders of
    /// magnitude of headroom for a loaded CI runner without hiding a genuinely stuck handle.
    /// </summary>
    private static readonly TimeSpan ShareRetryBudget = TimeSpan.FromSeconds(2);

    /// <summary>The one dispatch's start-stamp file time in this directory; fails loudly on zero or several.</summary>
    private static DateTime ReadDispatchStartUtc(string taskDirectory)
    {
        var stamps = Directory.GetFiles(taskDirectory, SlowCollisionStubAdapter.StartStampFilePrefix + "*");
        var stamp = Assert.Single(stamps);
        return File.GetLastWriteTimeUtc(stamp);
    }

    private static async Task<(string TaskDirectory, string BindingsFilePath)> CreateReadyTaskDirectoryAsync()
    {
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("dispatch-serialization-test"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(WorkerStep, "worker", [], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]));

        var taskDirectory = Path.Combine(Path.GetTempPath(), $"aer_590_dispatch_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(taskDirectory);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(taskDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

        var bindingsFilePath = Path.Combine(taskDirectory, "bindings.json");
        await WriteSlowCollisionBindingsAsync(bindingsFilePath, taskDirectory, promptTemplate: "irrelevant, no vendor is really invoked");

        return (taskDirectory, bindingsFilePath);
    }

    /// <summary>
    /// A single-step workflow whose one attempt has already failed (deterministically, via
    /// <see cref="SlowCollisionStubAdapter.ForceFailureSentinel"/>) and paused -- hand-written
    /// directly into <c>flow.jsonl</c>, the same technique
    /// <c>DaemonIntegrationTests.CreatePausedTaskDirectoryAsync</c> uses for its own Paused fixture,
    /// swapping <c>ExecutionSucceeded</c> for <c>ExecutionFailed</c> so the paused outcome is Failed --
    /// <c>ExternalDecisionValidator</c> refuses <c>RetryWithRevision</c> once the paused outcome is
    /// Succeeded, so a fixture built the other way could never legitimately re-dispatch.
    /// </summary>
    private static async Task<(string TaskDirectory, string ExecutionId)> CreatePausedFailedTaskDirectoryAsync()
    {
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("dispatch-serialization-paused-test"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(WorkerStep, "worker", [], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1), PausePoint: new PausePoint([]))]));

        var taskDirectory = Path.Combine(Path.GetTempPath(), $"aer_590_dispatch_paused_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(taskDirectory);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(taskDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

        var bindingsFilePath = Path.Combine(taskDirectory, "bindings.json");
        await WriteSlowCollisionBindingsAsync(bindingsFilePath, taskDirectory, promptTemplate: SlowCollisionStubAdapter.ForceFailureSentinel);

        var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
        var request = new ExecutionRequest(
            executionId,
            new WorkflowId("dispatch-serialization-paused-test"),
            WorkerStep,
            "worker",
            Inputs: [],
            Outputs: ["out"],
            Timeout: TimeSpan.FromSeconds(30),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        await using (var writer = new FlowEventLogWriter(Path.Combine(taskDirectory, "flow.jsonl")))
        {
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.ExecutionFailed(executionId, FailureClassification.Permanent, "forced failure for #590 test fixture"),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.WorkflowPaused(executionId, WorkerStep), TestContext.Current.CancellationToken);
        }

        return (taskDirectory, executionId.Value);
    }

    private static async Task WriteSlowCollisionBindingsAsync(string bindingsFilePath, string workingDirectory, string promptTemplate)
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["worker"] = new WorkerBindingConfigEntry(
                "slow-collision",
                new WorkerContract("worker", [], [new ProducedOutput("out")], []),
                promptTemplate,
                TimeSpan.FromSeconds(30),
                WorkingDirectory: workingDirectory),
        };

        await File.WriteAllTextAsync(bindingsFilePath, JsonSerializer.Serialize(config), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Names an adapter no registry entry can resolve, matching
    /// <c>DaemonIntegrationTests.WriteUnresolvableBindingsAsync</c>'s own convention (duplicated
    /// rather than shared -- each test class here owns its minimal fixture set) --
    /// <see cref="WorkerBindingResolver.Resolve"/> throws <see cref="UnknownWorkerAdapterException"/>
    /// synchronously, a fast, deterministic way to exercise the failure path with no live process.
    /// </summary>
    private static async Task<string> WriteUnresolvableBindingsAsync(string taskDirectory)
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["worker"] = new WorkerBindingConfigEntry(
                "not-a-registered-adapter", new WorkerContract("worker", [], [new ProducedOutput("out")], []),
                "irrelevant, never dispatched", TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(taskDirectory, "bad-bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config), TestContext.Current.CancellationToken);
        return path;
    }

    /// <summary>
    /// Polls until <paramref name="expectedCompletions"/> dispatches have actually run against
    /// <paramref name="taskDirectory"/> (or the timeout elapses -- the caller's own completions-count
    /// assertion is what turns a timeout into a failure, not this helper).
    /// </summary>
    private static async Task WaitForCompletionsAsync(string taskDirectory, int expectedCompletions)
    {
        var completionsFile = Path.Combine(taskDirectory, SlowCollisionStubAdapter.CompletionsFileName);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(completionsFile))
            {
                // Share-tolerant for the same reason as ReadCompletionsCount (#839).
                var lines = ReadLinesShareTolerant(completionsFile);
                if (lines.Count >= expectedCompletions)
                {
                    return;
                }
            }

            await Task.Delay(100);
        }
    }

    /// <summary>
    /// #872: this polled for two conditions -- the file not existing yet, and the file being empty --
    /// and read with <c>File.ReadAllTextAsync</c>, i.e. <c>FileShare.Read</c>. While the daemon holds
    /// <c>turn-errors.log</c> open for its own append, that read throws and the exception escaped the
    /// polling loop entirely: the retry was present, it just did not cover the failure that actually
    /// happens. Measured on a local gates run with a second lane building concurrently, which is why
    /// it only ever appeared under load. Same family as #839/#840/#842/#843, this time in test
    /// infrastructure rather than product code. Reading through
    /// <see cref="ReadTextShareTolerant"/> supplies both halves the fix needs, for the reason its
    /// own summary gives.
    /// </summary>
    private static async Task<string> WaitForFileContentAsync(string filePath)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(filePath))
            {
                var content = ReadTextShareTolerant(filePath);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    return content;
                }
            }

            await Task.Delay(100);
        }

        return string.Empty;
    }
}
