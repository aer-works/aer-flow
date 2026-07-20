using System.Text.Json;
using Aer.Adapters;
using Aer.Cli.Tests.TestSupport;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Flow.Templates;

namespace Aer.Cli.Tests;

/// <summary>
/// <c>aer supply</c> (M12 Phase 3, issue #97) exercised on its own: minting, populating, and
/// settling a step-less supplementary execution in one call, ahead of
/// <see cref="DecideCommandEndToEndTests"/>'s full supply → decide round trips.
/// </summary>
public class SupplyCommandEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task Supplying_an_artifact_mints_populates_and_settles_it_in_one_call()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-supply-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteSingleStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteSingleStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);
            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var sourceFilePath = Path.Combine(testRoot, "revision.txt");
            await File.WriteAllTextAsync(sourceFilePath, "the-revision", TestContext.Current.CancellationToken);
            var supplyOptions = new SupplyOptions(taskDirectory, "human", "revision", sourceFilePath, bindingsFilePath);

            var result = await SupplyCommand.ExecuteAsync(supplyOptions, Adapters, TestContext.Current.CancellationToken);

            Assert.Empty(result.Command.State.StepLessExecutions);
            var reader = new FlowEventLogReader(Path.Combine(taskDirectory, "flow.jsonl"));
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.ExecutionSucceeded succeeded && succeeded.ExecutionId == result.ExecutionId);

            var artifactsRoot = Path.Combine(taskDirectory, "artifacts");
            var outputPath = Path.Combine(artifactsRoot, $"execution_{result.ExecutionId}", "revision");
            Assert.Equal("the-revision", (await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task A_missing_source_file_throws_before_minting_anything()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-supply-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteSingleStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteSingleStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, taskDirectory);
            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var missingSourcePath = Path.Combine(testRoot, "does-not-exist.txt");
            var supplyOptions = new SupplyOptions(taskDirectory, "human", "revision", missingSourcePath, bindingsFilePath);

            await Assert.ThrowsAsync<FileNotFoundException>(() => SupplyCommand.ExecuteAsync(supplyOptions, Adapters, TestContext.Current.CancellationToken));

            var reader = new FlowEventLogReader(Path.Combine(taskDirectory, "flow.jsonl"));
            Assert.DoesNotContain(await reader.ReadAllAsync(TestContext.Current.CancellationToken), e => e is FlowEvent.ExecutionRequestAccepted accepted && accepted.Request.StepId is null);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Supplying_against_a_task_directory_with_no_snapshot_throws_a_typed_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-supply-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);
            var bindingsFilePath = await WriteSingleStepBindingsAsync(testRoot);
            var sourceFilePath = Path.Combine(testRoot, "revision.txt");
            await File.WriteAllTextAsync(sourceFilePath, "the-revision", TestContext.Current.CancellationToken);
            var supplyOptions = new SupplyOptions(taskDirectory, "human", "revision", sourceFilePath, bindingsFilePath);

            await Assert.ThrowsAsync<SnapshotLoadException>(() => SupplyCommand.ExecuteAsync(supplyOptions, Adapters, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task<string> WriteSingleStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("single-step"),
            1,
            [new WorkflowStepDefinition(new StepId("a"), "a", [], ["out_a"], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteSingleStepBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                WriteFileCommand("out_a", "a-out"), TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) => OperatingSystem.IsWindows()
        ? $"echo {content}>%AER_OUTPUT_DIR%\\{outputName}"
        : $"echo {content} > \"$AER_OUTPUT_DIR/{outputName}\"";
}
