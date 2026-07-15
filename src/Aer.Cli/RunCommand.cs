using Aer.Adapters;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Templates;

namespace Aer.Cli;

/// <summary>
/// <c>aer run</c>, the "pump" §21 designates as v1's execution driver (M11 Phase 3): the exact
/// project → resolve → dispatch → await loop <c>WorkflowEndToEndTests</c> has exercised since M7,
/// now reached through a real <see cref="IWorkerAdapter"/> and a real host process instead of a
/// test fixture constructing <see cref="WorkerBinding"/>s by hand.
/// </summary>
public static class RunCommand
{
    private const string SnapshotFileName = "snapshot.json";
    private const string LogFileName = "flow.jsonl";
    private const string ArtifactsDirectoryName = "artifacts";

    /// <summary>
    /// Parses the workflow template and worker-binding config (loading the already-bound snapshot
    /// instead, when <paramref name="options"/>'s task directory already has one — a resumed run,
    /// not a fresh one), resolves <paramref name="adapters"/> into <see cref="WorkerBinding"/>s, and
    /// runs the single mutation surface to a terminal state.
    /// </summary>
    /// <exception cref="CliArgumentException">
    /// <paramref name="options"/>'s <c>TaskDirectoryPath</c> has no persisted snapshot yet (a fresh
    /// start) and no <c>WorkflowFilePath</c> was given to bind one from.
    /// </exception>
    /// <exception cref="WorkflowDefinitionValidationException">The workflow template is malformed or invalid.</exception>
    /// <exception cref="SnapshotLoadException">The task directory's persisted snapshot is malformed.</exception>
    /// <exception cref="WorkerBindingConfigException">The worker-binding config is malformed.</exception>
    /// <exception cref="UnknownWorkerAdapterException">
    /// The worker-binding config names an adapter not present in <paramref name="adapters"/>.
    /// </exception>
    /// <exception cref="Aer.Flow.Concurrency.WorkflowLockedException">
    /// Another Flow instance already holds this task directory's lock.
    /// </exception>
    /// <param name="inFlightExecutions">
    /// M15 Phase 4's (issue #140) additive caller-retained delivery point — forwarded, unchanged, to
    /// <see cref="MutationInterface.StartWorkflowAsync"/>. <c>null</c> for every caller (the CLI
    /// included) that has no need to reach a live execution mid-pump; <c>Aer.Ui</c>'s
    /// <c>MainWindow</c> is the first caller that retains one, so a targeted Cancel from the UI can
    /// signal a specific in-flight execution this same call dispatched, without a second
    /// mutation-surface call racing §15's guard.
    /// </param>
    public static async Task<CommandResult> ExecuteAsync(
        RunOptions options,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        InFlightExecutionRegistry? inFlightExecutions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);

        Directory.CreateDirectory(options.TaskDirectoryPath);

        var snapshotPath = Path.Combine(options.TaskDirectoryPath, SnapshotFileName);
        var logPath = Path.Combine(options.TaskDirectoryPath, LogFileName);
        var artifactsRootPath = Path.Combine(options.TaskDirectoryPath, ArtifactsDirectoryName);

        var snapshot = File.Exists(snapshotPath)
            ? await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false)
            : await BindAndPersistAsync(RequireWorkflowFilePath(options), snapshotPath, cancellationToken).ConfigureAwait(false);

        var bindingConfig = await WorkerBindingConfigParser.LoadFromFileAsync(options.BindingsFilePath, cancellationToken)
            .ConfigureAwait(false);
        var workerBindings = WorkerBindingResolver.Resolve(bindingConfig, adapters);

        var workflowId = new WorkflowId(options.WorkflowId ?? snapshot.WorkflowTemplateId.Value);

        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);
        var dispatcher = new CoreDispatcher(writer);

        var state = await MutationInterface.StartWorkflowAsync(
                workflowId,
                options.TaskDirectoryPath,
                snapshot,
                workerBindings,
                artifactsRootPath,
                reader,
                writer,
                dispatcher,
                inFlightExecutions,
                cancellationToken)
            .ConfigureAwait(false);

        return new CommandResult(state, snapshot);
    }

    /// <summary>
    /// A fresh start with no <see cref="RunOptions.WorkflowFilePath"/> is a caller error, not a
    /// silent no-op — there is nothing to bind a snapshot from (M15 Phase 1, issue #137).
    /// </summary>
    private static string RequireWorkflowFilePath(RunOptions options) => options.WorkflowFilePath
        ?? throw new CliArgumentException(
            $"Task directory '{options.TaskDirectoryPath}' has no bound snapshot yet, and no workflow " +
            "template file was given to start one fresh.");

    private static async Task<WorkflowDefinitionSnapshot> BindAndPersistAsync(
        string workflowFilePath, string snapshotPath, CancellationToken cancellationToken)
    {
        var definition = await WorkflowDefinitionParser.LoadFromFileAsync(workflowFilePath, cancellationToken).ConfigureAwait(false);
        var snapshot = SnapshotBinder.Bind(definition);
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }
}
