using Aer.Adapters;
using Aer.Flow.Artifacts;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Templates;

namespace Aer.Cli;

/// <summary>
/// <c>aer supply</c> (M12 Phase 3): the CLI surface for §17.3's supplementary artifact — the one
/// mutation-interface entry point (<see cref="MutationInterface.RecordSupplementaryExecutionAsync"/>)
/// no CLI command reached before this phase. Per M11's decision of record that worker-binding
/// config entries only ever resolve to <see cref="Aer.Flow.Mutation.WorkerBinding.Process"/>, the
/// <see cref="Aer.Flow.Mutation.WorkerBinding.NonProcess"/> binding this command dispatches under is
/// constructed directly here, from <see cref="SupplyOptions.OutputName"/> — not looked up in the
/// bindings file. Minting alone does not drive the pump (§17.3: nothing about minting changes
/// readiness), so this command populates the assigned output immediately from
/// <see cref="SupplyOptions.SourceFilePath"/> and then runs one settling pump
/// (<see cref="MutationInterface.StartWorkflowAsync"/>) itself — the same two-call sequence
/// <c>PauseDecisionSupersedeHumanEndToEndTests</c> exercises directly against
/// <c>MutationInterface</c> — so the printed <see cref="ExecutionId"/> is already
/// <see cref="FlowEvent.ExecutionSucceeded"/> by the time this command returns, ready to hand
/// straight to <c>aer decide --supplementary</c>.
/// </summary>
public static class SupplyCommand
{
    private const string SnapshotFileName = "snapshot.json";
    private const string LogFileName = "flow.jsonl";
    private const string ArtifactsDirectoryName = "artifacts";

    /// <exception cref="SnapshotLoadException">
    /// The task directory has no persisted snapshot yet (never started via <c>aer run</c>), or its
    /// persisted snapshot is malformed.
    /// </exception>
    /// <exception cref="WorkerBindingConfigException">The worker-binding config is malformed.</exception>
    /// <exception cref="UnknownWorkerAdapterException">
    /// The worker-binding config names an adapter not present in <paramref name="adapters"/>.
    /// </exception>
    /// <exception cref="FileNotFoundException"><see cref="SupplyOptions.SourceFilePath"/> does not exist.</exception>
    /// <exception cref="Aer.Flow.Concurrency.WorkflowLockedException">
    /// Another Flow instance already holds this task directory's lock.
    /// </exception>
    public static async Task<SupplyResult> ExecuteAsync(
        SupplyOptions options,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);

        if (!File.Exists(options.SourceFilePath))
        {
            throw new FileNotFoundException($"Source file '{options.SourceFilePath}' does not exist.", options.SourceFilePath);
        }

        var snapshotPath = Path.Combine(options.TaskDirectoryPath, SnapshotFileName);
        var logPath = Path.Combine(options.TaskDirectoryPath, LogFileName);
        var artifactsRootPath = Path.Combine(options.TaskDirectoryPath, ArtifactsDirectoryName);

        if (!File.Exists(snapshotPath))
        {
            throw new SnapshotLoadException(
                $"Task directory '{options.TaskDirectoryPath}' has no bound snapshot — 'aer supply' " +
                "targets a task 'aer run' has already started, and never binds one fresh.");
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);

        var bindingConfig = await WorkerBindingConfigParser.LoadFromFileAsync(options.BindingsFilePath, cancellationToken)
            .ConfigureAwait(false);
        var profiles = await AerProfileStore.LoadAsync(AerProfileStore.DefaultPath, cancellationToken).ConfigureAwait(false);
        var workerBindings = new Dictionary<string, WorkerBinding>(WorkerBindingResolver.Resolve(
            bindingConfig, adapters, profiles, Path.GetDirectoryName(options.BindingsFilePath)));

        var contract = new WorkerContract(options.Worker, RequiredInputs: [], [new ProducedOutput(options.OutputName)], OptionalMetadata: []);
        workerBindings[options.Worker] = new WorkerBinding.NonProcess(contract);

        var workflowId = new WorkflowId(options.WorkflowId ?? snapshot.WorkflowTemplateId.Value);

        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);
        var dispatcher = new CoreDispatcher(writer);

        var (_, executionId) = await MutationInterface.RecordSupplementaryExecutionAsync(
                workflowId, options.TaskDirectoryPath, snapshot, workerBindings, artifactsRootPath,
                options.Worker, inputs: [], reader, writer, cancellationToken)
            .ConfigureAwait(false);

        var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, executionId);
        File.Copy(options.SourceFilePath, Path.Combine(outputDirectory, options.OutputName), overwrite: true);

        var settledState = await MutationInterface.StartWorkflowAsync(
                workflowId, options.TaskDirectoryPath, snapshot, workerBindings, artifactsRootPath,
                reader, writer, dispatcher, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new SupplyResult(executionId, new CommandResult(settledState, snapshot));
    }
}

/// <param name="ExecutionId">The minted supplementary execution's id — pass to <c>aer decide --supplementary</c>.</param>
/// <param name="Command">The settling pump's resulting state, reported the same way as any other command.</param>
public sealed record SupplyResult(ExecutionId ExecutionId, CommandResult Command);
