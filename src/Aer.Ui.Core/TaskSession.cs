using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.IO;
using Aer.Adapters;
using Aer.Cli;
using Aer.Flow;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;

namespace Aer.Ui.Core;

public record OpenTaskRequest(string DirectoryPath);
public record RunTaskRequest(string DirectoryPath, string? WorkflowTemplateFilePath, string BindingsFilePath);
public record ArtifactReference(string ExecutionId, string FileName);

public record DecideTaskRequest(
    string DirectoryPath,
    string StepId,
    string ExecutionId,
    DecisionType DecisionType,
    string? TargetStepId = null,
    string? RevisionFilePath = null,
    string? SupplementaryWorker = null,
    string? SupplementaryOutputName = null,
    ArtifactReference? ArtifactReference = null);

public record RunTemplateRequest(
    string TemplateId,
    string? PrimaryAdapter = null,
    string? SecondaryAdapter = null,
    string? TaskName = null,
    string? CustomPrompt = null,
    string? SecondaryCustomPrompt = null);

public record CancelTaskRequest(string DirectoryPath, string? ExecutionId = null);

/// <summary>M24 Phase 5 (#278): the request body shape shared by <c>/api/tasks/archive</c>, <c>/api/tasks/unarchive</c>, and <c>/api/tasks/delete</c>.</summary>
public record TaskDirectoryRequest(string DirectoryPath);
public record DaemonVersionInfo(string Version, bool HasRunningTasks, bool IsRemote = false);

public class BindingsPathHolder
{
    public string? BindingsFilePath { get; set; }
}

/// <summary>
/// One task-facing session's orchestration (M19 Phase 2, issue #187), updated in M20 Phase 2/3 to
/// support client-first daemonization: connects to Aer.Daemon background host process via REST/WebSockets
/// to execute pumps and stream real-time task projections. Falls back to in-process execution seamlessly
/// if the daemon cannot be reached or started. Enforces global mutex single-instance checks, 
/// local auth tokens, process supervision, and version-skew protection.
/// </summary>
public sealed partial class TaskSession
{
    // Partial split (#426, no behaviour change): this file holds the shell (shared connection/
    // client state + the constructor) and the *per-session core* — SetCurrentTaskDirectory /
    // LoadAsync / RunAsync / DecideAsync / CancelExecutionAsync and the ShouldApplyProjectionPush /
    // UpdateProjection / Rebuild* projection helpers. Peripheral clusters live in
    // TaskSession.{Connection,Sessions,Fleet,Remote,Persistence}.cs — same partial class, same
    // fields.
    //
    // #426 named a triplet here (CurrentTaskDirectoryPath, CurrentPumpTask,
    // _currentInFlightExecutions) as the surface #335 would lift into a per-session type. #335 has
    // landed and did exactly that for the *host* half: the registry, stop source and pump task now
    // live in HostedRun, keyed by session directory in _hostedRuns, so the daemon holds as many as
    // it is running. CurrentTaskDirectoryPath deliberately stayed single-valued — it is the
    // *client's* idea of what it is looking at, and desktop multi-session is #336's switcher.

    /// <summary>The outcome one load produces: exactly one of the two is non-null (§3's honest-error rule — an invalid directory is a rendered message, never a crash).</summary>
    public sealed record LoadOutcome(TaskProjection? Projection, string? ErrorMessage);

    /// <summary>The outcome one template load produces — <see cref="LoadOutcome"/>'s counterpart for a raw, not-yet-instantiated template file (M14 Phase 3).</summary>
    public sealed record TemplateLoadOutcome(Aer.Flow.Domain.WorkflowDefinition? Definition, string? ErrorMessage);

    /// <summary>Null on success; the in-window message otherwise (the M14 Phase 1 precedent: a GUI has no stderr/exit-code convention to fail into).</summary>
    public sealed record MutationOutcome(string? ErrorMessage);

    /// <summary><see cref="LoadOutcome"/>'s counterpart for <see cref="StartInteractiveSessionAsync"/> (M24 Phase 1 desktop wiring, issue #262).</summary>
    public sealed record SessionStartOutcome(SessionMetadata? Metadata, string? ErrorMessage);

