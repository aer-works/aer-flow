using Aer.Flow.Tests.TestSupport;
using System.Text.Json;
using Aer.Flow.Artifacts;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Store;

namespace Aer.Flow.Tests.Dispatch;

/// <summary>
/// Integration tests: these spawn a real process through the aer-core M5 <c>AerTask</c> binding
/// (M7 Phase 6's acceptance criteria — a trivial worker, output file appears in the pre-allocated
/// artifact directory, Core lifecycle events land in the log). No mocking of Aer.Core itself.
/// </summary>
public class CoreDispatcherTests
{
    private static readonly ExecutionId ExecutionId = new("exec-1");

    [Fact]
    public async Task DispatchAsync_runs_a_trivial_worker_and_the_output_file_appears_in_the_pre_allocated_directory()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var environment = ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot);
            var request = MakeRequest(environment);
            var target = EchoHelloToOutputFile();

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var result = await dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(CoreExitReason.Natural, result.Reason);
            Assert.True(File.Exists(Path.Combine(outputDirectory, "hello.txt")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task DispatchAsync_records_Started_and_Exited_CoreEvents_to_the_log()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var target = EchoHelloToOutputFile();

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await new CoreDispatcher(writer).DispatchAsync(request, target, TestContext.Current.CancellationToken);
            }

            var entries = (await File.ReadAllLinesAsync(logPath, TestContext.Current.CancellationToken))
                .Select(line => JsonSerializer.Deserialize<LogEntry>(line))
                .Cast<LogEntry.CoreLogEntry>()
                .Select(e => e.Event)
                .ToList();

            var started = Assert.Single(entries.OfType<CoreEvent.ExecutionStarted>());
            Assert.Equal(ExecutionId, started.ExecutionId);
            Assert.True(started.Pid > 0);

            var exited = Assert.Single(entries.OfType<CoreEvent.ExecutionExited>());
            Assert.Equal(ExecutionId, exited.ExecutionId);
            Assert.Equal(0, exited.ExitCode);
            Assert.Equal(CoreExitReason.Natural, exited.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task DispatchAsync_surfaces_a_non_zero_exit_code_without_throwing()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var request = MakeRequest([]);
            var target = OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", "exit 7"])
                : new CoreDispatchTarget("sh", ["-c", "exit 7"]);

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer).DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(7, result.ExitCode);
            Assert.Equal(CoreExitReason.Natural, result.Reason);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task DispatchAsync_does_not_resolve_pass_through_variable_values()
    {
        // Pass-through env var *values* are a future worker-adapter concern (spec §3) — the Core
        // Dispatcher must not accidentally leak a name-only declaration through as a literal value.
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var request = MakeRequest([new EnvironmentVariable.PassThrough("SOME_SECRET")]);
            var target = OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", "exit 0"])
                : new CoreDispatchTarget("sh", ["-c", "exit 0"]);

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer).DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    /// <summary>
    /// M23 Phase 3's own named verification bullet (#272): "an integration test asserting a spawned
    /// worker's actual cwd matches a configured WorkingDirectory" — through the real wiring
    /// (<see cref="CoreDispatchTarget.WorkingDirectory"/> → <see cref="CoreDispatcher.DispatchAsync"/>
    /// → the aer-core <c>AerTask.WithCwd</c> primitive), not the native primitive in isolation
    /// (already proven by <c>aer-core</c>'s own <c>EnvironmentAndWorkingDirectoryTests</c>).
    /// </summary>
    [Fact]
    public async Task DispatchAsync_spawns_the_worker_with_its_actual_cwd_set_to_the_configured_WorkingDirectory()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var configuredWorkingDirectory = Path.Combine(Path.GetTempPath(), $"cwd-target-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            Directory.CreateDirectory(configuredWorkingDirectory);

            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var environment = ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot);
            var request = MakeRequest(environment);
            var target = PrintCwdToOutputFile(configuredWorkingDirectory);

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer).DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            var printedCwd = (await File.ReadAllTextAsync(
                Path.Combine(outputDirectory, "hello.txt"), TestContext.Current.CancellationToken)).Trim();
            var expected = NormalizeRealPath(configuredWorkingDirectory);
            var actual = NormalizeRealPath(printedCwd);
            Assert.Equal(expected, actual, ignoreCase: OperatingSystem.IsWindows());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            DirectoryCleanup.DeleteRecursively(configuredWorkingDirectory);
            File.Delete(logPath);
        }
    }

    /// <summary>
    /// macOS resolves <c>/tmp</c>/<c>/var</c> (and therefore the default <see cref="Path.GetTempPath"/>
    /// root this test's directories live under) through a <c>/private</c> symlink at the OS level —
    /// a spawned shell's <c>pwd</c> reports the fully-resolved path even though the configured cwd
    /// was the pre-resolution one <see cref="Directory.CreateDirectory(string)"/> itself accepted.
    /// <see cref="Path.GetFullPath(string)"/> never resolves symlinks, so without this, "the same
    /// directory" fails a naive string comparison purely on this one OS. Only strips the prefix that
    /// specific symlink introduces — not a general realpath resolution — so this stays exact
    /// everywhere else.
    /// </summary>
    private static string NormalizeRealPath(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsMacOS() && normalized.StartsWith("/private/", StringComparison.Ordinal)
            ? normalized["/private".Length..]
            : normalized;
    }

    private static ExecutionRequest MakeRequest(IReadOnlyList<EnvironmentVariable> environment) => new(
        ExecutionId,
        new WorkflowId("wf-1"),
        new StepId("step-1"),
        "trivial",
        Inputs: [],
        Outputs: ["hello.txt"],
        Timeout: TimeSpan.FromSeconds(30),
        Environment: environment,
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    private static CoreDispatchTarget EchoHelloToOutputFile() => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", "echo hello > %AER_OUTPUT_DIR%\\hello.txt"])
        : new CoreDispatchTarget("sh", ["-c", "echo hello > \"$AER_OUTPUT_DIR/hello.txt\""]);

    private static CoreDispatchTarget PrintCwdToOutputFile(string workingDirectory) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", "cd > %AER_OUTPUT_DIR%\\hello.txt"], workingDirectory)
        : new CoreDispatchTarget("sh", ["-c", "pwd > \"$AER_OUTPUT_DIR/hello.txt\""], workingDirectory);
}
