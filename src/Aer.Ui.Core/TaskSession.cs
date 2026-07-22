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
public sealed class TaskSession
{
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
    /// The caller-retained delivery point (M15 Phase 4, issue #140) for whichever Run or Decide pump
    /// this session currently has in flight — <see langword="null"/> whenever this process is not
    /// hosting one. A targeted Cancel on an execution registered here is delivered in-process via
    /// <see cref="InFlightExecutionRegistry.RequestCancellationAsync"/>, never a second
    /// mutation-surface call, since §15's guard is already held for this call's entire duration
    /// (M10's decision of record).
    /// </summary>
    private InFlightExecutionRegistry? _currentInFlightExecutions;

    /// <summary>
    /// The host-stop token source for whichever pump this session currently has in flight (issue
    /// #140) — cancelling it is the Ctrl+C equivalent <c>Aer.Cli</c>'s <c>Program.cs</c> wires to
    /// <c>Console.CancelKeyPress</c>, reused by the desktop's Stop button and window-close handler.
    /// </summary>
    private CancellationTokenSource? _currentHostStopSource;

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
    public Task? CurrentPumpTask { get; private set; }

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

    public Task RecordOpenedAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
        => _configurationStore.RecordOpenedAsync(taskDirectoryPath, cancellationToken);

    public Task<IReadOnlyList<string>> LoadRecentTaskDirectoriesAsync(CancellationToken cancellationToken = default)
        => _configurationStore.LoadRecentTaskDirectoriesAsync(cancellationToken);

    public Task<string?> LoadLastBindingsFilePathAsync(CancellationToken cancellationToken = default)
        => _configurationStore.LoadLastBindingsFilePathAsync(cancellationToken);

    public Task<string?> LoadLastWorkflowTemplateFilePathAsync(CancellationToken cancellationToken = default)
        => _configurationStore.LoadLastWorkflowTemplateFilePathAsync(cancellationToken);

    public Task<string?> LoadTailscaleAuthKeyAsync(CancellationToken cancellationToken = default)
        => _configurationStore.LoadTailscaleAuthKeyAsync(cancellationToken);

    public Task RecordTailscaleAuthKeyAsync(string? tailscaleAuthKey, CancellationToken cancellationToken = default)
        => _configurationStore.RecordTailscaleAuthKeyAsync(tailscaleAuthKey, cancellationToken);

    private (string Url, string Token)? GetDaemonConnectionInfo()
    {
        if (string.IsNullOrEmpty(_daemonUrl)) return null;

        var aerDir = AerPaths.Root;
        if (!Directory.Exists(aerDir)) return null;

        var tokenFile = Path.Combine(aerDir, "daemon.token");
        if (!File.Exists(tokenFile)) return null;

        var token = File.ReadAllText(tokenFile).Trim();

        var portFile = Path.Combine(aerDir, "daemon.port");
        var activePort = 5000;
        if (File.Exists(portFile))
        {
            if (int.TryParse(File.ReadAllText(portFile).Trim(), out var p))
            {
                activePort = p;
            }
        }

        var url = $"http://localhost:{activePort}";
        return (url, token);
    }