    /// <summary><c>GET /api/sessions/{id}/commands</c>'s shape (M24 Phase 2 follow-up) — <see cref="WorkerCapabilities"/>'s own fields plus the additive <see cref="RecentlyUsed"/> sibling.</summary>
    public sealed record SessionCommandsResult(string Vendor, IReadOnlyList<WorkerCapabilityItem> Items, IReadOnlyList<string> Models, IReadOnlyList<string> RecentlyUsed);

    private readonly LocalUiConfigurationStore _configurationStore;
    private readonly IReadOnlyDictionary<string, IWorkerAdapter> _adapters;
    private readonly Func<string?> _bindingsFilePathProvider;
    private readonly Action _mutationStarted;
    private readonly Action _mutationFailed;
    private readonly Func<string, CancellationToken, Task> _reopenTaskAsync;
    private readonly Action<TaskProjection, string>? _onProjectionUpdated;
    private readonly string? _daemonUrl;

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly SynchronizationContext? _syncContext = SynchronizationContext.Current;

    private bool _isClientMode;
    private string? _activeDaemonUrl;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _wsCts;
    private ClientWebSocket? _progressWebSocket;
    private CancellationTokenSource? _progressWsCts;

    /// <summary>
    /// The client-side consumer for <c>/api/ws/progress</c> (M24 Phase 1's live in-turn streaming,
    /// issue #262) -- previously nothing subscribed to this socket at all, so a session's live
    /// <c>WorkerProgressEvent</c>s were broadcast into the void. Fires on the same
    /// <see cref="SynchronizationContext"/> marshaling <see cref="ReceiveWebSocketDataAsync"/>
    /// already uses for projection pushes. <c>DirectoryPath</c>/<c>StepId</c> are carried alongside
    /// the event exactly as the daemon broadcasts them, since this session may not have a chat
    /// directory open at all (a subscriber filters by directory itself).
    /// </summary>
    public event Action<string, string, WorkerProgressEvent>? SessionProgressReceived;

    /// <summary>
    /// One in-flight pump this process is hosting, and everything needed to reach it: the
    /// caller-retained delivery point for a targeted cancel (M15 Phase 4, issue #140), the host-stop
    /// source that is the Ctrl+C equivalent <c>Aer.Cli</c> wires to <c>Console.CancelKeyPress</c>,
    /// and the pump task itself so a caller can wait for a durable fixed point rather than
    /// abandoning it mid-write.
    /// </summary>
    /// <remarks>
    /// A targeted cancel on an execution registered here is delivered in-process via
    /// <see cref="InFlightExecutionRegistry.RequestCancellationAsync"/>, never a second
    /// mutation-surface call, since §15's guard is already held for this call's entire duration
    /// (M10's decision of record).
    /// </remarks>
    internal sealed class HostedRun(InFlightExecutionRegistry inFlightExecutions, CancellationTokenSource hostStopSource)
    {
        public InFlightExecutionRegistry InFlightExecutions { get; } = inFlightExecutions;

        public CancellationTokenSource HostStopSource { get; } = hostStopSource;

        public Task? PumpTask { get; set; }
    }

    /// <summary>
    /// Every pump this process is hosting, keyed by session directory (#335).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this these were three single-slot fields, so a second concurrent run overwrote the
    /// first's registry and stop source. The consequences were not subtle: a targeted cancel for
    /// session A fell through to the out-of-process <c>CancelCommand</c> path because
    /// <see cref="CurrentTaskDirectoryPath"/> had moved to B, and <see cref="RequestHostStop()"/>
    /// cancelled whichever run started last — so asking the daemon to stop A stopped B instead.
    /// Whichever pump finished first then nulled the shared fields, breaking cancellation for the
    /// one still running.
    /// </para>
    /// <para>
    /// Keyed by <see cref="AerPaths.RecordKey"/>, deliberately the same normaliser #393's per-session
    /// turn lock uses: two directory keys that disagree about whether two spellings are one session
    /// is exactly the failure both primitives exist to prevent.
    /// </para>
    /// <para>
    /// Entries are removed by the run that added them, in its own <c>finally</c> — never "clear the
    /// current one", which is the bug above in a new spelling.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, HostedRun> _hostedRuns = new(AerPaths.RecordKeyComparer);

