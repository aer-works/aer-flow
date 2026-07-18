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
public record DecideTaskRequest(
    string DirectoryPath,
    string StepId,
    string ExecutionId,
    DecisionType DecisionType,
    string? TargetStepId = null,
    string? RevisionFilePath = null,
    string? SupplementaryWorker = null,
    string? SupplementaryOutputName = null);
public record CancelTaskRequest(string DirectoryPath, string? ExecutionId = null);
public record DaemonVersionInfo(string Version, bool HasRunningTasks);

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

    private readonly LocalUiConfigurationStore _configurationStore;
    private readonly IReadOnlyDictionary<string, IWorkerAdapter> _adapters;
    private readonly Func<string?> _bindingsFilePathProvider;
    private readonly Action _mutationStarted;
    private readonly Action _mutationFailed;
    private readonly Func<string, CancellationToken, Task> _reopenTaskAsync;
    private readonly string? _daemonUrl;

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly SynchronizationContext? _syncContext = SynchronizationContext.Current;

    private bool _isClientMode;
    private string? _activeDaemonUrl;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _wsCts;

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
        string? daemonUrl = null)
    {
        _configurationStore = configurationStore;
        _adapters = adapters;
        ViewModel = viewModel;
        _bindingsFilePathProvider = bindingsFilePathProvider;
        _mutationStarted = mutationStarted;
        _mutationFailed = mutationFailed;
        _reopenTaskAsync = reopenTaskAsync;
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

    private (string Url, string Token)? GetDaemonConnectionInfo()
    {
        if (string.IsNullOrEmpty(_daemonUrl)) return null;

        var aerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aer");
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
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                var process = Process.Start(startInfo);
                if (process != null)
                {
                    _ = Task.Run(() => SuperviseDaemonProcessAsync(process), CancellationToken.None);

                    for (int i = 0; i < 30; i++)
                    {
                        try
                        {
                            var newConn = GetDaemonConnectionInfo();
                            _activeDaemonUrl = newConn?.Url ?? _daemonUrl;
                            var newToken = newConn?.Token;
                            if (newToken != null)
                            {
                                ConfigureHttpClientHeaders(newToken);
                            }

                            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/version", cancellationToken).ConfigureAwait(true);
                            if (response.IsSuccessStatusCode)
                            {
                                _isClientMode = true;
                                await StartWebSocketListenerAsync(_activeDaemonUrl, newToken, cancellationToken).ConfigureAwait(true);
                                return true;
                            }
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
    }

    private async Task ReceiveWebSocketDataAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        try
        {
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(true);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var projection = JsonSerializer.Deserialize<TaskProjection>(json, new JsonSerializerOptions
                    {
                        Converters = { new JsonStringEnumConverter() },
                        PropertyNameCaseInsensitive = true
                    });

                    if (projection != null && CurrentTaskDirectoryPath != null)
                    {
                        if (_syncContext != null)
                        {
                            _syncContext.Post(_ => UpdateProjection(projection), null);
                        }
                        else
                        {
                            UpdateProjection(projection);
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
                    var projection = await response.Content.ReadFromJsonAsync<TaskProjection>(cancellationToken: cancellationToken).ConfigureAwait(true);
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
    public async Task<MutationOutcome> RunAsync(
        string taskDirectoryPath, string? workflowTemplateFilePath, string bindingsFilePath, CancellationToken cancellationToken = default)
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
                () => RunCommand.ExecuteAsync(options, _adapters, inFlightExecutions, hostStopSource.Token), hostStopSource.Token);
            CurrentPumpTask = pumpTask;
            await pumpTask.ConfigureAwait(true);

            ViewModel.RunStatusText = string.Empty;

            if (!string.IsNullOrWhiteSpace(workflowTemplateFilePath))
            {
                await _configurationStore.RecordWorkflowTemplateFilePathAsync(workflowTemplateFilePath, cancellationToken).ConfigureAwait(true);
            }

            await _configurationStore.RecordBindingsFilePathAsync(bindingsFilePath, cancellationToken).ConfigureAwait(true);
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
    /// The paused-step decision mutation: dispatches the Decide command to the daemon or executes in-process.
    /// </summary>
    public async Task<MutationOutcome> DecideAsync(
        string taskDirectoryPath,
        StepId stepId,
        ExecutionId executionId,
        DecisionType decisionType,
        StepId? targetStepId,
        string? revisionFilePath,
        string? supplementaryWorker,
        string? supplementaryOutputName,
        CancellationToken cancellationToken = default)
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
                () => DecideCommand.ExecuteAsync(options, _adapters, inFlightExecutions, hostStopSource.Token), hostStopSource.Token);
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