    private void ConfigureHttpClientHeaders(string token)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<bool> EnsureDaemonConnectedAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_daemonUrl)) return false;
        if (_isClientMode) return true;

        var conn = GetDaemonConnectionInfo();
        _activeDaemonUrl = conn?.Url ?? _daemonUrl;
        var token = conn?.Token;

        if (token != null)
        {
            ConfigureHttpClientHeaders(token);
        }

        try
        {
            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/version", cancellationToken).ConfigureAwait(true);
            if (response.IsSuccessStatusCode)
            {
                var meta = await response.Content.ReadFromJsonAsync<DaemonVersionInfo>(cancellationToken: cancellationToken).ConfigureAwait(true);
                var clientVersion = typeof(TaskSession).Assembly.GetName().Version?.ToString() ?? "1.0.0";

                if (meta != null && meta.Version == clientVersion)
                {
                    _isClientMode = true;
                    await StartWebSocketListenerAsync(_activeDaemonUrl, token, cancellationToken).ConfigureAwait(true);
                    return true;
                }
                else if (meta != null && !meta.HasRunningTasks)
                {
                    // Version skew! Force shutdown running daemon to respawn updated one (safe since no tasks are running)
                    await _httpClient.PostAsync($"{_activeDaemonUrl}/api/daemon/shutdown", null, cancellationToken).ConfigureAwait(true);
                    await Task.Delay(500, cancellationToken).ConfigureAwait(true);
                }
                else
                {
                    // Version skew, but daemon has running tasks in flight. We must not terminate it;
                    // continue using the running daemon to preserve task integrity.
                    _isClientMode = true;
                    await StartWebSocketListenerAsync(_activeDaemonUrl, token, cancellationToken).ConfigureAwait(true);
                    return true;
                }
            }
        }
        catch
        {
            // Connect failed
        }

        return await SpawnDaemonProcessAsync("", cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Launches a fresh <c>Aer.Daemon</c> child process (appending <paramref name="extraArgs"/> to
    /// its command line, e.g. <c>--remote</c>) and polls <c>/api/version</c> until it answers or 30
    /// attempts are exhausted. Shared by <see cref="EnsureDaemonConnectedAsync"/>'s cold-start path
    /// and <see cref="SetRemoteEnabledAsync"/>'s shutdown-then-respawn (M21 Phase 3, issue #234) —
    /// both need the exact same "start it, then wait for it to come up" dance.
    /// </summary>
    private async Task<bool> SpawnDaemonProcessAsync(string extraArgs, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_daemonUrl)) return false;

        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string daemonPath;
            string args = "";

            if (OperatingSystem.IsWindows())
            {
                daemonPath = Path.Combine(baseDir, "Aer.Daemon.exe");
                if (!File.Exists(daemonPath))
                {
                    daemonPath = "dotnet";
                    args = $"\"{Path.Combine(baseDir, "Aer.Daemon.dll")}\"";
                }
            }
            else
            {
                daemonPath = Path.Combine(baseDir, "Aer.Daemon");
                if (!File.Exists(daemonPath))
                {
                    daemonPath = "dotnet";
                    args = $"\"{Path.Combine(baseDir, "Aer.Daemon.dll")}\"";
                }
            }

            if (!string.IsNullOrEmpty(extraArgs))
            {
                args = string.IsNullOrEmpty(args) ? extraArgs : $"{args} {extraArgs}";
            }

            var hasDll = File.Exists(Path.Combine(baseDir, "Aer.Daemon.dll"));
            var hasExe = File.Exists(Path.Combine(baseDir, "Aer.Daemon.exe")) || File.Exists(Path.Combine(baseDir, "Aer.Daemon"));

            if (hasExe || hasDll)
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = daemonPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                var process = Process.Start(startInfo);
                if (process != null)
                {
                    // Previously unredirected, so a spawn failure (bind conflict, unhandled
                    // exception at startup, anything) was completely invisible — the polling loop
                    // below just kept failing with no way to tell why. Async reads (not sync
                    // ReadToEnd) so a chatty child can't block on a full stdout/stderr pipe buffer.
                    try
                    {
                        var aerDir = AerPaths.Root;
                        Directory.CreateDirectory(aerDir);
                        var logPath = Path.Combine(aerDir, "daemon-spawn.log");
                        File.WriteAllText(logPath, $"--- spawn {DateTime.UtcNow:O} (args: {args}) ---{Environment.NewLine}");
                        process.OutputDataReceived += (_, e) => { if (e.Data != null) TryAppendLog(logPath, e.Data); };
                        process.ErrorDataReceived += (_, e) => { if (e.Data != null) TryAppendLog(logPath, e.Data); };
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                    }
                    catch
                    {
                        // Best-effort diagnostics only — never block a real spawn attempt on logging.
                    }

                    _ = Task.Run(() => SuperviseDaemonProcessAsync(process), CancellationToken.None);

                    for (int i = 0; i < 30; i++)
                    {
                        // A child process that has already exited (e.g. it lost the single-instance
                        // mutex race against a still-shutting-down old daemon — found live, M22
                        // planning: a caller that respawns without confirming the old process is
                        // truly gone first hits exactly this) will never answer /api/version. Fail
                        // fast instead of burning the rest of this loop's budget on a dead process.
                        if (process.HasExited) break;

                        try
                        {
                            var newConn = GetDaemonConnectionInfo();
                            _activeDaemonUrl = newConn?.Url ?? _daemonUrl;
                            var newToken = newConn?.Token;
                            if (newToken != null)
                            {
                                ConfigureHttpClientHeaders(newToken);
                            }

                            // Each attempt gets its own short deadline rather than inheriting
                            // _httpClient's full 5s default — 30 attempts at up to 5s each could
                            // stretch this loop to minutes if a request ever hangs instead of
                            // failing fast (found live: a stale port mid-handoff did exactly this).
                            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            attemptCts.CancelAfter(TimeSpan.FromMilliseconds(400));

                            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/version", attemptCts.Token).ConfigureAwait(true);
                            if (response.IsSuccessStatusCode)
                            {
                                _isClientMode = true;
                                await StartWebSocketListenerAsync(_activeDaemonUrl, newToken, cancellationToken).ConfigureAwait(true);
                                return true;
                            }
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            // This attempt's own short deadline elapsed, not the caller's token — keep polling.
                        }
                        catch
                        {
                            await Task.Delay(100, cancellationToken).ConfigureAwait(true);
                        }
                    }
                }
            }
        }
        catch
        {
            // Start process failed
        }

        _isClientMode = false;
        return false;
    }

    private static readonly Lock LogLock = new();

    private static void TryAppendLog(string logPath, string line)
    {
        try
        {
            lock (LogLock)
            {
                File.AppendAllText(logPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Best-effort diagnostics only.
        }
    }

    private async Task SuperviseDaemonProcessAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (_isClientMode)
            {
                _isClientMode = false;
                if (CurrentTaskDirectoryPath != null)
                {
                    _ = EnsureDaemonConnectedAsync(CancellationToken.None);
                }
            }
        }
        catch
        {
            // Supervision failed
        }
    }

    private async Task StartWebSocketListenerAsync(string resolvedUrl, string? token, CancellationToken cancellationToken)
    {
        _wsCts?.Cancel();
        _wsCts = new CancellationTokenSource();
        _webSocket = new ClientWebSocket();
        var wsUrl = resolvedUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/api/ws";
        if (token != null)
        {
            wsUrl += $"?token={token}";
        }
        try
        {
            await _webSocket.ConnectAsync(new Uri(wsUrl), cancellationToken).ConfigureAwait(true);
            _ = Task.Run(() => ReceiveWebSocketDataAsync(_webSocket, _wsCts.Token), _wsCts.Token);
        }
        catch
        {
            // WS connect failed
        }

        await StartProgressWebSocketListenerAsync(resolvedUrl, token, cancellationToken).ConfigureAwait(true);
    }

    private sealed record ProgressFrame(string DirectoryPath, string StepId, string Kind, string Text, bool IsPartial);

    /// <summary>
    /// Mirrors <see cref="TaskProjection"/>'s shape plus the <c>DirectoryPath</c> sibling property
    /// Aer.Daemon bolts onto every <c>/api/ws</c> frame (M21 Phase 2, #232). Deserializing straight
    /// into <see cref="TaskProjection"/>, as <see cref="ReceiveWebSocketDataAsync"/> did before,
    /// silently drops that sibling — leaving no way to tell whether an incoming push is even for the
    /// directory this client has open, so every push got applied unconditionally regardless of which
    /// client's action produced it. This wrapper exists solely so the receive loop can filter.
    /// </summary>
    private sealed record ProjectionFrame(
        string? DirectoryPath,
        WorkflowDefinitionSnapshot Snapshot,
        FlowState State,
        ExecutionHistory History,
        ArtifactLineage Lineage);

    /// <summary>The M24 Phase 1 live in-turn streaming socket's client-side counterpart -- see <see cref="SessionProgressReceived"/>. A dedicated connection, not folded into <see cref="StartWebSocketListenerAsync"/>'s own socket, for the exact same reason the daemon keeps the two endpoints separate (<c>Aer.Daemon.Program</c>'s <c>progressWebSockets</c> remarks): this frame shape has no type discriminator, so sharing a socket risks a <see cref="TaskProjection"/> deserialization corrupting on it.</summary>
    private async Task StartProgressWebSocketListenerAsync(string resolvedUrl, string? token, CancellationToken cancellationToken)
    {
        _progressWsCts?.Cancel();
        _progressWsCts = new CancellationTokenSource();
        _progressWebSocket = new ClientWebSocket();
        var wsUrl = resolvedUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/api/ws/progress";
        if (token != null)
        {
            wsUrl += $"?token={token}";
        }
        try
        {
            await _progressWebSocket.ConnectAsync(new Uri(wsUrl), cancellationToken).ConfigureAwait(true);
            _ = Task.Run(() => ReceiveProgressWebSocketDataAsync(_progressWebSocket, _progressWsCts.Token), _progressWsCts.Token);
        }
        catch
        {
            // WS connect failed
        }
    }

    private async Task ReceiveProgressWebSocketDataAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 64];
        try
        {
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(true);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                ms.Position = 0;
                var frame = await JsonSerializer.DeserializeAsync<ProgressFrame>(ms, DefaultJsonOptions, cancellationToken).ConfigureAwait(true);
                if (frame == null)
                {
                    continue;
                }

                var progressEvent = new WorkerProgressEvent(frame.Kind, frame.Text, frame.IsPartial);
                if (_syncContext != null)
                {
                    _syncContext.Post(_ => SessionProgressReceived?.Invoke(frame.DirectoryPath, frame.StepId, progressEvent), null);
                }
                else
                {
                    SessionProgressReceived?.Invoke(frame.DirectoryPath, frame.StepId, progressEvent);
                }
            }
        }
        catch
        {
            // WS disconnected
        }
    }

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

    private async Task ReceiveWebSocketDataAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        try
        {
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(true);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    ms.Position = 0;
                    var frame = await JsonSerializer.DeserializeAsync<ProjectionFrame>(ms, DefaultJsonOptions, cancellationToken).ConfigureAwait(true);

                    if (frame != null)
                    {
                        if (ShouldApplyProjectionPush(frame.DirectoryPath))
                        {
                            var projection = new TaskProjection(frame.Snapshot, frame.State, frame.History, frame.Lineage);
                            if (_syncContext != null)
                            {
                                _syncContext.Post(_ =>
                                {
                                    UpdateProjection(projection);
                                    if (_onProjectionUpdated != null && CurrentTaskDirectoryPath != null)
                                    {
                                        _onProjectionUpdated(projection, CurrentTaskDirectoryPath);
                                    }
                                }, null);
                            }
                            else
                            {
                                UpdateProjection(projection);
                                if (_onProjectionUpdated != null && CurrentTaskDirectoryPath != null)
                                {
                                    _onProjectionUpdated(projection, CurrentTaskDirectoryPath);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // WS disconnected
        }
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
        _currentInFlightExecutions = inFlightExecutions;
        _currentHostStopSource = hostStopSource;

        try
        {
            var pumpTask = Task.Run(
                () => RunCommand.ExecuteAsync(options, _adapters, inFlightExecutions, hostStopSource.Token, onWorkerStdoutLine), hostStopSource.Token);
            CurrentPumpTask = pumpTask;
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
            return new MutationOutcome(ex.Message);
        }
        finally
        {
            ViewModel.IsMutationInFlight = false;
            _currentInFlightExecutions = null;
            _currentHostStopSource = null;
            CurrentPumpTask = null;
            hostStopSource.Dispose();
        }

        await _reopenTaskAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);
        return new MutationOutcome(null);
    }

    /// <summary>
    /// Starts an interactive chat/codebase session (M24 Phase 1, issue #262) the same daemon-first,
    /// in-process-fallback way as <see cref="RunAsync"/>. Unlike RunAsync's fallback, there is no
    /// Aer.Cli equivalent for dispatching a session's initial turn -- that logic
    /// (<c>ExecuteSessionTurnAsync</c>) lives only in <c>Aer.Daemon.Program</c> -- so without a
    /// reachable daemon, <see cref="StartSessionRequest.InitialMessage"/> materializes the session
    /// but is not auto-answered; the caller can send it afterward once a daemon is available.
    /// </summary>
    public async Task<SessionStartOutcome> StartInteractiveSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
    {
        if (await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/sessions/start", request, cancellationToken).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    var metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: cancellationToken).ConfigureAwait(true);
                    return new SessionStartOutcome(metadata, null);
                }

                var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
                return new SessionStartOutcome(null, err);
            }
            catch (Exception ex)
            {
                return new SessionStartOutcome(null, ex.Message);
            }
        }

        // In-process fallback: materialize locally, same directory-naming rule the daemon endpoint
        // uses (InteractiveSessionMaterializer.ResolveTaskDirectoryPath), so a session created
        // without a daemon lands wherever a later-started daemon's /api/sessions endpoints would
        // also look for it.
        try
        {
            var sessionId = Guid.NewGuid().ToString("N")[..12];
            var taskDirectoryPath = InteractiveSessionMaterializer.ResolveTaskDirectoryPath(sessionId, request.TaskName, request.DirectoryPath);

            var metadata = await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                sessionId,
                taskDirectoryPath,
                string.IsNullOrWhiteSpace(request.Adapter) ? "claude" : request.Adapter.Trim().ToLowerInvariant(),
                request.Model,
                request.WorkingDirectory,
                request.InitialMessage,
                request.SafetyCeiling ?? InteractiveSessionMaterializer.DefaultSafetyCeiling,
                request.PermissionGrant,
                cancellationToken).ConfigureAwait(true);

            SetCurrentTaskDirectory(taskDirectoryPath);
            await RecordOpenedAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);

            return new SessionStartOutcome(metadata, null);
        }
        catch (Exception ex)
        {
            return new SessionStartOutcome(null, ex.Message);
        }
    }

    /// <summary>
    /// Sends the next turn to an already-started interactive session (M24 Phase 1 desktop chat UI,
    /// issue #262). Unlike <see cref="StartInteractiveSessionAsync"/> there is no in-process
    /// fallback here: <c>ExecuteSessionTurnAsync</c> only exists in <c>Aer.Daemon.Program</c>, and
    /// <c>POST /api/sessions/send</c> itself only confirms the turn was dispatched onto the
    /// daemon's own background task, not that it completed -- the caller observes completion by
    /// polling <see cref="LoadSessionMetadataAsync"/> the same way every other live task state in
    /// this app is observed (<c>MainWindow</c>'s existing 2-second poll).
    /// </summary>
    public async Task<MutationOutcome> SendSessionMessageAsync(SendSessionMessageRequest request, CancellationToken cancellationToken = default)
    {
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            return new MutationOutcome("A session turn requires the daemon, and none is reachable.");
        }

        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/sessions/send", request, cancellationToken).ConfigureAwait(true);
            if (response.IsSuccessStatusCode)
            {
                return new MutationOutcome(null);
            }

            var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
            return new MutationOutcome(err);
        }
        catch (Exception ex)
        {
            return new MutationOutcome(ex.Message);
        }
    }

    /// <summary>
    /// Discovered skills/commands/agents/models for a session's current vendor, plus this vendor's
    /// most-recently-used entries (M24 Phase 2 follow-up chat capability picker). No in-process
    /// fallback, same reasoning as <see cref="SendSessionMessageAsync"/> -- this is purely a daemon
    /// read.
    /// </summary>
    public async Task<(SessionCommandsResult? Result, string? ErrorMessage)> GetSessionCommandsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            return (null, "Discovering commands requires the daemon, and none is reachable.");
        }

        try
        {
            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/sessions/{sessionId}/commands", cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                return (null, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true));
            }

            var result = await response.Content.ReadFromJsonAsync<SessionCommandsResult>(cancellationToken: cancellationToken).ConfigureAwait(true);
            return (result, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Best-effort: a picked command failing to record as "recently used" shouldn't block or error the chat UI.</summary>
    public async Task RecordCommandUsedAsync(string sessionId, string commandName, CancellationToken cancellationToken = default)
    {
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/sessions/{sessionId}/commands/record", new { Name = commandName }, cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            // Recency is a convenience, not a correctness requirement -- see LocalUiConfigurationStore's own remarks.
        }
    }

    /// <summary>Session-level mode (M24 Phase 2 follow-up): "auto", "default", or "plan", applying to whichever vendor is currently active.</summary>
    public async Task<MutationOutcome> SetSessionModeAsync(string sessionId, string mode, CancellationToken cancellationToken = default)
    {
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            return new MutationOutcome("Setting session mode requires the daemon, and none is reachable.");
        }

        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/sessions/{sessionId}/mode", new { Mode = mode }, cancellationToken).ConfigureAwait(true);
            if (response.IsSuccessStatusCode)
            {
                return new MutationOutcome(null);
            }

            var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
            return new MutationOutcome(err);
        }
        catch (Exception ex)
        {
            return new MutationOutcome(ex.Message);
        }
    }

    /// <summary>The currently active session mode (#286), reverse-mapped server-side from the persisted PermissionGrant — "auto", "default", "plan", or "custom".</summary>
    public async Task<(string? Mode, string? ErrorMessage)> GetSessionModeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            return (null, "Reading session mode requires the daemon, and none is reachable.");
        }

        try
        {
            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/sessions/{sessionId}/mode", cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                return (null, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true));
            }

            var result = await response.Content.ReadFromJsonAsync<SessionModeResult>(cancellationToken: cancellationToken).ConfigureAwait(true);
            return (result?.Mode, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Compacts a session's history (#286): wires the chat command picker's "/compact" item to the
    /// real dedicated action instead of inserting literal text and hoping the vendor's own
    /// (unverified) slash-command handling does something with it. Fire-and-forget on the daemon
    /// side, same as <see cref="SendSessionMessageAsync"/> — completion is observed via the existing
    /// metadata poll, not this call's response.
    /// </summary>
    public async Task<MutationOutcome> CompactSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            return new MutationOutcome("Compacting a session requires the daemon, and none is reachable.");
        }

        try
        {
            var response = await _httpClient.PostAsync($"{_activeDaemonUrl}/api/sessions/{sessionId}/compact", null, cancellationToken).ConfigureAwait(true);
            if (response.IsSuccessStatusCode)
            {
                return new MutationOutcome(null);
            }

            var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
            return new MutationOutcome(err);
        }
        catch (Exception ex)
        {
            return new MutationOutcome(ex.Message);
        }
    }

    /// <summary>
    /// Clears a session's visible transcript and forces the next turn to start a genuinely fresh
    /// native session (#286) — unlike <see cref="CompactSessionAsync"/> this never talks to the
    /// vendor and completes synchronously, so the caller gets the updated (empty-turns) metadata
    /// back directly rather than needing to poll for it.
    /// </summary>
    public async Task<(SessionMetadata? Result, string? ErrorMessage)> ClearSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            return (null, "Clearing a session requires the daemon, and none is reachable.");
        }

        try
        {
            var response = await _httpClient.PostAsync($"{_activeDaemonUrl}/api/sessions/{sessionId}/clear", null, cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                return (null, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true));
            }

            var result = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: cancellationToken).ConfigureAwait(true);
            return (result, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private sealed record SessionModeResult(string? Mode);

    /// <summary>
    /// The fleet list (M24 Phase 5, #278): every known task/session directory's lightweight status.
    /// Daemon-only, same reasoning as <see cref="GetSessionCommandsAsync"/> — scanning both
    /// <c>~/.aer/tasks</c> and <c>~/.aer/sessions</c> is inherently a whole-host operation, not
    /// something this client instance's own in-process fallback state could answer meaningfully.
    /// </summary>
    public async Task<(IReadOnlyList<TaskFleetItem>? Items, string? ErrorMessage)> GetFleetAsync(
        bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            return (null, "Listing tasks requires the daemon, and none is reachable.");
        }

        try
        {
            var response = await _httpClient.GetAsync(
                $"{_activeDaemonUrl}/api/tasks?includeArchived={includeArchived}", cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                return (null, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true));
            }

            var items = await response.Content.ReadFromJsonAsync<List<TaskFleetItem>>(
                DefaultJsonOptions, cancellationToken: cancellationToken).ConfigureAwait(true);
            return (items, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Archives a task/session directory (M24 Phase 5, #278) — hidden from the default fleet list, name still reserved until a real delete.</summary>
    public async Task<MutationOutcome> ArchiveTaskAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        if (await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync(
                    $"{_activeDaemonUrl}/api/tasks/archive", new TaskDirectoryRequest(taskDirectoryPath), cancellationToken).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    return new MutationOutcome(null);
                }
                return new MutationOutcome(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                return new MutationOutcome(ex.Message);
            }
        }

        // In-process fallback
        try
        {
            await TaskLifecycle.ArchiveAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);
            return new MutationOutcome(null);
        }
        catch (Exception ex)
        {
            return new MutationOutcome(ex.Message);
        }
    }

    /// <summary>Unarchives a task/session directory (M24 Phase 5, #278) — reappears in the default fleet list.</summary>
    public async Task<MutationOutcome> UnarchiveTaskAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        if (await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync(
                    $"{_activeDaemonUrl}/api/tasks/unarchive", new TaskDirectoryRequest(taskDirectoryPath), cancellationToken).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    return new MutationOutcome(null);
                }
                return new MutationOutcome(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                return new MutationOutcome(ex.Message);
            }
        }

        // In-process fallback
        try
        {
            await TaskLifecycle.UnarchiveAsync(taskDirectoryPath).ConfigureAwait(true);
            return new MutationOutcome(null);
        }
        catch (Exception ex)
        {
            return new MutationOutcome(ex.Message);
        }
    }

    /// <summary>Really deletes a task/session directory (M24 Phase 5, #278) — the only action that frees its name for reuse — and strips it from the recents list so a stale recent never 404s on the next open.</summary>
    public async Task<MutationOutcome> DeleteTaskAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        if (await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync(
                    $"{_activeDaemonUrl}/api/tasks/delete", new TaskDirectoryRequest(taskDirectoryPath), cancellationToken).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    return new MutationOutcome(null);
                }
                return new MutationOutcome(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                return new MutationOutcome(ex.Message);
            }
        }

        // In-process fallback
        try
        {
            if (!Directory.Exists(taskDirectoryPath))
            {
                return new MutationOutcome($"'{taskDirectoryPath}' does not exist.");
            }

            Directory.Delete(taskDirectoryPath, recursive: true);
            await _configurationStore.RemoveRecentTaskDirectoryAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);
            return new MutationOutcome(null);
        }
        catch (Exception ex)
        {
            return new MutationOutcome(ex.Message);
        }
    }

    /// <summary>
    /// Reads <paramref name="taskDirectoryPath"/>'s <c>.aer/session.json</c> directly rather than
    /// round-tripping through the daemon (unlike <see cref="LoadAsync"/>'s <c>TaskProjection</c>,
    /// <see cref="SessionMetadata"/> is a directly-readable local artifact with no in-memory
    /// projection of its own) -- also doubles as the "is this task directory a chat/codebase
    /// session" check <c>MainWindow.OpenAsync</c> uses to decide whether to route to the Chat view.
    /// Returns <see langword="null"/> for a directory that isn't an interactive session at all.
    /// </summary>
    public async Task<SessionMetadata?> LoadSessionMetadataAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        var metadataPath = Path.Combine(taskDirectoryPath, ".aer", "session.json");
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        return await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath, cancellationToken).ConfigureAwait(true);
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
        _currentInFlightExecutions = inFlightExecutions;
        _currentHostStopSource = hostStopSource;

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
            CurrentPumpTask = pumpTask;
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
            _currentInFlightExecutions = null;
            _currentHostStopSource = null;
            CurrentPumpTask = null;
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

        // In-process fallback
        if (_currentInFlightExecutions is { } registry && CurrentTaskDirectoryPath == taskDirectoryPath)
        {
            await registry.RequestCancellationAsync(executionId, cancellationToken).ConfigureAwait(true);
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
    /// Stops the host pump.
    /// </summary>
    public void RequestHostStop()
    {
        if (_isClientMode && CurrentTaskDirectoryPath != null && _activeDaemonUrl != null)
        {
            _ = _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/tasks/cancel", new CancelTaskRequest(CurrentTaskDirectoryPath, null));
            return;
        }

        _currentHostStopSource?.Cancel();
    }

    /// <summary>Current remote-access state, for the Enable Remote Access view (M21 Phase 3, issue #234).</summary>
    public sealed record RemoteAccessStatus(bool IsRemote, int Port, bool HasRunningTasks);

    /// <summary>A freshly generated pairing code, mirroring <c>/api/pairing/code</c>'s response shape.</summary>
    public sealed record PairingCode(string Code, int ExpiresInSeconds);

    /// <summary>Reads the daemon's current bind mode and port straight from <c>/api/version</c> — not cached, since the whole point is to detect drift after a toggle.</summary>
    public async Task<RemoteAccessStatus?> GetRemoteAccessStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_activeDaemonUrl == null) return null;

        try
        {
            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/version", cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode) return null;

            var meta = await response.Content.ReadFromJsonAsync<DaemonVersionInfo>(DefaultJsonOptions, cancellationToken: cancellationToken).ConfigureAwait(true);
            if (meta == null) return null;

            var port = new Uri(_activeDaemonUrl).Port;
            return new RemoteAccessStatus(meta.IsRemote, port, meta.HasRunningTasks);
        }
        catch
        {
            return null;
        }
    }

    public async Task<PairingCode?> GetPairingCodeAsync(CancellationToken cancellationToken = default)
    {
        if (_activeDaemonUrl == null) return null;

        try
        {
            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/pairing/code", cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadFromJsonAsync<PairingCode>(DefaultJsonOptions, cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A paired device, mirroring <c>/api/pairing/clients</c>'s response shape (Phase 6, #243).</summary>
    public sealed record PairedClientInfo(string ClientId, string Name, DateTime PairedAt);

    /// <summary>Lists paired devices for the "Paired Devices" management list — desktop-owner-only, see <c>Program.cs</c>'s <c>IsLocalToken</c> gate.</summary>
    public async Task<IReadOnlyList<PairedClientInfo>?> GetPairedClientsAsync(CancellationToken cancellationToken = default)
    {
        if (_activeDaemonUrl == null) return null;

        try
        {
            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/pairing/clients", cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadFromJsonAsync<IReadOnlyList<PairedClientInfo>>(DefaultJsonOptions, cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Revokes a paired device's token — its next request 401s immediately (Phase 6, #243).</summary>
    public async Task<bool> RevokePairedClientAsync(string clientId, CancellationToken cancellationToken = default)
    {
        if (_activeDaemonUrl == null) return false;

        try
        {
            var response = await _httpClient.DeleteAsync($"{_activeDaemonUrl}/api/pairing/clients/{Uri.EscapeDataString(clientId)}", cancellationToken).ConfigureAwait(true);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The Go tsnet sidecar's own state, mirrored via <c>/api/remote/sidecar-status</c>
    /// (Phase 5, #242) — <c>Ready</c>/<c>TailscaleIp</c> once tsnet enrollment is complete,
    /// <c>AuthUrl</c> while the one-time interactive Tailscale login is still pending, or
    /// <c>Error</c> for anything else (sidecar binary missing, tsnet itself failed to start).</summary>
    public sealed record SidecarStatus(bool Ready, string? AuthUrl, string? TailscaleIp, string? Error);

    /// <summary>Not cached — proxies the sidecar's live <c>/status</c> straight through Aer.Daemon, so this never reports stale enrollment state.</summary>
    public async Task<SidecarStatus?> GetSidecarStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_activeDaemonUrl == null) return null;

        try
        {
            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/remote/sidecar-status", cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadFromJsonAsync<SidecarStatus>(DefaultJsonOptions, cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Signs the tsnet sidecar out of its current tailnet and re-enters the one-time interactive
    /// login flow — the in-app counterpart of deleting the node from the Tailscale admin console and
    /// restarting Aer.Ui, which was previously the only way to disconnect it. Fire-and-forget on the
    /// daemon side: the sidecar answers 202 immediately and does the actual logout/re-auth in the
    /// background, so the next few <see cref="GetSidecarStatusAsync"/> polls are what surfaces the
    /// fresh <c>AuthUrl</c> once it's ready.
    /// </summary>
    public async Task<bool> ForgetSidecarAsync(CancellationToken cancellationToken = default)
    {
        if (_activeDaemonUrl == null) return false;

        try
        {
            var response = await _httpClient.PostAsync($"{_activeDaemonUrl}/api/remote/sidecar-forget", null, cancellationToken).ConfigureAwait(true);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Flips the daemon between loopback-only and <c>--remote</c>. There's no live Kestrel rebind
    /// (<c>Aer.Daemon/Program.cs</c> bakes the bind address in at startup) — so this shuts the
    /// daemon down and respawns it with/without <c>--remote</c>, reusing the same
    /// shutdown-then-respawn move <see cref="EnsureDaemonConnectedAsync"/> already makes on version
    /// skew. Refuses while a task is in flight, since bouncing the daemon would orphan it.
    /// </summary>
    public async Task<MutationOutcome> SetRemoteEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            return await SetRemoteEnabledAsyncCore(enabled, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogToggleDiagnostic($"SetRemoteEnabledAsync threw: {ex}");
            throw;
        }
    }

    private async Task<MutationOutcome> SetRemoteEnabledAsyncCore(bool enabled, CancellationToken cancellationToken)
    {
        LogToggleDiagnostic($"SetRemoteEnabledAsync(enabled={enabled}) start, _activeDaemonUrl={_activeDaemonUrl}");
        var status = await GetRemoteAccessStatusAsync(cancellationToken).ConfigureAwait(true);
        if (status == null)
        {
            LogToggleDiagnostic("GetRemoteAccessStatusAsync returned null -> Could not reach Aer.Daemon.");
            return new MutationOutcome("Could not reach Aer.Daemon.");
        }
        LogToggleDiagnostic($"status: IsRemote={status.IsRemote}, HasRunningTasks={status.HasRunningTasks}, Port={status.Port}");
        if (status.HasRunningTasks) return new MutationOutcome("Can't change remote access while a task is running — finish or pause it first.");
        if (status.IsRemote == enabled) return new MutationOutcome(null);

        await ShutdownDaemonAsync().ConfigureAwait(true);
        LogToggleDiagnostic("ShutdownDaemonAsync completed");
        _isClientMode = false;

        // A fixed sleep here raced the old process's real shutdown time. A first fix attempt
        // replaced it with polling the old daemon's /api/version until the request failed — but
        // that misfired too: a request that times out because the daemon is briefly slow to
        // respond while mid-graceful-shutdown (still holding its listening socket and
        // single-instance mutex) looks identical, over HTTP, to a request that fails because
        // nothing is listening at all. Found live via ~/.aer/daemon-spawn.log: the respawn below
        // fired while the old process — in the OLD mode — was still actually running, hit
        // "Another instance of Aer.Daemon is already running.", and died immediately. The
        // single-instance guard itself (see Aer.Daemon/Program.cs's RunDaemonAsync) is an OS-named
        // mutex that only exists while some process holds it — polling for that directly is an
        // unambiguous signal, not a timing inference.
        var exited = await WaitForDaemonToExitAsync(cancellationToken).ConfigureAwait(true);
        LogToggleDiagnostic($"WaitForDaemonToExitAsync returned {exited}");
        if (!exited)
        {
            return new MutationOutcome("Aer.Daemon is taking a while to shut down — try again in a moment.");
        }

        var started = await SpawnDaemonProcessAsync(enabled ? "--remote" : "", cancellationToken).ConfigureAwait(true);
        LogToggleDiagnostic($"SpawnDaemonProcessAsync returned {started}");
        return started ? new MutationOutcome(null) : new MutationOutcome("Could not restart Aer.Daemon.");
    }

    private static void LogToggleDiagnostic(string line)
    {
        try
        {
            var aerDir = AerPaths.Root;
            Directory.CreateDirectory(aerDir);
            TryAppendLog(Path.Combine(aerDir, "toggle-diagnostic.log"), $"{DateTime.UtcNow:O} {line}");
        }
        catch
        {
            // Best-effort diagnostics only.
        }
    }

    /// <summary>
    /// Polls the daemon's single-instance mutex (same name Aer.Daemon/Program.cs's
    /// <c>RunDaemonAsync</c> creates) until no process holds it, or ~12s elapses. The daemon's own
    /// <c>HostOptions.ShutdownTimeout</c> is explicitly bounded to 3s (see
    /// <c>Aer.Daemon/Program.cs</c>'s <c>RunDaemonAsync</c>), so this only needs enough margin above
    /// that for the shutdown request's own round trip plus process teardown — not the old 30s
    /// default's worth. A mutex check has no false positives the way the HTTP-timing check it
    /// replaced did, so a longer budget here costs time, not correctness.
    /// </summary>
    private static async Task<bool> WaitForDaemonToExitAsync(CancellationToken cancellationToken)
    {
        var mutexName = $"Global\\AerDaemonMutex_{Environment.UserName}";

        for (int i = 0; i < 60; i++)
        {
            if (!Mutex.TryOpenExisting(mutexName, out var existing))
            {
                return true;
            }

            existing.Dispose();
            await Task.Delay(200, cancellationToken).ConfigureAwait(true);
        }

        return false;
    }

    public async Task ShutdownDaemonAsync()
    {
        if (_activeDaemonUrl != null)
        {
            try
            {
                await _httpClient.PostAsync($"{_activeDaemonUrl}/api/daemon/shutdown", null).ConfigureAwait(false);
            }
            catch
            {
                // Ignore
            }
        }
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

        var isLocallyHostedTask = _currentInFlightExecutions is not null && CurrentTaskDirectoryPath == taskDirectoryPath;

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