    /// <summary>The hosted run for <paramref name="taskDirectoryPath"/>, or null if this process is not running it.</summary>
    internal HostedRun? HostedRunFor(string taskDirectoryPath) =>
        _hostedRuns.TryGetValue(AerPaths.RecordKey(taskDirectoryPath), out var run) ? run : null;

    /// <summary>How many pumps this process is hosting right now — the multi-session invariant, observable.</summary>
    internal int HostedRunCount => _hostedRuns.Count;

    /// <summary>
    /// Claims the host slot for <paramref name="taskDirectoryPath"/>. Overwrites any stale entry
    /// rather than refusing: §15's <c>flow.lock</c> is what actually prevents two mutators on one
    /// session, and duplicating that decision here would mean two guards that can disagree.
    /// </summary>
    private HostedRun RegisterHostedRun(
        string taskDirectoryPath, InFlightExecutionRegistry inFlightExecutions, CancellationTokenSource hostStopSource)
    {
        var hostedRun = new HostedRun(inFlightExecutions, hostStopSource);
        _hostedRuns[AerPaths.RecordKey(taskDirectoryPath)] = hostedRun;
        return hostedRun;
    }

    /// <summary>
    /// Releases the host slot, but <b>only if it still holds this run</b>. The identity check is the
    /// point: an unconditional remove would let a finishing run evict a newer run that had already
    /// claimed the same directory, silently disarming cancellation for the one still going.
    /// </summary>
    private void ReleaseHostedRun(string taskDirectoryPath, HostedRun hostedRun) =>
        _hostedRuns.TryRemove(new KeyValuePair<string, HostedRun>(AerPaths.RecordKey(taskDirectoryPath), hostedRun));

    public MainWindowViewModel ViewModel { get; }

    /// <summary>
    /// Which task directory *this client instance* is currently viewing — set only by this
    /// session's own actions (<see cref="SetCurrentTaskDirectory"/>, <see cref="RunAsync"/>,
    /// <see cref="StartInteractiveSessionAsync"/>), never by another client's. Aer.Daemon's own
    /// "current task" is a separate, process-wide notion the daemon uses only to decide what a
    /// brand-new WS connection sees before this client has opened anything of its own — see
    /// <see cref="ShouldApplyProjectionPush"/>, which is what actually keeps two clients pointed
    /// at different directories from corrupting each other's view (pre-M24 defect, filed as part
    /// of issue #262's chat work).
    /// </summary>
    public string? CurrentTaskDirectoryPath { get; private set; }

    public bool LastLoadSucceeded { get; private set; }
    public WorkflowStatus? LastWorkflowStatus { get; private set; }
    public WorkflowDefinitionSnapshot? LastSnapshot { get; private set; }
    public bool IsDaemonConfigured => !string.IsNullOrEmpty(_daemonUrl);
    public bool IsClientMode => _isClientMode;

    /// <summary>
    /// The background task driving whichever pump is currently in flight — retained so the desktop's
    /// window-close handler can wait for it to reach a durable fixed point before actually closing,
    /// rather than abandoning it mid-write (issue #140).
    /// </summary>
    /// <remarks>
    /// Its one caller is the desktop close handler, which hosts a single run, so "the pump" is
    /// unambiguous there. In the daemon, which may host several (#335), this returns an arbitrary
    /// one — callers that mean a specific session must go through <see cref="HostedRunFor"/>. This
    /// stays deliberately non-plural rather than being made per-session for a caller that has no
    /// second session to distinguish; desktop multi-session is #336.
    /// </remarks>
    public Task? CurrentPumpTask => _hostedRuns.Values.FirstOrDefault()?.PumpTask;

    /// <summary>Whether the poller should keep observing: a successfully opened task that has not reached §12's terminal fixed point.</summary>
    public bool ShouldLiveRefresh => LastLoadSucceeded && LastWorkflowStatus != WorkflowStatus.Terminal;

    public TaskSession(
        LocalUiConfigurationStore configurationStore,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        MainWindowViewModel viewModel,
        Func<string?> bindingsFilePathProvider,
        Action mutationStarted,
        Action mutationFailed,
        Func<string, CancellationToken, Task> reopenTaskAsync,
        Action<TaskProjection, string>? onProjectionUpdated = null,
        string? daemonUrl = null)
    {
        _configurationStore = configurationStore;
        _adapters = adapters;
        ViewModel = viewModel;
        _bindingsFilePathProvider = bindingsFilePathProvider;
        _mutationStarted = mutationStarted;
        _mutationFailed = mutationFailed;
        _reopenTaskAsync = reopenTaskAsync;
        _onProjectionUpdated = onProjectionUpdated;
        _daemonUrl = daemonUrl;
    }

