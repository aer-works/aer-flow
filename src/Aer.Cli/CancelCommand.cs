using Aer.Adapters;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Templates;

namespace Aer.Cli;

/// <summary>
/// <c>aer cancel</c> (M12 Phase 2): exposes <see cref="MutationInterface.RequestCancellationAsync"/>
/// on the CLI. Unlike <see cref="RunCommand"/>, this never binds a fresh snapshot — §11.2's rule that
/// mutation commands only ever act against a task <c>aer run</c> has already started — and, like
/// every mutation entry point, is itself a pump: recording the cancellation intent resumes driving
/// the rest of the workflow to its next fixed point (§21).
/// </summary>
public static class CancelCommand
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
    /// <exception cref="Aer.Flow.Mutation.UnknownExecutionIdException">
    /// <paramref name="options"/>'s <c>ExecutionId</c> was never admitted for execution.
    /// </exception>
    /// <exception cref="Aer.Flow.Concurrency.WorkflowLockedException">
    /// Another Flow instance already holds this task directory's lock — most likely a live
    /// <c>aer run</c> pump; see that exception's message for how to reach an in-flight execution
    /// instead.
    /// </exception>
    public static async Task<CommandResult> ExecuteAsync(
        CancelOptions options,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);

        var snapshotPath = Path.Combine(options.TaskDirectoryPath, SnapshotFileName);
        var logPath = Path.Combine(options.TaskDirectoryPath, LogFileName);
        var artifactsRootPath = Path.Combine(options.TaskDirectoryPath, ArtifactsDirectoryName);

        if (!File.Exists(snapshotPath))
        {
            throw new SnapshotLoadException(
                $"Task directory '{options.TaskDirectoryPath}' has no bound snapshot — 'aer cancel' " +
                "targets a task 'aer run' has already started, and never binds one fresh.");
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);

        var bindingConfig = await WorkerBindingConfigParser.LoadFromFileAsync(options.BindingsFilePath, cancellationToken)
            .ConfigureAwait(false);
        var profiles = await AerProfileStore.LoadAsync(AerProfileStore.DefaultPath, cancellationToken).ConfigureAwait(false);
        var workerBindings = WorkerBindingResolver.Resolve(
            bindingConfig, adapters, profiles, Path.GetDirectoryName(options.BindingsFilePath));

        var workflowId = new WorkflowId(options.WorkflowId ?? snapshot.WorkflowTemplateId.Value);
        var targetExecutionId = new ExecutionId(options.ExecutionId);

        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);
        var dispatcher = new CoreDispatcher(writer);

        var state = await MutationInterface.RequestCancellationAsync(
                workflowId,
                options.TaskDirectoryPath,
                snapshot,
                workerBindings,
                artifactsRootPath,
                reader,
                writer,
                dispatcher,
                targetExecutionId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new CommandResult(state, snapshot);
    }
}
