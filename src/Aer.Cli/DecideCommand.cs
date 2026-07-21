using Aer.Adapters;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Templates;

namespace Aer.Cli;

/// <summary>
/// <c>aer decide</c> (M12 Phase 3): exposes <see cref="MutationInterface.RecordDecisionAsync"/> on
/// the CLI — UI spec §7's reference mapping made real. The vocabulary is exactly §17.2's closed set
/// (<see cref="Domain.DecisionType"/>); every validity rule (which options a given type requires or
/// forbids) stays <c>ExternalDecisionValidator</c>'s, never re-implemented here. Like
/// <see cref="CancelCommand"/>, this never binds a fresh snapshot, and recording a decision resumes
/// the workflow — <c>ExternalDecisionRecorded</c> + <c>WorkflowResumed</c>, then the pump to a fixed
/// point — so this command blocks and reports exactly like <c>aer run</c>.
/// </summary>
public static class DecideCommand
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
    /// <exception cref="InvalidExternalDecisionException">The decision violates one of §17.2's rules.</exception>
    /// <exception cref="Aer.Flow.Concurrency.WorkflowLockedException">
    /// Another Flow instance already holds this task directory's lock.
    /// </exception>
    /// <param name="inFlightExecutions">
    /// M15 Phase 4's (issue #140) additive caller-retained delivery point — see
    /// <see cref="RunCommand.ExecuteAsync"/>'s own remarks; forwarded, unchanged, to
    /// <see cref="MutationInterface.RecordDecisionAsync"/>.
    /// </param>
    /// <param name="onWorkerStdoutLine">
    /// M24 Phase 1's live in-turn streaming — forwarded verbatim to <see cref="WorkerBindingResolver.Resolve"/>.
    /// Null for the real <c>aer decide</c> CLI entry point; only <c>Aer.Daemon</c>'s in-process
    /// session-turn path supplies one (see <c>Program.ExecuteSessionTurnAsync</c>).
    /// </param>
    public static async Task<CommandResult> ExecuteAsync(
        DecideOptions options,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        InFlightExecutionRegistry? inFlightExecutions = null,
        CancellationToken cancellationToken = default,
        Action<string, string>? onWorkerStdoutLine = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);

        var snapshotPath = Path.Combine(options.TaskDirectoryPath, SnapshotFileName);
        var logPath = Path.Combine(options.TaskDirectoryPath, LogFileName);
        var artifactsRootPath = Path.Combine(options.TaskDirectoryPath, ArtifactsDirectoryName);

        if (!File.Exists(snapshotPath))
        {
            throw new SnapshotLoadException(
                $"Task directory '{options.TaskDirectoryPath}' has no bound snapshot — 'aer decide' " +
                "targets a task 'aer run' has already started, and never binds one fresh.");
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);

        var bindingConfig = await WorkerBindingConfigParser.LoadFromFileAsync(options.BindingsFilePath, cancellationToken)
            .ConfigureAwait(false);
        var profiles = await AerProfileStore.LoadAsync(AerProfileStore.DefaultPath, cancellationToken).ConfigureAwait(false);
        var workerBindings = WorkerBindingResolver.Resolve(
            bindingConfig, adapters, profiles, Path.GetDirectoryName(options.BindingsFilePath), onWorkerStdoutLine);

        var workflowId = new WorkflowId(options.WorkflowId ?? snapshot.WorkflowTemplateId.Value);
        var referencedExecutionId = new ExecutionId(options.ExecutionId);
        var supplementaryExecutionId = options.SupplementaryExecutionId is { } id ? new ExecutionId(id) : (ExecutionId?)null;

        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);
        var dispatcher = new CoreDispatcher(writer);

        var state = await MutationInterface.RecordDecisionAsync(
                workflowId,
                options.TaskDirectoryPath,
                snapshot,
                workerBindings,
                artifactsRootPath,
                reader,
                writer,
                dispatcher,
                referencedExecutionId,
                options.DecisionType,
                options.TargetStepId,
                supplementaryExecutionId,
                inFlightExecutions,
                cancellationToken)
            .ConfigureAwait(false);

        return new CommandResult(state, snapshot);
    }
}