    /// <summary>Points the session at <paramref name="taskDirectoryPath"/> without loading — <c>OpenAsync</c>'s bookkeeping half; the load itself goes through <see cref="LoadAsync"/>.</summary>
    public void SetCurrentTaskDirectory(string? taskDirectoryPath) => CurrentTaskDirectoryPath = taskDirectoryPath;

    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Decides whether an incoming projection push for <paramref name="incomingDirectoryPath"/>
    /// should be applied to this client's state, seeding <see cref="CurrentTaskDirectoryPath"/>
    /// from the first push a fresh client ever sees (typically whatever Aer.Daemon last had
    /// open) and rejecting every later push for a different directory. Before this (issue #262
    /// follow-up), every push was applied unconditionally, so one client opening a different
    /// task silently corrupted every other connected client's view with that task's data,
    /// mislabeled under whatever directory the victim client had open. Extracted from
    /// <see cref="ReceiveWebSocketDataAsync"/> so this decision is unit-testable without a live
    /// daemon connection.
    /// </summary>
    internal bool ShouldApplyProjectionPush(string? incomingDirectoryPath)
    {
        CurrentTaskDirectoryPath ??= incomingDirectoryPath;
        return incomingDirectoryPath == CurrentTaskDirectoryPath;
    }

    private void UpdateProjection(TaskProjection projection)
    {
        if (CurrentTaskDirectoryPath != null)
        {
            RebuildPausedSteps(projection, CurrentTaskDirectoryPath);
            RebuildRunningExecutions(projection, CurrentTaskDirectoryPath);
            LastLoadSucceeded = true;
            LastWorkflowStatus = projection.State.Status;
            LastSnapshot = projection.Snapshot;
        }
    }

