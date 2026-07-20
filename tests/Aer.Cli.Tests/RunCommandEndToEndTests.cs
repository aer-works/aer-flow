using System.Text.Json;
using Aer.Adapters;
using Aer.Cli.Tests.TestSupport;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Flow.Templates;

namespace Aer.Cli.Tests;

/// <summary>
/// M11 Phase 3's completion gate: the project → resolve → dispatch → await loop
/// <c>Aer.Flow.Tests.EndToEnd.WorkflowEndToEndTests</c> has exercised since M7, now reached through
/// <c>RunCommand.ExecuteAsync</c> — the exact call <c>Program.cs</c> makes — with a real
/// <see cref="IWorkerAdapter"/> resolving a real worker-binding config file, not a
/// <see cref="Aer.Flow.Mutation.WorkerBinding"/> constructed directly by the test. The shell-stub
/// adapter (<see cref="ShellCommandWorkerAdapter"/>) keeps every dispatch CI-safe while still
/// going through the real aer-core M5 binding, same as <c>WorkflowEndToEndTests</c> itself.
/// </summary>
public class RunCommandEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task A_three_step_linear_workflow_runs_to_completion_through_RunCommand()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);

            var finalState = (await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(3, finalState.Steps.Count);
            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));

            var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
            var stepStateById = finalState.Steps.ToDictionary(s => s.StepId);
            await AssertOutputAsync(artifactsRoot, stepStateById[new StepId("architect")], "plan", "the-plan");
            await AssertOutputAsync(artifactsRoot, stepStateById[new StepId("critic")], "review", "the-plan");
            await AssertOutputAsync(artifactsRoot, stepStateById[new StepId("publisher")], "summary", "the-plan");

            // WorkflowId defaults to the bound snapshot's WorkflowTemplateId when not given.
            var reader = new FlowEventLogReader(Path.Combine(taskDirectory, "flow.jsonl"));
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var requests = events.OfType<FlowEvent.ExecutionRequestAccepted>().Select(e => e.Request).ToList();
            Assert.Equal(3, requests.Count);
            Assert.All(requests, request => Assert.Equal("three-step-linear", request.WorkflowId.Value));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Running_again_against_the_same_task_directory_resumes_without_redispatching()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);

            var firstRun = (await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
            Assert.All(firstRun.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));

            var logPath = Path.Combine(taskDirectory, "flow.jsonl");
            var eventCountAfterFirstRun = (await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken)).Count;

            var secondRun = (await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;

            Assert.Equal(WorkflowStatus.Terminal, secondRun.Status);
            Assert.All(secondRun.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));

            var eventCountAfterSecondRun = (await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken)).Count;
            Assert.Equal(eventCountAfterFirstRun, eventCountAfterSecondRun);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task A_malformed_workflow_file_throws_a_typed_validation_exception()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var workflowFilePath = Path.Combine(testRoot, "workflow.json");
            await File.WriteAllTextAsync(workflowFilePath, "{ not valid json", TestContext.Current.CancellationToken);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, Path.Combine(testRoot, "task"));

            await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(
                () => RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken));
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
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = Path.Combine(testRoot, "bindings.json");
            await File.WriteAllTextAsync(bindingsFilePath, "{ not valid json", TestContext.Current.CancellationToken);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, Path.Combine(testRoot, "task"));

            await Assert.ThrowsAsync<WorkerBindingConfigException>(() => RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task A_bindings_entry_naming_an_unregistered_adapter_throws()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = Path.Combine(testRoot, "bindings.json");
            var config = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["architect"] = new WorkerBindingConfigEntry(
                    "not-registered",
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    "irrelevant",
                    TimeSpan.FromSeconds(30)),
            };
            await File.WriteAllTextAsync(bindingsFilePath, JsonSerializer.Serialize(config), TestContext.Current.CancellationToken);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, Path.Combine(testRoot, "task"));

            await Assert.ThrowsAsync<UnknownWorkerAdapterException>(() => RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken));
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

    private static async Task AssertOutputAsync(string artifactsRoot, StepState stepState, string outputName, string expectedContent)
    {
        var outputPath = Path.Combine(artifactsRoot, $"execution_{stepState.LatestExecutionId}", outputName);
        Assert.True(File.Exists(outputPath));
        Assert.Equal(expectedContent, (await File.ReadAllTextAsync(outputPath)).Trim());
    }
}
