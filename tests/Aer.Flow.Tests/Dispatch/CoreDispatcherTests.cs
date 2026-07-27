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

    /// <summary>
    /// #533: <see cref="CoreDispatchTarget.Environment"/> is the seam a vendor adapter uses to set a
    /// vendor-specific variable (e.g. Claude Code's subagent depth cap) without <c>Aer.Flow</c> ever
    /// knowing the variable's name (Architecture Rule 2). This proves it actually reaches the child
    /// process, not just that <c>CoreDispatcher</c> compiles against it.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_sets_CoreDispatchTarget_Environment_variables_on_the_child_process()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var target = EchoEnvVarToOutputFile("AER_533_TEST_VAR", [("AER_533_TEST_VAR", "reached-the-child")]);

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var result = await dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            var written = await File.ReadAllTextAsync(
                Path.Combine(outputDirectory, "hello.txt"), TestContext.Current.CancellationToken);
            Assert.Contains("reached-the-child", written);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            File.Delete(logPath);
        }
    }

    /// <summary>
    /// The control for the test above: an unset target requests no environment contribution, so the
    /// shell's own unset-variable expansion (empty on both cmd and sh) is what appears — proving the
    /// prior test's positive result came from <see cref="CoreDispatchTarget.Environment"/> and not
    /// from something already present in the test host's own environment.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_leaves_the_variable_unset_when_CoreDispatchTarget_Environment_is_null()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var target = EchoEnvVarToOutputFile("AER_533_TEST_VAR", environment: null);

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var result = await dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            var written = await File.ReadAllTextAsync(
                Path.Combine(outputDirectory, "hello.txt"), TestContext.Current.CancellationToken);
            Assert.DoesNotContain("reached-the-child", written);
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

    private static CoreDispatchTarget EchoEnvVarToOutputFile(
        string variableName, IReadOnlyList<(string Name, string Value)>? environment) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget(
            "cmd", ["/c", $"echo %{variableName}% > %AER_OUTPUT_DIR%\\hello.txt"], Environment: environment)
        : new CoreDispatchTarget(
            "sh", ["-c", $"echo \"${variableName}\" > \"$AER_OUTPUT_DIR/hello.txt\""], Environment: environment);

    private static CoreDispatchTarget PrintCwdToOutputFile(string workingDirectory) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", "cd > %AER_OUTPUT_DIR%\\hello.txt"], workingDirectory)
        : new CoreDispatchTarget("sh", ["-c", "pwd > \"$AER_OUTPUT_DIR/hello.txt\""], workingDirectory);

    // Issue #292: durable capture of an ordinary step's resolved prompt, written into the execution's
    // own output directory before the worker ever spawns.

    [Fact]
    public async Task DispatchAsync_writes_the_expanded_PromptText_to_prompt_txt_in_the_output_directory()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var environment = ArtifactManager.BuildEnvironment(["/inputs/goal.md"], outputDirectory, artifactsRoot);
            var request = MakeRequest(environment);
            var promptText = OperatingSystem.IsWindows()
                ? "Use %AER_INPUT_0% and write to %AER_OUTPUT_DIR%."
                : "Use $AER_INPUT_0 and write to $AER_OUTPUT_DIR.";
            var target = EchoHelloToOutputFile() with { PromptText = promptText };

            await using var writer = new FlowEventLogWriter(logPath);
            await new CoreDispatcher(writer).DispatchAsync(request, target, TestContext.Current.CancellationToken);

            var promptFilePath = Path.Combine(outputDirectory, ArtifactManager.PromptFileName);
            Assert.True(File.Exists(promptFilePath));
            var writtenPrompt = await File.ReadAllTextAsync(promptFilePath, TestContext.Current.CancellationToken);
            Assert.Equal("Use /inputs/goal.md and write to " + outputDirectory + ".", writtenPrompt);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            File.Delete(logPath);
        }
    }

    /// <summary>
    /// Written before the worker spawns (§7-style intent-first ordering), so the prompt stays
    /// available for audit even when the worker itself exits nonzero -- exactly the "present even if
    /// the execution later fails" guarantee issue #292 asks for.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_writes_prompt_txt_even_when_the_worker_exits_non_zero()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var environment = ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot);
            var request = MakeRequest(environment);
            var target = (OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", "exit 7"])
                : new CoreDispatchTarget("sh", ["-c", "exit 7"])) with
            { PromptText = "Draft a plan." };

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer).DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(7, result.ExitCode);
            var promptFilePath = Path.Combine(outputDirectory, ArtifactManager.PromptFileName);
            Assert.True(File.Exists(promptFilePath));
            Assert.Equal("Draft a plan.", await File.ReadAllTextAsync(promptFilePath, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task DispatchAsync_writes_no_prompt_file_when_PromptText_is_null()
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
            await new CoreDispatcher(writer).DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.False(File.Exists(Path.Combine(outputDirectory, ArtifactManager.PromptFileName)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            File.Delete(logPath);
        }
    }

    // #563: a worker's stderr used to be read by aer-core's drain thread and passed to
    // io::copy(.., io::sink()) — produced, consumed, and discarded. These spawn a real process that
    // writes to a real stderr pipe; nothing here is stubbed.

    [Fact]
    public async Task DispatchAsync_captures_what_the_worker_wrote_to_stderr()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var request = MakeRequest([]);
            var target = WriteToStderrAndExit("BOILER-PLATE-DIAGNOSTIC", exitCode: 1);

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer).DispatchAsync(
                request, target, TestContext.Current.CancellationToken);

            Assert.Equal(1, result.ExitCode);
            Assert.NotNull(result.StderrTail);
            Assert.Contains("BOILER-PLATE-DIAGNOSTIC", result.StderrTail);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    /// <summary>
    /// The polarity control for the test above. Without it, an implementation that returned, say, an
    /// empty string for every dispatch would still pass the positive arm's <c>Contains</c> — this is
    /// what makes <c>StderrTail</c> mean "the worker spoke" rather than "the field exists".
    /// </summary>
    [Fact]
    public async Task DispatchAsync_leaves_StderrTail_null_when_the_worker_writes_nothing_to_stderr()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var request = MakeRequest([]);
            var target = OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", "exit 1"])
                : new CoreDispatchTarget("sh", ["-c", "exit 1"]);

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer).DispatchAsync(
                request, target, TestContext.Current.CancellationToken);

            Assert.Equal(1, result.ExitCode);
            Assert.Null(result.StderrTail);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    /// <summary>
    /// Proves the buffer keeps the <i>end</i> and is bounded, in one test — the two properties are
    /// one mistake apart. A head-keeping implementation is equally "bounded" and would surface the
    /// worker's opening banner while discarding the error it exited on, which is the exact content
    /// #563 exists to deliver.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_bounds_StderrTail_and_keeps_the_end_rather_than_the_beginning()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        var payloadDirectory = Path.Combine(Path.GetTempPath(), $"stderr-payload-{Guid.NewGuid():N}");
        try
        {
            // Dumped from a file rather than generated by a shell loop: `cmd`'s `for /L` and `sh`'s
            // `for` need different quoting and escaping, and the first version of this test silently
            // emitted a single padding line on Windows — making it a test of batch syntax rather than
            // of the buffer. Writing the bytes here keeps the content identical on both platforms.
            //
            // Referenced by bare filename from a dedicated working directory, never by absolute path:
            // the whole script is one argument, so a path containing a space makes the launcher
            // re-quote it and the inner quotes then break the command. A bare name cannot contain one.
            Directory.CreateDirectory(payloadDirectory);
            const string payloadFileName = "payload.txt";
            var payload = "FIRST-MARKER" + new string('x', CoreDispatcher.MaxRetainedStderrLength * 3) + "LAST-MARKER";
            await File.WriteAllTextAsync(
                Path.Combine(payloadDirectory, payloadFileName), payload, TestContext.Current.CancellationToken);

            var target = OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", $"type {payloadFileName} 1>&2 & exit 1"], payloadDirectory)
                : new CoreDispatchTarget("sh", ["-c", $"cat {payloadFileName} >&2; exit 1"], payloadDirectory);

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer).DispatchAsync(
                MakeRequest([]), target, TestContext.Current.CancellationToken);

            Assert.NotNull(result.StderrTail);
            Assert.True(
                result.StderrTail.Length <= CoreDispatcher.MaxRetainedStderrLength,
                $"retained {result.StderrTail.Length} chars, cap is {CoreDispatcher.MaxRetainedStderrLength}");
            Assert.Contains("LAST-MARKER", result.StderrTail);
            Assert.DoesNotContain("FIRST-MARKER", result.StderrTail);
        }
        finally
        {
            File.Delete(logPath);
            DirectoryCleanup.DeleteRecursively(payloadDirectory);
        }
    }

    /// <summary>
    /// A pipe splits at arbitrary byte offsets, so a multi-byte UTF-8 sequence routinely straddles
    /// two chunks. Decoding each chunk with its own <c>GetString</c> emits U+FFFD at every such
    /// boundary and corrupts exactly the non-ASCII diagnostic the field exists to carry.
    /// </summary>
    /// <remarks>
    /// Driven through the decode helpers directly rather than through a spawned process, because a
    /// real pipe gives no control over <i>where</i> it splits: a short payload arrives in a single
    /// chunk, never reaches the boundary case, and would pass against the naive implementation this
    /// is written to exclude. Splitting the sequence by hand is what makes the test discriminate.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Stderr_decoding_survives_a_multi_byte_sequence_split_across_two_chunks(int splitWithinCharacter)
    {
        // U+1F6A8 (surrogate pair in UTF-16, 4 bytes in UTF-8) preceded by a 2-byte and a 3-byte
        // character, so the split offsets below land inside sequences of three different lengths.
        const string payload = "é—🚨";
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);

        // Split inside the final 4-byte sequence, at each of its three interior offsets.
        var splitAt = bytes.Length - 4 + splitWithinCharacter;

        var buffer = new System.Text.StringBuilder();
        var decoder = System.Text.Encoding.UTF8.GetDecoder();

        CoreDispatcher.AppendBoundedTail(buffer, decoder, bytes[..splitAt]);
        CoreDispatcher.AppendBoundedTail(buffer, decoder, bytes[splitAt..]);
        CoreDispatcher.FlushDecoder(buffer, decoder);

        Assert.Equal(payload, buffer.ToString());
        Assert.DoesNotContain('�', buffer.ToString());
    }

    /// <summary>
    /// Trimming to the tail cuts from the front, so it can orphan a low surrogate whose high half is
    /// inside the removed prefix — the mirror of the hazard
    /// <c>ContractValidator.TrimWithoutSplittingSurrogatePair</c> guards at the other end. An orphan
    /// is not a rendering nicety: it is an unpaired UTF-16 code unit that does not round-trip.
    /// </summary>
    [Fact]
    public void Trimming_stderr_to_the_tail_never_leaves_an_orphaned_low_surrogate()
    {
        // Non-BMP characters only, so every char in the builder is half of a pair and the cut is
        // forced to land mid-pair for one of the two parities regardless of the exact cap.
        var buffer = new System.Text.StringBuilder(
            string.Concat(Enumerable.Repeat("🚨", CoreDispatcher.MaxRetainedStderrLength)));

        CoreDispatcher.TrimToTail(buffer);

        var trimmed = buffer.ToString();
        Assert.True(trimmed.Length <= CoreDispatcher.MaxRetainedStderrLength);
        Assert.False(char.IsLowSurrogate(trimmed[0]), "leading char is an orphaned low surrogate");

        // The real proof: an orphaned surrogate does not survive a UTF-8 round-trip, so this
        // comparison fails on any implementation that leaves one behind.
        var roundTripped = System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(trimmed));
        Assert.Equal(trimmed, roundTripped);
    }

    private static CoreDispatchTarget WriteToStderrAndExit(string message, int exitCode) =>
        OperatingSystem.IsWindows()
            ? new CoreDispatchTarget("cmd", ["/c", $"echo {message} 1>&2 & exit {exitCode}"])
            : new CoreDispatchTarget("sh", ["-c", $"echo {message} >&2; exit {exitCode}"]);
}
