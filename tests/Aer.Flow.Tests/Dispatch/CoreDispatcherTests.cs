using Aer.Flow.Tests.TestSupport;
using System.Globalization;
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
                .Select(line => JsonSerializer.Deserialize<LogEntry>(line, FlowEventLogJson.Options))
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
    // One offset interior to each of the three sequence lengths present, named by what it splits
    // rather than derived from the end of the array. The first version of this test computed all
    // three offsets from `bytes.Length - 4`, which put every one of them inside the same 4-byte
    // sequence while the comment claimed it covered three different lengths.
    [InlineData(1, "inside the 2-byte é")]
    [InlineData(3, "inside the 3-byte —")]
    [InlineData(6, "inside the 4-byte 🚨")]
    [InlineData(7, "inside the 4-byte 🚨, one byte later")]
    public void Stderr_decoding_survives_a_multi_byte_sequence_split_across_two_chunks(int splitAt, string what)
    {
        Assert.NotEmpty(what);

        // 9 UTF-8 bytes: é at [0,2), — at [2,5), 🚨 at [5,9).
        const string payload = "é—🚨";
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        Assert.Equal(9, bytes.Length);

        var tail = new StderrTailBuffer();
        tail.Append(bytes[..splitAt]);
        tail.Append(bytes[splitAt..]);

        Assert.Equal(payload, tail.ToTailOrNull());
        Assert.DoesNotContain('�', tail.ToTailOrNull()!);
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
        // The trailing "x" is what makes this test discriminate, and it is not cosmetic. Without it
        // the buffer is 4000 chars of surrogate pairs, so `excess` is 4000 - 2000 = 2000 — an EVEN
        // index, which in a run of pairs is always the HIGH half. The guard tests for a LOW
        // surrogate, so it never fired and the test passed with the guard deleted. Nor is that fixable
        // by choosing a different repeat count: for a run of pairs the parity of `excess` follows the
        // parity of the cap, so an even cap always cuts on a high surrogate. One BMP character makes
        // the length odd, `excess` 2001, and the cut land on a low surrogate — the case the guard exists for.
        var buffer = new System.Text.StringBuilder(
            string.Concat(Enumerable.Repeat("🚨", CoreDispatcher.MaxRetainedStderrLength)) + "x");

        Assert.True(char.IsLowSurrogate(buffer[buffer.Length - CoreDispatcher.MaxRetainedStderrLength]),
            "payload does not put a low surrogate at the cut index, so the guard under test is never reached");

        StderrTailBuffer.TrimToTail(buffer);

        var trimmed = buffer.ToString();
        Assert.True(trimmed.Length <= CoreDispatcher.MaxRetainedStderrLength);
        Assert.False(char.IsLowSurrogate(trimmed[0]), "leading char is an orphaned low surrogate");

        // The real proof: an orphaned surrogate does not survive a UTF-8 round-trip, so this
        // comparison fails on any implementation that leaves one behind.
        var roundTripped = System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(trimmed));
        Assert.Equal(trimmed, roundTripped);
    }

    /// <summary>
    /// The regression test for the reason whitespace is collapsed at capture time rather than at
    /// render time. A worker that prints its diagnostic and then clears a progress display on the way
    /// out — enough trailing blank lines to fill the retention buffer — used to have its entire
    /// retained tail be whitespace, which then collapsed to nothing and produced the bare pre-#563
    /// reason. The feature silently did not fire in its own headline use case.
    /// </summary>
    [Fact]
    public void A_diagnostic_followed_by_enough_blank_lines_to_fill_the_buffer_still_survives()
    {
        var tail = new StderrTailBuffer();
        tail.Append(System.Text.Encoding.UTF8.GetBytes("Error: model not found"));

        // Comfortably more than MaxRetainedStderrLength, so a buffer that retained whitespace would
        // hold nothing else by the end.
        tail.Append(System.Text.Encoding.UTF8.GetBytes(new string('\n', CoreDispatcher.MaxRetainedStderrLength * 2)));

        Assert.Equal("Error: model not found", tail.ToTailOrNull());
    }

    /// <summary>
    /// The other half of the same defect. Whitespace collapsing used to run <i>between</i> the
    /// retention cap and the display cap, so the two caps measured different units: mostly-whitespace
    /// stderr could lose thousands of characters to the silent cap and still collapse to under the
    /// marked cap, showing a truncated tail with no ellipsis. Collapsing at capture time means the
    /// retained length is already in the units the display cap compares against.
    /// </summary>
    [Fact]
    public void Mostly_whitespace_stderr_is_retained_in_the_same_units_the_display_cap_measures()
    {
        var tail = new StderrTailBuffer();

        // Each line is one visible token in a wide field of padding — the shape of an indented stack
        // trace or a column-padded table. Raw length is far past the cap; collapsed length is not.
        for (var i = 0; i < 400; i++)
        {
            tail.Append(System.Text.Encoding.UTF8.GetBytes(new string(' ', 40) + $"line{i}\n"));
        }

        var captured = tail.ToTailOrNull();
        Assert.NotNull(captured);

        // No run of whitespace survives, so every retained character counts toward the same budget
        // the classifier's display cap will apply.
        Assert.DoesNotContain("  ", captured);
        Assert.True(
            captured.Length <= CoreDispatcher.MaxRetainedStderrLength,
            $"retained {captured.Length}, cap {CoreDispatcher.MaxRetainedStderrLength}");

        // And the retained content is long enough to reach the display cap, which is what makes the
        // truncation visible rather than silent.
        Assert.True(
            captured.Length > Aer.Flow.Outcomes.OutcomeClassifier.MaxStderrTailInReason,
            "collapsed tail must still exceed the display cap, or the marker never fires");
        Assert.EndsWith("line399", captured);
    }

    /// <summary>
    /// A whitespace run split across a chunk boundary must still collapse to one space. This is the
    /// reason <c>pendingSpace</c> is instance state rather than a local inside the per-chunk decode.
    /// </summary>
    [Fact]
    public void A_whitespace_run_split_across_chunks_collapses_to_a_single_space()
    {
        var tail = new StderrTailBuffer();
        tail.Append(System.Text.Encoding.UTF8.GetBytes("before  "));
        tail.Append(System.Text.Encoding.UTF8.GetBytes("  after"));

        Assert.Equal("before after", tail.ToTailOrNull());
    }

    private static CoreDispatchTarget WriteToStderrAndExit(string message, int exitCode) =>
        OperatingSystem.IsWindows()
            ? new CoreDispatchTarget("cmd", ["/c", $"echo {message} 1>&2 & exit {exitCode}"])
            : new CoreDispatchTarget("sh", ["-c", $"echo {message} >&2; exit {exitCode}"]);

    // Issue #598: an over-long command line is refused by AER, naming its size and the limit, rather
    // than reaching aer-core and coming back as an OS-authored complaint about a filename.

    /// <summary>
    /// Pins the arithmetic the ceiling is compared against, so that a change to the accounting has to
    /// be a deliberate edit here rather than a silent shift in where the guard fires.
    /// </summary>
    [Fact]
    public void MeasureCommandLineLength_counts_the_program_its_arguments_and_their_separators()
    {
        // "prog" quoted (6) + " " + "ab" quoted (4) => 6 + 1 + 4 = 11, and again for the second arg.
        Assert.Equal(6, CoreDispatcher.MeasureCommandLineLength("prog", []));
        Assert.Equal(11, CoreDispatcher.MeasureCommandLineLength("prog", ["ab"]));
        Assert.Equal(16, CoreDispatcher.MeasureCommandLineLength("prog", ["ab", "cd"]));
    }

    /// <summary>
    /// The escape term is what makes the measure an upper bound rather than an approximation, and it
    /// is the whole reason a quote-dense prompt cannot slip past the ceiling into an OS-level failure.
    /// Without it every assertion here would be two characters short per escaped character.
    /// </summary>
    [Fact]
    public void MeasureCommandLineLength_charges_for_what_Windows_escaping_can_add()
    {
        // Same raw length in every case; only the escapable characters differ.
        Assert.Equal(11, CoreDispatcher.MeasureCommandLineLength("prog", ["ab"]));
        Assert.Equal(12, CoreDispatcher.MeasureCommandLineLength("prog", ["a\""]));
        Assert.Equal(12, CoreDispatcher.MeasureCommandLineLength("prog", ["a\\"]));
        Assert.Equal(13, CoreDispatcher.MeasureCommandLineLength("prog", ["\"\""]));

        // The program is charged the same way, not just the arguments.
        Assert.Equal(7, CoreDispatcher.MeasureCommandLineLength("pro\"", []));
    }

    /// <summary>
    /// The case review of #598 found: an argument whose raw characters sit comfortably under the
    /// ceiling but whose escaping pushes it past. Before the measure charged for escaping this was
    /// waved through and failed at the OS instead — a prompt quoting JSON, a schema, or a file's
    /// contents reaches this easily, so it is an ordinary case rather than a pathological one.
    /// </summary>
    [Fact]
    public void GuardCommandLineLength_refuses_an_argument_only_its_escaping_pushes_over()
    {
        const int ceiling = 100;
        var quoteDense = new string('"', 60);

        // Under the ceiling on raw characters alone (60 + 6 == 66), over it once escaping is charged.
        Assert.True(quoteDense.Length + 6 <= ceiling);
        Assert.True(CoreDispatcher.MeasureCommandLineLength("p", [quoteDense]) > ceiling);

        Assert.Throws<CommandLineTooLongException>(
            () => CoreDispatcher.GuardCommandLineLength("p", [quoteDense], ceiling));
    }

    /// <summary>
    /// The ceiling's own doc justifies the number as sitting below <c>CreateProcessW</c>'s documented
    /// maximum. Asserted rather than left to the comment, so raising it past the real limit fails here
    /// instead of silently turning the guard into a formality.
    /// </summary>
    [Fact]
    public void WindowsCommandLineCeiling_stays_below_the_documented_CreateProcessW_maximum()
    {
        Assert.True(
            CoreDispatcher.WindowsCommandLineCeiling < 32_767,
            $"The ceiling ({CoreDispatcher.WindowsCommandLineCeiling}) must stay below CreateProcessW's "
            + "documented 32,767-character lpCommandLine maximum.");
    }

    /// <summary>
    /// The two arms of the boundary, one character apart, which is the pair that makes either arm
    /// mean anything: a guard that throws on everything would pass the first assertion alone, and one
    /// that throws on nothing would pass the second alone.
    /// </summary>
    [Fact]
    public void GuardCommandLineLength_fires_one_character_past_the_ceiling_and_not_at_it()
    {
        const int ceiling = 100;

        // MeasureCommandLineLength("p", [arg]) == arg.Length + 6, so this lands exactly on the ceiling.
        var exactlyAtCeiling = new string('x', ceiling - 6);
        Assert.Equal(ceiling, CoreDispatcher.MeasureCommandLineLength("p", [exactlyAtCeiling]));
        CoreDispatcher.GuardCommandLineLength("p", [exactlyAtCeiling], ceiling);

        var oneOver = exactlyAtCeiling + "x";
        Assert.Equal(ceiling + 1, CoreDispatcher.MeasureCommandLineLength("p", [oneOver]));
        Assert.Throws<CommandLineTooLongException>(
            () => CoreDispatcher.GuardCommandLineLength("p", [oneOver], ceiling));
    }

    /// <summary>
    /// The whole point of the issue is the message, not the throw: an operator who cannot see how big
    /// the prompt was, and how big it was allowed to be, is no better off than with the OS error.
    /// </summary>
    [Fact]
    public void GuardCommandLineLength_names_the_program_the_measured_size_the_ceiling_and_the_longest_argument()
    {
        const int ceiling = 100;
        var longest = new string('x', 200);

        var exception = Assert.Throws<CommandLineTooLongException>(
            () => CoreDispatcher.GuardCommandLineLength("agy", ["-p", longest], ceiling));

        // Anchored to the surrounding words, not bare numbers: three Contains on "213"/"100"/"200"
        // alone would still pass if the message printed the same figures in swapped roles.
        var measured = CoreDispatcher.MeasureCommandLineLength("agy", ["-p", longest])
            .ToString(CultureInfo.InvariantCulture);
        Assert.Contains("'agy'", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"about {measured} characters", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"past the {ceiling.ToString(CultureInfo.InvariantCulture)}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("longest single argument is 200 characters", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard claims a limit only where one was measured (#579's <c>Win32Exception (206)</c>).
    /// Asserted in both directions so that quietly giving POSIX an invented number, or quietly
    /// dropping Windows' real one, both fail here.
    /// </summary>
    [Fact]
    public void PlatformCommandLineCeiling_carries_a_number_on_Windows_and_none_elsewhere()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(CoreDispatcher.WindowsCommandLineCeiling, CoreDispatcher.PlatformCommandLineCeiling);
        }
        else
        {
            Assert.Null(CoreDispatcher.PlatformCommandLineCeiling);
        }
    }

    /// <summary>
    /// The end-to-end arm: the guard is actually wired into <see cref="CoreDispatcher.DispatchAsync"/>
    /// and refuses before aer-core is reached. Windows-only because it is the only platform
    /// <see cref="CoreDispatcher.PlatformCommandLineCeiling"/> claims a limit for -- the boundary
    /// itself is covered on every platform by the tests above, which pass their own ceiling in.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_refuses_an_over_long_command_line_before_spawning()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("No command-line ceiling is claimed off Windows; the boundary is covered by the guard tests.");
        }

        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var oversizedPrompt = new string('x', CoreDispatcher.WindowsCommandLineCeiling + 1_000);

            // "exit 0" would succeed if it ever ran, so a passing assertion here cannot come from the
            // command failing for some unrelated reason of its own.
            var target = new CoreDispatchTarget("cmd", ["/c", "exit 0", oversizedPrompt]);

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var exception = await Assert.ThrowsAsync<CommandLineTooLongException>(
                () => dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken));
            Assert.Contains(
                CoreDispatcher.WindowsCommandLineCeiling.ToString(CultureInfo.InvariantCulture),
                exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            File.Delete(logPath);
        }
    }

    /// <summary>
    /// The control for the test above: the identical target with an ordinary-sized argument dispatches
    /// normally. Without this, a guard that refused every dispatch outright would still look correct.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_dispatches_normally_when_the_command_line_is_within_the_ceiling()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var ordinaryPrompt = new string('x', 1_000);
            var target = OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", "exit 0", ordinaryPrompt])
                : new CoreDispatchTarget("sh", ["-c", "exit 0", ordinaryPrompt]);

            await using var writer = new FlowEventLogWriter(logPath);
            var result = await new CoreDispatcher(writer)
                .DispatchAsync(request, target, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(CoreExitReason.Natural, result.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            File.Delete(logPath);
        }
    }

    /// <summary>
    /// Pins the deliberate ordering: the guard is measured after #292's prompt capture, so the
    /// artifact showing how the prompt got that large survives the refusal. Reversing the two would
    /// withhold the evidence for precisely the failure being reported, and nothing else would notice.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_still_captures_the_prompt_when_the_command_line_guard_fires()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("No command-line ceiling is claimed off Windows, so the guard never fires here.");
        }

        var artifactsRoot = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        var logPath = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, ExecutionId);
            var request = MakeRequest(ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot));
            var oversizedPrompt = new string('x', CoreDispatcher.WindowsCommandLineCeiling + 1_000);
            var target = new CoreDispatchTarget("cmd", ["/c", "exit 0", oversizedPrompt])
                with
            { PromptText = oversizedPrompt };

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer);

            await Assert.ThrowsAsync<CommandLineTooLongException>(
                () => dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken));

            var promptFilePath = Path.Combine(outputDirectory, ArtifactManager.PromptFileName);
            Assert.True(File.Exists(promptFilePath));
            Assert.Equal(
                oversizedPrompt.Length,
                (await File.ReadAllTextAsync(promptFilePath, TestContext.Current.CancellationToken)).Length);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(artifactsRoot);
            File.Delete(logPath);
        }
    }
}
