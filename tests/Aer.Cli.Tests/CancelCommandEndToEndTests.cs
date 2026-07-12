using System.Text.Json;
using Aer.Adapters;
using Aer.Cli.Tests.TestSupport;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Templates;

namespace Aer.Cli.Tests;

public class CancelCommandEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task Cancelling_an_already_succeeded_execution_is_a_too_late_no_op_reported_as_success()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);

            var finalState = await RunCommand.ExecuteAsync(runOptions, Adapters);
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

            var architectExecutionId = finalState.Steps.First(s => s.StepId.Value == "architect").LatestExecutionId;
            Assert.NotNull(architectExecutionId);

            var cancelOptions = new CancelOptions(taskDirectory, architectExecutionId.Value.Value, bindingsFilePath);
            var canceledState = await CancelCommand.ExecuteAsync(cancelOptions, Adapters);

            Assert.Equal(WorkflowStatus.Terminal, canceledState.Status);
            Assert.All(canceledState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));

            var reader = new FlowEventLogReader(Path.Combine(taskDirectory, "flow.jsonl"));
            var events = await reader.ReadAllAsync();
            var cancellationEvents = events.OfType<FlowEvent.CancellationRequested>().ToList();
            Assert.Single(cancellationEvents);
            Assert.Equal(architectExecutionId.Value, cancellationEvents[0].ExecutionId);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Cancelling_against_a_task_directory_with_no_snapshot_throws_a_typed_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var cancelOptions = new CancelOptions(taskDirectory, "exec-1", bindingsFilePath);

            await Assert.ThrowsAsync<SnapshotLoadException>(
                () => CancelCommand.ExecuteAsync(cancelOptions, Adapters));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Cancelling_an_unknown_execution_id_throws_a_typed_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);

            await RunCommand.ExecuteAsync(runOptions, Adapters);

            var cancelOptions = new CancelOptions(taskDirectory, "not-a-real-execution-id", bindingsFilePath);
            await Assert.ThrowsAsync<UnknownExecutionIdException>(
                () => CancelCommand.ExecuteAsync(cancelOptions, Adapters));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task A_malformed_bindings_file_throws_a_typed_config_exception()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);

            await RunCommand.ExecuteAsync(runOptions, Adapters);

            var malformedBindingsPath = Path.Combine(testRoot, "malformed.json");
            await File.WriteAllTextAsync(malformedBindingsPath, "{ not valid json");
            var cancelOptions = new CancelOptions(taskDirectory, "whatever", malformedBindingsPath);

            await Assert.ThrowsAsync<WorkerBindingConfigException>(
                () => CancelCommand.ExecuteAsync(cancelOptions, Adapters));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
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

    private static string WriteFileCommand(string outputName, string content) => OperatingSystem.IsWindows()
        ? $"echo {content}>%AER_OUTPUT_DIR%\\{outputName}"
        : $"echo {content} > \"$AER_OUTPUT_DIR/{outputName}\"";

    private static string CopyFirstInputCommand(string outputName) => OperatingSystem.IsWindows()
        ? $"type %AER_INPUT_0% >%AER_OUTPUT_DIR%\\{outputName}"
        : $"cat \"$AER_INPUT_0\" > \"$AER_OUTPUT_DIR/{outputName}\"";
}
