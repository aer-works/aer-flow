using System.Text.Json;
using Aer.Adapters;
using Aer.Cli.Tests.TestSupport;
using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.Flow.Templates;

namespace Aer.Cli.Tests;

public class StatusCommandEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task Status_of_a_terminal_workflow_reports_one_line_per_step_with_status_and_execution_id()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);

            var finalState = (await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

            var architectExecutionId = finalState.Steps.First(s => s.StepId.Value == "architect").LatestExecutionId!.Value.Value;

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(taskDirectory), output, TestContext.Current.CancellationToken);

            var text = output.ToString();
            Assert.Contains("Workflow status: Terminal", text);
            Assert.Contains($"architect: Succeeded (execution={architectExecutionId})", text);
            Assert.Contains("critic: Succeeded", text);
            Assert.Contains("publisher: Succeeded", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_against_a_nonexistent_task_directory_throws_a_typed_error_and_creates_nothing()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);

            await Assert.ThrowsAsync<SnapshotLoadException>(
                () => StatusCommand.ExecuteAsync(new StatusOptions(taskDirectory), TextWriter.Null, TestContext.Current.CancellationToken));

            Assert.False(Directory.Exists(taskDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_against_an_existing_directory_with_no_snapshot_throws_the_same_typed_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(taskDirectory);

            await Assert.ThrowsAsync<SnapshotLoadException>(
                () => StatusCommand.ExecuteAsync(new StatusOptions(taskDirectory), TextWriter.Null, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_succeeds_while_another_process_holds_the_workflow_lock_and_writes_nothing()
    {
        // The control that actually discriminates: if StatusCommand ever acquired
        // ConcurrencyGuard's lock itself, this call would throw WorkflowLockedException the moment
        // another holder (simulated here) already has it -- exactly the failure a live `aer run`
        // pump would trigger for a real operator running `aer status` alongside it.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);
            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            using var guard = ConcurrencyGuard.Acquire(taskDirectory);
            var filesBefore = Directory.GetFiles(taskDirectory).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(taskDirectory), output, TestContext.Current.CancellationToken);

            var filesAfter = Directory.GetFiles(taskDirectory).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(filesBefore, filesAfter);
            Assert.Contains("Workflow status: Terminal", output.ToString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Follow_on_an_already_terminal_workflow_prints_state_and_exits_without_hanging()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);
            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var output = new StringWriter();
            var statusTask = StatusCommand.ExecuteAsync(
                new StatusOptions(taskDirectory, Follow: true), output, TestContext.Current.CancellationToken);

            var completedFirst = await Task.WhenAny(
                statusTask, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            Assert.True(ReferenceEquals(statusTask, completedFirst), "aer status --follow hung on an already-terminal workflow instead of exiting.");
            await statusTask;

            Assert.Contains("Workflow status: Terminal", output.ToString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Following_a_running_workflow_prints_new_events_as_they_land_and_exits_at_terminal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepDelayedBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);

            var runTask = RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            // Wait for the first *event*, not just the snapshot: a snapshot with zero recorded
            // events projects as WorkflowStatus.Terminal by this codebase's own deliberate,
            // already-tested design (StateProjectorTests.An_all_pending_workflow_projects_WorkflowStatus_Terminal)
            // -- an unstarted task is indistinguishable from a finished one until the pump records
            // its first dispatch. Starting status before that point would make it exit immediately
            // on a correct read, not exercise the tailing loop this test is for.
            var logPath = Path.Combine(taskDirectory, "flow.jsonl");
            while (!File.Exists(logPath) || new FileInfo(logPath).Length == 0)
            {
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            var output = new StringWriter();
            var statusTask = StatusCommand.ExecuteAsync(
                new StatusOptions(taskDirectory, Follow: true), output, TestContext.Current.CancellationToken);

            var completedFirst = await Task.WhenAny(
                statusTask, Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.True(ReferenceEquals(statusTask, completedFirst), "aer status --follow never reached the workflow's terminal state.");
            await statusTask;

            var runResult = await runTask;
            Assert.Equal(WorkflowStatus.Terminal, runResult.State.Status);

            var text = output.ToString();
            Assert.Contains("ExecutionRequestAccepted", text);
            Assert.Contains("ExecutionSucceeded", text);
            Assert.Contains("Workflow status: Terminal", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Cancelling_a_follow_on_a_still_running_workflow_returns_cleanly_instead_of_throwing()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepDelayedBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);

            var runTask = RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var logPath = Path.Combine(taskDirectory, "flow.jsonl");
            while (!File.Exists(logPath) || new FileInfo(logPath).Length == 0)
            {
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            // Cancel while the workflow is still genuinely mid-flight (the first ~900ms step
            // delay has not elapsed yet) -- this is the Ctrl+C/host-stop path the issue's own
            // acceptance criteria name ("exiting when the workflow reaches a terminal state, or on
            // Ctrl-C"), and it must return cleanly rather than let OperationCanceledException
            // escape as a raw, unmapped exception.
            using var followCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            followCancellation.CancelAfter(TimeSpan.FromMilliseconds(300));

            var output = new StringWriter();
            var statusTask = StatusCommand.ExecuteAsync(new StatusOptions(taskDirectory, Follow: true), output, followCancellation.Token);

            var completedFirst = await Task.WhenAny(statusTask, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.True(ReferenceEquals(statusTask, completedFirst), "Cancelling aer status --follow did not return promptly.");

            var exception = await Record.ExceptionAsync(() => statusTask);
            Assert.Null(exception);

            await runTask;
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<string> WriteThreeStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("three-step-linear"),
            1,
            [
                new WorkflowStepDefinition(new StepId("architect"), "architect", [], ["plan"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("critic"), "critic", ["plan"], ["review"], [new StepId("architect")], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("publisher"), "publisher", ["review"], ["summary"], [new StepId("critic")], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteThreeStepBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                WriteFileCommand("plan", "the-plan"),
                TimeSpan.FromSeconds(30)),
            ["critic"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                CopyFirstInputCommand("review"),
                TimeSpan.FromSeconds(30)),
            ["publisher"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                CopyFirstInputCommand("summary"),
                TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    /// <summary>
    /// The same three-step chain as <see cref="WriteThreeStepBindingsAsync"/>, with each step's
    /// shell command padded with a ~1s delay before it writes its output — enough of a window for
    /// a concurrently-running <c>aer status --follow</c> poll (500ms) to observe at least one
    /// intermediate, non-terminal state rather than only the final one.
    /// </summary>
    private static async Task<string> WriteThreeStepDelayedBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                DelayedWriteFileCommand("plan", "the-plan"),
                TimeSpan.FromSeconds(30)),
            ["critic"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                DelayedCopyFirstInputCommand("review"),
                TimeSpan.FromSeconds(30)),
            ["publisher"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                DelayedCopyFirstInputCommand("summary"),
                TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "delayed-bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) => OperatingSystem.IsWindows()
        ? $"echo {content}>%AER_OUTPUT_DIR%\\{outputName}"
        : $"echo {content} > \"$AER_OUTPUT_DIR/{outputName}\"";

    private static string CopyFirstInputCommand(string outputName) => OperatingSystem.IsWindows()
        ? $"type %AER_INPUT_0% >%AER_OUTPUT_DIR%\\{outputName}"
        : $"cat \"$AER_INPUT_0\" > \"$AER_OUTPUT_DIR/{outputName}\"";

    // Not `ping -n 2 127.0.0.1`: it depends on ICMP/loopback networking actually being reachable
    // from the spawned worker process, which is not guaranteed in every sandboxed environment --
    // if it fails instantly instead of delaying, `&` (not `&&`) runs the write step immediately
    // anyway, silently collapsing the whole window this test relies on. `Start-Sleep` has no such
    // external dependency, and deliberately unquoted: a quoted `-Command "..."` argument nested
    // inside `cmd /c "<this whole string>"` measured at 385ms elapsed for a 900ms sleep -- cmd's
    // handling of embedded quotes inside an already-quoted /c argument is fragile enough that the
    // sleep silently never ran. Unquoted (PowerShell's -Command already takes the remaining
    // arguments verbatim), it measures the full ~900ms+ it should.
    private static string DelayedWriteFileCommand(string outputName, string content) => OperatingSystem.IsWindows()
        ? $"powershell -NoProfile -Command Start-Sleep -Milliseconds 900 & echo {content}>%AER_OUTPUT_DIR%\\{outputName}"
        : $"sleep 1 && echo {content} > \"$AER_OUTPUT_DIR/{outputName}\"";

    private static string DelayedCopyFirstInputCommand(string outputName) => OperatingSystem.IsWindows()
        ? $"powershell -NoProfile -Command Start-Sleep -Milliseconds 900 & type %AER_INPUT_0% >%AER_OUTPUT_DIR%\\{outputName}"
        : $"sleep 1 && cat \"$AER_INPUT_0\" > \"$AER_OUTPUT_DIR/{outputName}\"";
}