    /// <summary>
    /// Loads <paramref name="taskDirectoryPath"/> through <see cref="TaskProjectionLoader"/> and
    /// rebuilds the ViewModel's mutation surfaces (<see cref="MainWindowViewModel.PausedSteps"/>,
    /// <see cref="MainWindowViewModel.RunningExecutions"/>) from the projected facts.
    /// </summary>
    public async Task<LoadOutcome> LoadAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        if (await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/tasks/open", new OpenTaskRequest(taskDirectoryPath), cancellationToken).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    var projection = await response.Content.ReadFromJsonAsync<TaskProjection>(DefaultJsonOptions, cancellationToken: cancellationToken).ConfigureAwait(true);
                    if (projection != null)
                    {
                        UpdateProjection(projection);
                        return new LoadOutcome(projection, null);
                    }
                }
                else
                {
                    var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
                    return new LoadOutcome(null, err);
                }
            }
            catch (Exception ex)
            {
                return new LoadOutcome(null, ex.Message);
            }
        }

        // In-process fallback
        try
        {
            var projection = await TaskProjectionLoader.LoadAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);

            RebuildPausedSteps(projection, taskDirectoryPath);
            RebuildRunningExecutions(projection, taskDirectoryPath);

            LastLoadSucceeded = true;
            LastWorkflowStatus = projection.State.Status;
            LastSnapshot = projection.Snapshot;
            return new LoadOutcome(projection, null);
        }
        catch (AerFlowException ex)
        {
            ViewModel.PausedSteps.Clear();
            ViewModel.DecisionStatusText = string.Empty;
            ViewModel.RunningExecutions.Clear();
            ViewModel.CancelStatusText = string.Empty;

            LastLoadSucceeded = false;
            LastWorkflowStatus = null;
            LastSnapshot = null;
            return new LoadOutcome(null, ex.Message);
        }
    }

    /// <summary>
    /// Loads a raw template file and clears the mutation surfaces.
    /// </summary>
    public async Task<TemplateLoadOutcome> LoadTemplateAsync(string templateFilePath, CancellationToken cancellationToken = default)
    {
        ViewModel.PausedSteps.Clear();
        ViewModel.DecisionStatusText = string.Empty;
        ViewModel.RunningExecutions.Clear();
        ViewModel.CancelStatusText = string.Empty;

        try
        {
            var definition = await TemplateProjectionLoader.LoadAsync(templateFilePath, cancellationToken).ConfigureAwait(true);

            LastLoadSucceeded = true;
            LastWorkflowStatus = null;
            LastSnapshot = null;
            return new TemplateLoadOutcome(definition, null);
        }
        catch (AerFlowException ex)
        {
            LastLoadSucceeded = false;
            LastWorkflowStatus = null;
            LastSnapshot = null;
            return new TemplateLoadOutcome(null, ex.Message);
        }
    }

    /// <summary>
    /// The Run mutation: dispatches the Run command to the daemon or executes in-process as fallback.
    /// </summary>
    /// <param name="onWorkerStdoutLine">
    /// M24 Phase 1's live in-turn streaming — forwarded to <see cref="RunCommand.ExecuteAsync"/>'s
    /// own same-named parameter, and therefore only takes effect on the in-process fallback path
    /// below (a delegate can't cross the HTTP call to a real remote daemon). <c>Aer.Daemon</c>'s own
    /// <see cref="TaskSession"/> singleton always takes that fallback path (it has no daemon of its
    /// own to delegate to), which is exactly the case that needs this.
    /// </param>
    public async Task<MutationOutcome> RunAsync(
        string taskDirectoryPath, string? workflowTemplateFilePath, string bindingsFilePath, CancellationToken cancellationToken = default,
        Action<string, string>? onWorkerStdoutLine = null)
    {
        CurrentTaskDirectoryPath = taskDirectoryPath;

        if (await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            try
            {
                var request = new RunTaskRequest(taskDirectoryPath, workflowTemplateFilePath, bindingsFilePath);
                ViewModel.IsMutationInFlight = true;
                ViewModel.RunStatusText = "Running…";
                _mutationStarted();

                var response = await _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/tasks/run", request, cancellationToken).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    ViewModel.RunStatusText = string.Empty;
                    ViewModel.IsMutationInFlight = false;

                    if (!string.IsNullOrWhiteSpace(workflowTemplateFilePath))
                    {
                        await _configurationStore.RecordWorkflowTemplateFilePathAsync(workflowTemplateFilePath, cancellationToken).ConfigureAwait(true);
                    }
                    await _configurationStore.RecordBindingsFilePathAsync(bindingsFilePath, cancellationToken).ConfigureAwait(true);
                    await RecordTaskPathMetadataAsync(taskDirectoryPath, workflowTemplateFilePath, bindingsFilePath, cancellationToken).ConfigureAwait(true);
                    await _reopenTaskAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);
                    return new MutationOutcome(null);
                }
                else
                {
                    var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
                    _mutationFailed();
                    ViewModel.RunStatusText = err;
                    ViewModel.IsMutationInFlight = false;
                    return new MutationOutcome(err);
                }
            }
            catch (Exception ex)
            {
                _mutationFailed();
                ViewModel.RunStatusText = ex.Message;
                ViewModel.IsMutationInFlight = false;
                return new MutationOutcome(ex.Message);
            }
        }

        // In-process fallback
        var options = new RunOptions(
            string.IsNullOrWhiteSpace(workflowTemplateFilePath) ? null : workflowTemplateFilePath,
            bindingsFilePath,
            taskDirectoryPath);

        ViewModel.IsMutationInFlight = true;
        ViewModel.RunStatusText = "Running…";
        _mutationStarted();

        var inFlightExecutions = new InFlightExecutionRegistry();
        var hostStopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var hostedRun = RegisterHostedRun(taskDirectoryPath, inFlightExecutions, hostStopSource);

        try
        {
            var pumpTask = Task.Run(
                () => RunCommand.ExecuteAsync(options, _adapters, inFlightExecutions, hostStopSource.Token, onWorkerStdoutLine), hostStopSource.Token);
            hostedRun.PumpTask = pumpTask;
            await pumpTask.ConfigureAwait(true);

            ViewModel.RunStatusText = string.Empty;

            if (!string.IsNullOrWhiteSpace(workflowTemplateFilePath))
            {
                await _configurationStore.RecordWorkflowTemplateFilePathAsync(workflowTemplateFilePath, cancellationToken).ConfigureAwait(true);
            }

            await _configurationStore.RecordBindingsFilePathAsync(bindingsFilePath, cancellationToken).ConfigureAwait(true);
            await RecordTaskPathMetadataAsync(taskDirectoryPath, workflowTemplateFilePath, bindingsFilePath, cancellationToken).ConfigureAwait(true);
        }
        catch (AerFlowException ex)
        {
            _mutationFailed();
            ViewModel.RunStatusText = ex.Message;

            // #330: a failed pump used to return here without ever calling _reopenTaskAsync, so
            // Aer.Daemon's own wiring of that hook (reopenTaskAsync -> BroadcastStateAsync,
            // Program.cs) never fired for this run at all -- a connected phone watching this
            // directory saw nothing, permanently, instead of learning the run stopped.
            await _reopenTaskAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);
            return new MutationOutcome(ex.Message);
        }
        finally
        {
            ViewModel.IsMutationInFlight = false;
            ReleaseHostedRun(taskDirectoryPath, hostedRun);
            hostStopSource.Dispose();
        }

        await _reopenTaskAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);
        return new MutationOutcome(null);
    }

    private static async Task RecordTaskPathMetadataAsync(string taskDirectoryPath, string? workflowTemplateFilePath, string? bindingsFilePath, CancellationToken cancellationToken)
    {
        try
        {
            var aerDir = Path.Combine(taskDirectoryPath, ".aer");
            Directory.CreateDirectory(aerDir);
            if (!string.IsNullOrWhiteSpace(workflowTemplateFilePath))
            {
                await File.WriteAllTextAsync(Path.Combine(aerDir, "workflow-path"), workflowTemplateFilePath, cancellationToken).ConfigureAwait(false);
            }
            if (!string.IsNullOrWhiteSpace(bindingsFilePath))
            {
                await File.WriteAllTextAsync(Path.Combine(aerDir, "bindings-path"), bindingsFilePath, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best effort
        }
    }

    /// <summary>
    /// The paused-step decision mutation: dispatches the Decide command to the daemon or executes in-process.
    /// </summary>
    /// <param name="onWorkerStdoutLine">
    /// M24 Phase 1's live in-turn streaming — see <see cref="RunAsync"/>'s remarks on the same
    /// parameter; identical in-process-fallback-only behavior applies here.
    /// </param>
    public async Task<MutationOutcome> DecideAsync(
        string taskDirectoryPath,
        StepId stepId,
        ExecutionId executionId,
        DecisionType decisionType,
        StepId? targetStepId,
        string? revisionFilePath,
        string? supplementaryWorker,
        string? supplementaryOutputName,
        CancellationToken cancellationToken = default,
        Action<string, string>? onWorkerStdoutLine = null)
    {
        if (await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            try
            {
                ViewModel.DecisionStatusText = $"Deciding {stepId.Value}…";
                ViewModel.IsMutationInFlight = true;
                _mutationStarted();

                var request = new DecideTaskRequest(
                    taskDirectoryPath,
                    stepId.Value,
                    executionId.Value,
                    decisionType,
                    targetStepId?.Value,
                    revisionFilePath,
                    supplementaryWorker,
                    supplementaryOutputName);

                var response = await _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/tasks/decide", request, cancellationToken).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    ViewModel.DecisionStatusText = string.Empty;
                    ViewModel.IsMutationInFlight = false;
                    await _reopenTaskAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);
                    return new MutationOutcome(null);
                }
                else
                {
                    var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
                    _mutationFailed();
                    ViewModel.DecisionStatusText = err;
                    ViewModel.IsMutationInFlight = false;
                    return new MutationOutcome(err);
                }
            }
            catch (Exception ex)
            {
                _mutationFailed();
                ViewModel.DecisionStatusText = ex.Message;
                ViewModel.IsMutationInFlight = false;
                return new MutationOutcome(ex.Message);
            }
        }

        // In-process fallback
        ViewModel.DecisionStatusText = $"Deciding {stepId.Value}…";
        ViewModel.IsMutationInFlight = true;
        _mutationStarted();

        var inFlightExecutions = new InFlightExecutionRegistry();
        var hostStopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var hostedRun = RegisterHostedRun(taskDirectoryPath, inFlightExecutions, hostStopSource);

        try
        {
            ExecutionId? supplementaryExecutionId = null;

            if (revisionFilePath is not null)
            {
                var supplyOptions = new SupplyOptions(
                    taskDirectoryPath,
                    supplementaryWorker ?? string.Empty,
                    supplementaryOutputName ?? string.Empty,
                    revisionFilePath,
                    _bindingsFilePathProvider() ?? string.Empty);

                var supplyResult = await Task.Run(() => SupplyCommand.ExecuteAsync(supplyOptions, _adapters, hostStopSource.Token), hostStopSource.Token)
                    .ConfigureAwait(true);

                supplementaryExecutionId = supplyResult.ExecutionId;
            }

            var options = new DecideOptions(
                taskDirectoryPath,
                executionId.Value,
                decisionType,
                targetStepId,
                supplementaryExecutionId?.Value,
                _bindingsFilePathProvider() ?? string.Empty);

            var pumpTask = Task.Run(
                () => DecideCommand.ExecuteAsync(options, _adapters, inFlightExecutions, hostStopSource.Token, onWorkerStdoutLine), hostStopSource.Token);
            hostedRun.PumpTask = pumpTask;
            await pumpTask.ConfigureAwait(true);

            ViewModel.DecisionStatusText = string.Empty;
        }
        catch (Exception ex) when (ex is AerFlowException or FileNotFoundException)
        {
            _mutationFailed();
            ViewModel.DecisionStatusText = ex.Message;
            return new MutationOutcome(ex.Message);
        }
        finally
        {
            ViewModel.IsMutationInFlight = false;
            ReleaseHostedRun(taskDirectoryPath, hostedRun);
            hostStopSource.Dispose();
        }

        await _reopenTaskAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);
        return new MutationOutcome(null);
    }

    /// <summary>
    /// The targeted-Cancel surface: cancels via daemon or executes in-process.
    /// </summary>
    public async Task<MutationOutcome> CancelExecutionAsync(
        string taskDirectoryPath, ExecutionId executionId, CancellationToken cancellationToken = default)
    {
        if (await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            try
            {
                ViewModel.CancelStatusText = $"Cancelling {executionId.Value}…";
                ViewModel.IsMutationInFlight = true;
                _mutationStarted();

                var request = new CancelTaskRequest(taskDirectoryPath, executionId.Value);
                var response = await _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/tasks/cancel", request, cancellationToken).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    ViewModel.CancelStatusText = string.Empty;
                    ViewModel.IsMutationInFlight = false;
                    await _reopenTaskAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);
                    return new MutationOutcome(null);
                }
                else
                {
                    var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
                    _mutationFailed();
                    ViewModel.CancelStatusText = err;
                    ViewModel.IsMutationInFlight = false;
                    return new MutationOutcome(err);
                }
            }
            catch (Exception ex)
            {
                _mutationFailed();
                ViewModel.CancelStatusText = ex.Message;
                ViewModel.IsMutationInFlight = false;
                return new MutationOutcome(ex.Message);
            }
        }

        // In-process fallback. Resolved by the directory the caller named, never by whichever
        // session happens to be "current" (#335): with two runs in flight the current one is
        // simply the more recent, so cancelling the other used to miss its live registry entirely
        // and fall through to the out-of-process path below.
        if (HostedRunFor(taskDirectoryPath) is { } hostedRun)
        {
            await hostedRun.InFlightExecutions.RequestCancellationAsync(executionId, cancellationToken).ConfigureAwait(true);
            return new MutationOutcome(null);
        }

        ViewModel.CancelStatusText = $"Cancelling {executionId.Value}…";
        ViewModel.IsMutationInFlight = true;
        _mutationStarted();

        try
        {
            var options = new CancelOptions(taskDirectoryPath, executionId.Value, _bindingsFilePathProvider() ?? string.Empty);
            await Task.Run(() => CancelCommand.ExecuteAsync(options, _adapters, cancellationToken: cancellationToken), cancellationToken)
                .ConfigureAwait(true);

            ViewModel.CancelStatusText = string.Empty;
        }
        catch (AerFlowException ex)
        {
            _mutationFailed();
            ViewModel.CancelStatusText = ex.Message;
            return new MutationOutcome(ex.Message);
        }
        finally
        {
            ViewModel.IsMutationInFlight = false;
        }

        await _reopenTaskAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);
        return new MutationOutcome(null);
    }

    /// <summary>
    /// Stops <b>every</b> pump this process is hosting — the desktop's Stop button and window-close
    /// handler, where there is only ever one, and daemon shutdown, where stopping all is the intent.
    /// To stop one named session, use <see cref="RequestHostStop(string)"/>.
    /// </summary>
    public void RequestHostStop()
    {
        if (_isClientMode && CurrentTaskDirectoryPath != null && _activeDaemonUrl != null)
        {
            _ = _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/tasks/cancel", new CancelTaskRequest(CurrentTaskDirectoryPath, null));
            return;
        }

        foreach (var hostedRun in _hostedRuns.Values)
        {
            hostedRun.HostStopSource.Cancel();
        }
    }

    /// <summary>
    /// Stops the pump hosting <paramref name="taskDirectoryPath"/>, leaving every other hosted
    /// session running. Returns whether one was found.
    /// </summary>
    /// <remarks>
    /// The parameterless overload used to cancel a single shared token source, which with two runs
    /// in flight was whichever started last — so a daemon client asking to stop session A stopped
    /// session B and left A running (#335). Any caller that knows which session it means must call
    /// this one.
    /// </remarks>
    public bool RequestHostStop(string taskDirectoryPath)
    {
        if (_isClientMode && _activeDaemonUrl != null)
        {
            _ = _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/tasks/cancel", new CancelTaskRequest(taskDirectoryPath, null));
            return true;
        }

        if (HostedRunFor(taskDirectoryPath) is not { } hostedRun)
        {
            return false;
        }

        hostedRun.HostStopSource.Cancel();
        return true;
    }

    private void RebuildPausedSteps(TaskProjection projection, string taskDirectoryPath)
    {
        ViewModel.PausedSteps.Clear();

        var stepDefinitionByStepId = projection.Snapshot.Steps.ToDictionary(step => step.StepId);

        foreach (var stepState in projection.State.Steps)
        {
            if (stepState.Status != StepStatus.Paused || stepState.LatestExecutionId is not { } executionId)
            {
                continue;
            }

            var supersedeTargets = stepDefinitionByStepId[stepState.StepId].PausePoint!.SupersedeTargets;

            ViewModel.PausedSteps.Add(new PausedStepViewModel(
                stepState.StepId,
                executionId,
                supersedeTargets,
                (stepId, decidedExecutionId, decisionType, targetStepId, revisionFilePath, supplementaryWorker, supplementaryOutputName) =>
                    DecideAsync(
                        taskDirectoryPath, stepId, decidedExecutionId, decisionType, targetStepId,
                        revisionFilePath, supplementaryWorker, supplementaryOutputName))
            {
                IsEnabled = !ViewModel.IsMutationInFlight,
            });
        }
    }

    private void RebuildRunningExecutions(TaskProjection projection, string taskDirectoryPath)
    {
        ViewModel.RunningExecutions.Clear();

        var isLocallyHostedTask = HostedRunFor(taskDirectoryPath) is not null;

        foreach (var stepState in projection.State.Steps)
        {
            if (stepState.Status != StepStatus.Running || stepState.LatestExecutionId is not { } executionId)
            {
                continue;
            }

            AddRunningExecution(stepState.StepId, executionId, isLocallyHostedTask || _isClientMode, projection.State, taskDirectoryPath);
        }

        foreach (var stepLessExecution in projection.State.StepLessExecutions)
        {
            AddRunningExecution(stepId: null, stepLessExecution.ExecutionId, isLocallyHosted: false, projection.State, taskDirectoryPath);
        }
    }

    private void AddRunningExecution(
        StepId? stepId, ExecutionId executionId, bool isLocallyHosted, FlowState state, string taskDirectoryPath)
    {
        var cancellationRequested = state.CancellationRequestedExecutionIds.Contains(executionId);

        ViewModel.RunningExecutions.Add(new RunningExecutionViewModel(
            stepId,
            executionId,
            isLocallyHosted,
            cancellationRequested,
            targetExecutionId => CancelExecutionAsync(taskDirectoryPath, targetExecutionId))
        {
            IsEnabled = isLocallyHosted || !ViewModel.IsMutationInFlight,
        });
    }
}
