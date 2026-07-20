using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aer.Adapters;
using Aer.Daemon;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Ui.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aer.Ui.Tests;

[Collection("DaemonIntegrationTests")]
public class DaemonIntegrationTests : IAsyncLifetime
{
    private Task? _daemonTask;
    private const string BaseUrl = "http://localhost:5050";
    private readonly HttpClient _client = new();
    private string? _tempTaskDirectory;

    public async ValueTask InitializeAsync()
    {
        // Start Daemon on port 5050
        _daemonTask = DaemonHost.RunDaemonAsync(new[] { "--port", "5050", "--no-mutex" });

        // Wait for daemon to spin up
        for (int i = 0; i < 30; i++)
        {
            try
            {
                var response = await _client.GetAsync($"{BaseUrl}/api/version", TestContext.Current.CancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }

        // Configure client authorization header
        var aerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aer");
        var tokenFile = Path.Combine(aerDir, "daemon.token");
        if (File.Exists(tokenFile))
        {
            var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        // Create a temporary task directory for testing
        _tempTaskDirectory = Path.Combine(Path.GetTempPath(), "aer_daemon_test_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempTaskDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        // Stop Daemon
        if (DaemonHost.App != null)
        {
            await DaemonHost.App.StopAsync();
        }

        if (_daemonTask != null)
        {
            await _daemonTask;
        }

        _client.Dispose();

        if (_tempTaskDirectory != null && Directory.Exists(_tempTaskDirectory))
        {
            try
            {
                Directory.Delete(_tempTaskDirectory, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    // M21 Phase 3 (issue #234): the Enable Remote Access view's toggle reads this field to know
    // whether the daemon it's already talking to is bound loopback-only or --remote — this test
    // daemon is started with neither flag (InitializeAsync's --port/--no-mutex only), so it must
    // report false.
    [Fact]
    public async Task GetVersion_ReportsIsRemote_FalseForALoopbackOnlyDaemon()
    {
        var response = await _client.GetAsync($"{BaseUrl}/api/version", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);
        var meta = await response.Content.ReadFromJsonAsync<DaemonVersionInfo>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(meta);
        Assert.False(meta.IsRemote);
    }

    [Fact]
    public async Task GetRecentTasks_ReturnsOk()
    {
        var response = await _client.GetAsync($"{BaseUrl}/api/tasks/recent", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);
        var recent = await response.Content.ReadFromJsonAsync<IReadOnlyList<string>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(recent);
    }

    [Fact]
    public async Task OpenTask_WithMissingDirectory_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync($"{BaseUrl}/api/tasks/open", new OpenTaskRequest(""), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OpenTask_WithInvalidDirectory_ReturnsBadRequest()
    {
        var invalidDir = Path.Combine(_tempTaskDirectory!, "non_existent_folder_abc_123");
        var response = await _client.PostAsJsonAsync($"{BaseUrl}/api/tasks/open", new OpenTaskRequest(invalidDir), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Pairing_Flow_Succeeds_And_Enables_Auth()
    {
        // 1. Get pairing code (authenticated via loopback token)
        var codeResponse = await _client.GetAsync($"{BaseUrl}/api/pairing/code", TestContext.Current.CancellationToken);
        Assert.True(codeResponse.IsSuccessStatusCode);
        var codeData = await codeResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var code = codeData.GetProperty("code").GetString();
        Assert.NotNull(code);

        // 2. Pair remote client (public POST, no auth headers on request client)
        using var remoteClient = new HttpClient();
        var pairRequest = new { Code = code, ClientName = "Test Mobile App" };
        var pairResponse = await remoteClient.PostAsJsonAsync($"{BaseUrl}/api/pairing/pair", pairRequest, TestContext.Current.CancellationToken);
        Assert.True(pairResponse.IsSuccessStatusCode);
        var pairData = await pairResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var pairedToken = pairData.GetProperty("token").GetString();
        Assert.NotNull(pairedToken);

        // 3. Make a request using the newly paired token (should be authorized)
        remoteClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pairedToken);
        var recentTasksResponse = await remoteClient.GetAsync($"{BaseUrl}/api/tasks/recent", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, recentTasksResponse.StatusCode);
    }

    [Fact]
    public async Task Pairing_With_Invalid_Code_Returns_BadRequest()
    {
        using var remoteClient = new HttpClient();
        var pairRequest = new { Code = "999999", ClientName = "Test Mobile App" };
        var pairResponse = await remoteClient.PostAsJsonAsync($"{BaseUrl}/api/pairing/pair", pairRequest, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, pairResponse.StatusCode);
    }

    [Fact]
    public async Task Pairing_Locks_Out_After_Max_Failed_Attempts()
    {
        // A real code is active, but every guess below is deliberately wrong — proving the
        // pairing endpoint can't be brute-forced across its 60s validity window: after enough
        // wrong guesses, even the correct code is rejected until a fresh one is generated.
        var codeResponse = await _client.GetAsync($"{BaseUrl}/api/pairing/code", TestContext.Current.CancellationToken);
        var codeData = await codeResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var code = codeData.GetProperty("code").GetString();
        var wrongCode = code == "000000" ? "111111" : "000000";

        using var remoteClient = new HttpClient();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrongResponse = await remoteClient.PostAsJsonAsync(
                $"{BaseUrl}/api/pairing/pair", new { Code = wrongCode, ClientName = "Attacker" }, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, wrongResponse.StatusCode);
        }

        // Attempts are now exhausted — even the real code must be rejected.
        var finalResponse = await remoteClient.PostAsJsonAsync(
            $"{BaseUrl}/api/pairing/pair", new { Code = code, ClientName = "Test Mobile App" }, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, finalResponse.StatusCode);
    }

    [Fact]
    public async Task Request_Without_Token_Is_Rejected_With_401()
    {
        using var remoteClient = new HttpClient();
        var response = await remoteClient.GetAsync($"{BaseUrl}/api/tasks/recent", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // M21 Phase 6 (#243): PairedClientsStore could add a client but never remove one — the missing
    // revocation path M20 deferred until "whichever milestone builds the actual remote client".
    private async Task<(string ClientId, string Token)> PairANewClientAsync(string name)
    {
        var codeResponse = await _client.GetAsync($"{BaseUrl}/api/pairing/code", TestContext.Current.CancellationToken);
        var codeData = await codeResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var code = codeData.GetProperty("code").GetString();

        using var remoteClient = new HttpClient();
        var pairResponse = await remoteClient.PostAsJsonAsync(
            $"{BaseUrl}/api/pairing/pair", new { Code = code, ClientName = name }, TestContext.Current.CancellationToken);
        var pairData = await pairResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var token = pairData.GetProperty("token").GetString()!;

        var clientsResponse = await _client.GetAsync($"{BaseUrl}/api/pairing/clients", TestContext.Current.CancellationToken);
        var clients = await clientsResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var clientId = clients.EnumerateArray().Last(c => c.GetProperty("name").GetString() == name).GetProperty("clientId").GetString()!;

        return (clientId, token);
    }

    [Fact]
    public async Task RevokePairedClient_CausesNextRequest_ToBeUnauthorized()
    {
        var (clientId, token) = await PairANewClientAsync("Revocation Test Device");

        using var pairedClient = new HttpClient();
        pairedClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var beforeRevoke = await pairedClient.GetAsync($"{BaseUrl}/api/tasks/recent", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, beforeRevoke.StatusCode);

        var deleteResponse = await _client.DeleteAsync($"{BaseUrl}/api/pairing/clients/{clientId}", TestContext.Current.CancellationToken);
        Assert.True(deleteResponse.IsSuccessStatusCode);

        var afterRevoke = await pairedClient.GetAsync($"{BaseUrl}/api/tasks/recent", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task RevokeUnknownClientId_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync($"{BaseUrl}/api/pairing/clients/does-not-exist", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PairedClient_CannotListOrRevokeOtherDevices()
    {
        var (_, token) = await PairANewClientAsync("Non-Owner Device");
        using var pairedClient = new HttpClient();
        pairedClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var listResponse = await pairedClient.GetAsync($"{BaseUrl}/api/pairing/clients", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, listResponse.StatusCode);

        var deleteResponse = await pairedClient.DeleteAsync($"{BaseUrl}/api/pairing/clients/does-not-exist", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, deleteResponse.StatusCode);
    }

    // M21 Phase 2 (#232): /api/tasks/artifact — the only way a client with no access to the
    // daemon host's filesystem (Aer.Mobile) can see what it's approving.
    private static readonly StepId WorkerStep = new("worker");

    private static async Task<string> CreateTaskDirectoryWithArtifactAsync(
        string executionId, string fileName, string content, CancellationToken cancellationToken)
    {
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("single-step"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(WorkerStep, "worker", ["goal"], [fileName], DependsOn: [], RetryPolicy: new RetryPolicy(1))]));

        var taskDirectory = Path.Combine(Path.GetTempPath(), $"aer_daemon_artifact_test_{Guid.NewGuid():N}");
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(taskDirectory, "snapshot.json"), cancellationToken);

        var request = new ExecutionRequest(
            new ExecutionId(executionId),
            new WorkflowId("wf-1"),
            WorkerStep,
            "worker",
            Inputs: [],
            Outputs: [fileName],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        await using (var writer = new FlowEventLogWriter(Path.Combine(taskDirectory, "flow.jsonl")))
        {
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), cancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(new ExecutionId(executionId)), cancellationToken);
        }

        var outputDirectory = Path.Combine(taskDirectory, "artifacts", $"execution_{executionId}");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, fileName), content, cancellationToken);

        return taskDirectory;
    }

    [Fact]
    public async Task GetArtifact_WithKnownExecutionAndFile_ReturnsItsContent()
    {
        var taskDirectory = await CreateTaskDirectoryWithArtifactAsync(
            "exec-1", "result.txt", "The output.", TestContext.Current.CancellationToken);

        var response = await _client.GetAsync(
            $"{BaseUrl}/api/tasks/artifact?directoryPath={Uri.EscapeDataString(taskDirectory)}&executionId=exec-1&fileName=result.txt",
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("The output.", body.GetProperty("content").GetString());
        Assert.False(body.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task GetArtifact_WithFileNameNotInExecutionsOutputs_ReturnsNotFound()
    {
        var taskDirectory = await CreateTaskDirectoryWithArtifactAsync(
            "exec-1", "result.txt", "The output.", TestContext.Current.CancellationToken);

        // Neither a real output of exec-1 nor a real path — this is the path-traversal guard:
        // fileName must appear in the execution's own recorded OutputFiles, nothing else.
        var response = await _client.GetAsync(
            $"{BaseUrl}/api/tasks/artifact?directoryPath={Uri.EscapeDataString(taskDirectory)}&executionId=exec-1&fileName=..%2f..%2fsecrets.txt",
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetArtifact_WithMissingQueryParameters_ReturnsBadRequest()
    {
        var response = await _client.GetAsync(
            $"{BaseUrl}/api/tasks/artifact?directoryPath=&executionId=&fileName=",
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<string> CreatePausedTaskDirectoryAsync(string executionId, CancellationToken cancellationToken)
    {
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("single-step-gate"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(WorkerStep, "worker", ["goal"], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1), PausePoint: new PausePoint([]))]));

        var taskDirectory = Path.Combine(Path.GetTempPath(), $"aer_daemon_paused_test_{Guid.NewGuid():N}");
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(taskDirectory, "snapshot.json"), cancellationToken);

        var request = new ExecutionRequest(
            new ExecutionId(executionId),
            new WorkflowId("wf-1"),
            WorkerStep,
            "worker",
            Inputs: [],
            Outputs: ["out"],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        await using (var writer = new FlowEventLogWriter(Path.Combine(taskDirectory, "flow.jsonl")))
        {
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), cancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(new ExecutionId(executionId)), cancellationToken);
            await writer.AppendAsync(new FlowEvent.WorkflowPaused(new ExecutionId(executionId), WorkerStep), cancellationToken);
        }

        return taskDirectory;
    }

    private static async Task<string> WriteRejectableBindingsAsync(CancellationToken cancellationToken)
    {
        // "claude" (the real, registered adapter -- the daemon has no "shell" stub) resolves to a
        // command-line descriptor only (ClaudeWorkerAdapter.Resolve builds args, never spawns a
        // process) -- WorkerBindingResolver.Resolve calls this eagerly for every entry regardless of
        // decision type, but Reject itself never dispatches the resolved binding, so no real `claude`
        // process is ever started by this test.
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["worker"] = new WorkerBindingConfigEntry(
                "claude", new WorkerContract("worker", ["goal"], [new ProducedOutput("out")], []),
                "irrelevant, never dispatched", TimeSpan.FromSeconds(30)),
        };

        var directory = Path.Combine(Path.GetTempPath(), $"aer_daemon_bindings_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config), cancellationToken);
        return path;
    }

    [Fact]
    public async Task Reject_TriggersASecondWebSocketBroadcast_SoAPhoneSeesTheDecisionLand()
    {
        // The connect-time snapshot push (proven by the DirectoryPath test above) is only half of
        // what Aer.Mobile's decision inbox depends on -- the other half, never previously exercised
        // by any test, is that POSTing a decision actually triggers a *second* broadcast to every
        // connected socket. /api/tasks/decide dispatches on a background Task.Run and returns 200
        // immediately (fire-and-forget, see Program.cs), so a missing broadcast here would look
        // identical to the phone: 200 OK, card never updates. See TaskSession.DecideAsync's
        // in-process fallback, which reaches the daemon's reopenTaskAsync -> BroadcastStateAsync path.
        const string executionId = "exec-reject-1";
        var taskDirectory = await CreatePausedTaskDirectoryAsync(executionId, TestContext.Current.CancellationToken);

        var openResponse = await _client.PostAsJsonAsync(
            $"{BaseUrl}/api/tasks/open", new OpenTaskRequest(taskDirectory), TestContext.Current.CancellationToken);
        Assert.True(openResponse.IsSuccessStatusCode);

        // DecideCommand always loads a bindings file, regardless of decision type (Aer.Cli's
        // DecideCommand.cs) -- set it directly on the daemon's DI-registered BindingsPathHolder,
        // *after* /api/tasks/open (which overwrites it from LoadLastBindingsFilePathAsync's own
        // remembered value), rather than through /api/tasks/run, which would persist to the real
        // per-user %APPDATA%\Aer.Ui\recent-task-directories.json convenience file
        // (LocalUiConfigurationStore.CreateDefault(), Program.cs:113) -- this test must not leave
        // that behind on whatever machine runs it.
        var bindingsFilePath = await WriteRejectableBindingsAsync(TestContext.Current.CancellationToken);
        DaemonHost.App!.Services.GetRequiredService<BindingsPathHolder>().BindingsFilePath = bindingsFilePath;

        var token = _client.DefaultRequestHeaders.Authorization!.Parameter!;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://localhost:5050/api/ws?token={token}"), TestContext.Current.CancellationToken);

        var buffer = new byte[1024 * 64];
        var first = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);
        var firstPayload = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, first.Count)).RootElement;
        Assert.Equal("Paused", firstPayload.GetProperty("State").GetProperty("Status").GetString());

        var decideResponse = await _client.PostAsJsonAsync(
            $"{BaseUrl}/api/tasks/decide",
            new DecideTaskRequest(taskDirectory, WorkerStep.Value, executionId, DecisionType.Reject),
            TestContext.Current.CancellationToken);
        Assert.True(decideResponse.IsSuccessStatusCode);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var second = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token);
        var secondPayload = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, second.Count)).RootElement;

        Assert.Equal("Terminal", secondPayload.GetProperty("State").GetProperty("Status").GetString());
        Assert.Equal(taskDirectory, secondPayload.GetProperty("DirectoryPath").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WebSocketSnapshot_IncludesDirectoryPath_SoAClientThatNeverCalledOpenCanStillDecide()
    {
        // A client that only ever observes the WS stream (Aer.Mobile — the task was opened by the
        // desktop, not by this client) has no other way to learn the directoryPath that
        // /api/tasks/decide and /api/tasks/cancel require.
        var taskDirectory = await CreateTaskDirectoryWithArtifactAsync(
            "exec-1", "result.txt", "The output.", TestContext.Current.CancellationToken);
        var openResponse = await _client.PostAsJsonAsync(
            $"{BaseUrl}/api/tasks/open", new OpenTaskRequest(taskDirectory), TestContext.Current.CancellationToken);
        Assert.True(openResponse.IsSuccessStatusCode);

        var token = _client.DefaultRequestHeaders.Authorization!.Parameter!;

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://localhost:5050/api/ws?token={token}"), TestContext.Current.CancellationToken);

        var buffer = new byte[1024 * 64];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);
        var json = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
        var payload = JsonDocument.Parse(json).RootElement;

        Assert.Equal(taskDirectory, payload.GetProperty("DirectoryPath").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetTemplates_ReturnsCatalogAndVendorPresence()
    {
        var response = await _client.GetAsync($"{BaseUrl}/api/templates", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var hasTemplates = body.TryGetProperty("templates", out var templates) || body.TryGetProperty("Templates", out templates);
        Assert.True(hasTemplates);
        Assert.Equal(2, templates.GetArrayLength());

        var hasVendors = body.TryGetProperty("availableVendors", out var vendors) || body.TryGetProperty("AvailableVendors", out vendors);
        Assert.True(hasVendors);
        Assert.True(vendors.GetArrayLength() > 0);
    }

    [Fact]
    public async Task RunTemplate_MaterializesAndStartsTaskWithoutCallerSuppliedPaths()
    {
        var request = new RunTemplateRequest(
            TemplateId: "solo-run",
            PrimaryAdapter: "claude",
            TaskName: "test-template-task-" + Guid.NewGuid().ToString("N"));

        var response = await _client.PostAsJsonAsync($"{BaseUrl}/api/templates/run", request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var hasProp = body.TryGetProperty("taskDirectoryPath", out var dirProp) || body.TryGetProperty("TaskDirectoryPath", out dirProp);
        Assert.True(hasProp);
        var dirPath = dirProp.GetString();
        Assert.NotNull(dirPath);
        Assert.True(Directory.Exists(dirPath));
        Assert.True(File.Exists(Path.Combine(dirPath, "workflow.json")));
        Assert.True(File.Exists(Path.Combine(dirPath, "bindings.json")));
    }
}
