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
using Aer.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aer.Ui.Tests;

[Collection("DaemonIntegrationTests")]
public class DaemonIntegrationTests : IAsyncLifetime
{
    private Task? _daemonTask;
    private string _baseUrl = "";
    private readonly HttpClient _client = new();
    private string? _tempTaskDirectory;

    /// <summary>The daemon's dynamically-assigned base URL (issue #296), reused for the WebSocket
    /// endpoints below rather than the old hardcoded "ws://localhost:5050".</summary>
    private string WsBaseUrl => "ws" + _baseUrl["http".Length..];

    public async ValueTask InitializeAsync()
    {
        // Start Daemon on a dynamically OS-assigned port (issue #296) — a hardcoded port collides
        // whenever two test runs happen to overlap.
        (_daemonTask, _baseUrl) = await DaemonTestHost.StartAsync();

        // Wait for daemon to spin up
        for (int i = 0; i < 30; i++)
        {
            try
            {
                var response = await _client.GetAsync($"{_baseUrl}/api/version", TestContext.Current.CancellationToken);
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
        var response = await _client.GetAsync($"{_baseUrl}/api/version", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);
        var meta = await response.Content.ReadFromJsonAsync<DaemonVersionInfo>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(meta);
        Assert.False(meta.IsRemote);
    }

    [Fact]
    public async Task GetRecentTasks_ReturnsOk()
    {
        var response = await _client.GetAsync($"{_baseUrl}/api/tasks/recent", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);
        var recent = await response.Content.ReadFromJsonAsync<IReadOnlyList<string>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(recent);
    }

    [Fact]
    public async Task OpenTask_WithMissingDirectory_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/tasks/open", new OpenTaskRequest(""), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OpenTask_WithInvalidDirectory_ReturnsBadRequest()
    {
        var invalidDir = Path.Combine(_tempTaskDirectory!, "non_existent_folder_abc_123");
        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/tasks/open", new OpenTaskRequest(invalidDir), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Pairing_Flow_Succeeds_And_Enables_Auth()
    {
        // 1. Get pairing code (authenticated via loopback token)
        var codeResponse = await _client.GetAsync($"{_baseUrl}/api/pairing/code", TestContext.Current.CancellationToken);
        Assert.True(codeResponse.IsSuccessStatusCode);
        var codeData = await codeResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var code = codeData.GetProperty("code").GetString();
        Assert.NotNull(code);

        // 2. Pair remote client (public POST, no auth headers on request client)
        using var remoteClient = new HttpClient();
        var pairRequest = new { Code = code, ClientName = "Test Mobile App" };
        var pairResponse = await remoteClient.PostAsJsonAsync($"{_baseUrl}/api/pairing/pair", pairRequest, TestContext.Current.CancellationToken);
        Assert.True(pairResponse.IsSuccessStatusCode);
        var pairData = await pairResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var pairedToken = pairData.GetProperty("token").GetString();
        Assert.NotNull(pairedToken);

        // 3. Make a request using the newly paired token (should be authorized)
        remoteClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pairedToken);
        var recentTasksResponse = await remoteClient.GetAsync($"{_baseUrl}/api/tasks/recent", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, recentTasksResponse.StatusCode);
    }

    [Fact]
    public async Task Pairing_With_Invalid_Code_Returns_BadRequest()
    {
        using var remoteClient = new HttpClient();
        var pairRequest = new { Code = "999999", ClientName = "Test Mobile App" };
        var pairResponse = await remoteClient.PostAsJsonAsync($"{_baseUrl}/api/pairing/pair", pairRequest, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, pairResponse.StatusCode);
    }

    [Fact]
    public async Task Pairing_Locks_Out_After_Max_Failed_Attempts()
    {
        // A real code is active, but every guess below is deliberately wrong — proving the
        // pairing endpoint can't be brute-forced across its 60s validity window: after enough
        // wrong guesses, even the correct code is rejected until a fresh one is generated.
        var codeResponse = await _client.GetAsync($"{_baseUrl}/api/pairing/code", TestContext.Current.CancellationToken);
        var codeData = await codeResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var code = codeData.GetProperty("code").GetString();
        var wrongCode = code == "000000" ? "111111" : "000000";

        using var remoteClient = new HttpClient();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrongResponse = await remoteClient.PostAsJsonAsync(
                $"{_baseUrl}/api/pairing/pair", new { Code = wrongCode, ClientName = "Attacker" }, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, wrongResponse.StatusCode);
        }

        // Attempts are now exhausted — even the real code must be rejected.
        var finalResponse = await remoteClient.PostAsJsonAsync(
            $"{_baseUrl}/api/pairing/pair", new { Code = code, ClientName = "Test Mobile App" }, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, finalResponse.StatusCode);
    }

    [Fact]
    public async Task Request_Without_Token_Is_Rejected_With_401()
    {
        using var remoteClient = new HttpClient();
        var response = await remoteClient.GetAsync($"{_baseUrl}/api/tasks/recent", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // M21 Phase 6 (#243): PairedClientsStore could add a client but never remove one — the missing
    // revocation path M20 deferred until "whichever milestone builds the actual remote client".
    private async Task<(string ClientId, string Token)> PairANewClientAsync(string name)
    {
        var codeResponse = await _client.GetAsync($"{_baseUrl}/api/pairing/code", TestContext.Current.CancellationToken);
        var codeData = await codeResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var code = codeData.GetProperty("code").GetString();

        using var remoteClient = new HttpClient();
        var pairResponse = await remoteClient.PostAsJsonAsync(
            $"{_baseUrl}/api/pairing/pair", new { Code = code, ClientName = name }, TestContext.Current.CancellationToken);
        var pairData = await pairResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var token = pairData.GetProperty("token").GetString()!;

        var clientsResponse = await _client.GetAsync($"{_baseUrl}/api/pairing/clients", TestContext.Current.CancellationToken);
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
        var beforeRevoke = await pairedClient.GetAsync($"{_baseUrl}/api/tasks/recent", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, beforeRevoke.StatusCode);

        var deleteResponse = await _client.DeleteAsync($"{_baseUrl}/api/pairing/clients/{clientId}", TestContext.Current.CancellationToken);
        Assert.True(deleteResponse.IsSuccessStatusCode);

        var afterRevoke = await pairedClient.GetAsync($"{_baseUrl}/api/tasks/recent", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task RevokeUnknownClientId_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync($"{_baseUrl}/api/pairing/clients/does-not-exist", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PairedClient_CannotListOrRevokeOtherDevices()
    {
        var (_, token) = await PairANewClientAsync("Non-Owner Device");
        using var pairedClient = new HttpClient();
        pairedClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var listResponse = await pairedClient.GetAsync($"{_baseUrl}/api/pairing/clients", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, listResponse.StatusCode);

        var deleteResponse = await pairedClient.DeleteAsync($"{_baseUrl}/api/pairing/clients/does-not-exist", TestContext.Current.CancellationToken);
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
            $"{_baseUrl}/api/tasks/artifact?directoryPath={Uri.EscapeDataString(taskDirectory)}&executionId=exec-1&fileName=result.txt",
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
            $"{_baseUrl}/api/tasks/artifact?directoryPath={Uri.EscapeDataString(taskDirectory)}&executionId=exec-1&fileName=..%2f..%2fsecrets.txt",
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetArtifact_WithMissingQueryParameters_ReturnsBadRequest()
    {
        var response = await _client.GetAsync(
            $"{_baseUrl}/api/tasks/artifact?directoryPath=&executionId=&fileName=",
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
            $"{_baseUrl}/api/tasks/open", new OpenTaskRequest(taskDirectory), TestContext.Current.CancellationToken);
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
        await socket.ConnectAsync(new Uri($"{WsBaseUrl}/api/ws?token={token}"), TestContext.Current.CancellationToken);

        var buffer = new byte[1024 * 64];
        var first = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);
        var firstPayload = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, first.Count)).RootElement;
        Assert.Equal("Paused", firstPayload.GetProperty("State").GetProperty("Status").GetString());

        var decideResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/decide",
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
            $"{_baseUrl}/api/tasks/open", new OpenTaskRequest(taskDirectory), TestContext.Current.CancellationToken);
        Assert.True(openResponse.IsSuccessStatusCode);

        var token = _client.DefaultRequestHeaders.Authorization!.Parameter!;

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"{WsBaseUrl}/api/ws?token={token}"), TestContext.Current.CancellationToken);

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
        var response = await _client.GetAsync($"{_baseUrl}/api/templates", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var hasTemplates = body.TryGetProperty("templates", out var templates) || body.TryGetProperty("Templates", out templates);
        Assert.True(hasTemplates);
        Assert.Equal(5, templates.GetArrayLength());

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

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/templates/run", request, TestContext.Current.CancellationToken);
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

    [Theory]
    [InlineData("../../escaped-task")]
    [InlineData("..\\..\\escaped-task")]
    public async Task RunTemplate_WithPathTraversalTaskName_ReturnsBadRequest(string maliciousTaskName)
    {
        // Review follow-up (issue #250): TaskName used to be Path.Combine'd into the daemon-owned
        // tasks root with no containment check -- a crafted name could escape ~/.aer/tasks entirely
        // and make the daemon create/write files anywhere it can reach. This is exactly the
        // filesystem access the milestone's own design says a caller with only TemplateId/TaskName
        // (no real paths) should never get.
        if (maliciousTaskName.Contains('\\') && !OperatingSystem.IsWindows())
        {
            // '\' is not a path separator outside Windows, so this input never traverses out of the
            // tasks root there -- it's just a literal (contained) folder name, and OK is correct.
            return;
        }

        var request = new RunTemplateRequest(TemplateId: "solo-run", PrimaryAdapter: "claude", TaskName: maliciousTaskName);

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/templates/run", request, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DecideWithArtifactReference_FileNameNotInExecutionsOutputs_ReturnsBadRequest()
    {
        // Same path-traversal guard as GetArtifact_WithFileNameNotInExecutionsOutputs_ReturnsNotFound
        // above, but for /api/tasks/decide's ArtifactReference resolution (M22 Phase 5) -- it used to
        // Path.Combine the caller-supplied FileName straight into the resolved output directory with
        // no check that it names a real output of that execution, letting a remote client (the exact
        // audience Phase 5 exists to serve without host filesystem access) pull an arbitrary host file
        // in as "reviewer feedback".
        const string executionId = "exec-artifact-ref-1";
        var taskDirectory = await CreatePausedTaskDirectoryAsync(executionId, TestContext.Current.CancellationToken);
        var outputDirectory = Path.Combine(taskDirectory, "artifacts", $"execution_{executionId}");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "out"), "the real output", TestContext.Current.CancellationToken);

        var decideResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/decide",
            new DecideTaskRequest(
                taskDirectory, WorkerStep.Value, executionId, DecisionType.Reject,
                ArtifactReference: new ArtifactReference(executionId, "../../../secrets.txt")),
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, decideResponse.StatusCode);
    }

    [Fact]
    public async Task DecideWithArtifactReference_WithKnownExecutionAndFile_IsAccepted()
    {
        const string executionId = "exec-artifact-ref-2";
        var taskDirectory = await CreatePausedTaskDirectoryAsync(executionId, TestContext.Current.CancellationToken);
        var outputDirectory = Path.Combine(taskDirectory, "artifacts", $"execution_{executionId}");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "out"), "the real output", TestContext.Current.CancellationToken);

        var decideResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/decide",
            new DecideTaskRequest(
                taskDirectory, WorkerStep.Value, executionId, DecisionType.Reject,
                ArtifactReference: new ArtifactReference(executionId, "out")),
            TestContext.Current.CancellationToken);

        Assert.True(decideResponse.IsSuccessStatusCode);
    }

    // M24 Phase 1 (#262): Interactive Sessions endpoint coverage. These deliberately never send an
    // InitialMessage/Message that would reach ExecuteSessionTurnAsync's real vendor dispatch --
    // that path shells out to whatever CLI the resolved adapter names, which isn't something a
    // default (non-smoke) test run can assume is installed or authenticated on the host (see
    // CLAUDE.md's live-vendor-smoke-tests section). Everything below only exercises
    // materialization, persistence, and request validation, which never touch a vendor process.
    private async Task<(string SessionId, string TaskDirectoryPath)> StartASessionAsync(string? taskName = null)
    {
        var request = new StartSessionRequest(
            Adapter: "claude",
            TaskName: taskName ?? "test-session-" + Guid.NewGuid().ToString("N"));

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/start", request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        return (metadata.SessionId, metadata.TaskDirectoryPath);
    }

    [Fact]
    public async Task StartSession_WithNoInitialMessage_MaterializesAndReturnsDirectoryPath()
    {
        var (sessionId, taskDirectoryPath) = await StartASessionAsync();

        Assert.NotEmpty(sessionId);
        Assert.True(Directory.Exists(taskDirectoryPath));
        Assert.True(File.Exists(Path.Combine(taskDirectoryPath, "workflow.json")));
        Assert.True(File.Exists(Path.Combine(taskDirectoryPath, "bindings.json")));
        Assert.True(File.Exists(Path.Combine(taskDirectoryPath, ".aer", "session.json")));
    }

    [Fact]
    public async Task StartSession_ThenGetById_ReturnsTheSamePersistedSession()
    {
        var (sessionId, taskDirectoryPath) = await StartASessionAsync();

        var getResponse = await _client.GetAsync($"{_baseUrl}/api/sessions/{sessionId}", TestContext.Current.CancellationToken);
        Assert.True(getResponse.IsSuccessStatusCode);

        var metadata = await getResponse.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        Assert.Equal(sessionId, metadata.SessionId);
        Assert.Equal(taskDirectoryPath, metadata.TaskDirectoryPath);
    }

    [Fact]
    public async Task GetById_ForAnUnknownSessionId_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"{_baseUrl}/api/sessions/does-not-exist-{Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListSessions_IncludesAJustStartedSession()
    {
        var (sessionId, _) = await StartASessionAsync();

        var response = await _client.GetAsync($"{_baseUrl}/api/sessions", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var sessions = await response.Content.ReadFromJsonAsync<List<SessionMetadata>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sessions);
        Assert.Contains(sessions, s => s.SessionId == sessionId);
    }

    [Fact]
    public async Task WebSocketSnapshot_IncludesSessionId_ForASessionDirectory()
    {
        // Aer.Mobile's chat UI (issue #262 follow-up): a phone whose _openDirectoryPath was seeded
        // from another client's push (never having called /api/sessions/start itself) has no other
        // way to learn this directory is an interactive session, or which SessionId to fetch turns
        // for, without this sibling -- see SendStateAsync's remarks in Program.cs.
        //
        // Deliberately not using StartASessionAsync/POST /api/sessions/start here: a session
        // materialized with no initial message never actually runs (Aer.Daemon's
        // ExecuteSessionTurnAsync only fires when InitialMessage is set), so it has no snapshot.json
        // yet -- TaskProjectionLoader.LoadAsync throws InvalidTaskDirectoryException for it, and
        // /api/ws's on-connect push silently never fires (LastLoadSucceeded stays false). That's a
        // pre-existing daemon quirk, not something to route around by invoking a real vendor CLI in
        // a test (this suite never does that -- see the other CreateXxxDirectoryAsync helpers,
        // which all hand-write snapshot.json/flow.jsonl directly). This helper does the same, plus a
        // hand-written .aer/session.json, standing in for what a session's first real completed turn
        // would leave behind.
        const string sessionId = "test-session-ws-1";
        var taskDirectory = await CreateSessionTaskDirectoryAsync(sessionId, TestContext.Current.CancellationToken);

        var openResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/open", new OpenTaskRequest(taskDirectory), TestContext.Current.CancellationToken);
        Assert.True(openResponse.IsSuccessStatusCode);

        var token = _client.DefaultRequestHeaders.Authorization!.Parameter!;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"{WsBaseUrl}/api/ws?token={token}"), TestContext.Current.CancellationToken);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var buffer = new byte[1024 * 64];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token);
        var payload = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement;

        Assert.Equal(taskDirectory, payload.GetProperty("DirectoryPath").GetString());
        Assert.Equal(sessionId, payload.GetProperty("SessionId").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
    }

    private static async Task<string> CreateSessionTaskDirectoryAsync(string sessionId, CancellationToken cancellationToken)
    {
        var taskDirectory = await CreateTaskDirectoryWithArtifactAsync("exec-session-1", "response.md", "Hi there.", cancellationToken);

        var metadata = new SessionMetadata(
            sessionId,
            taskDirectory,
            CurrentAdapter: "claude",
            CurrentVendorSessionId: null,
            Model: null,
            WorkingDirectory: null,
            TurnCount: 1,
            SafetyCeiling: InteractiveSessionMaterializer.DefaultSafetyCeiling,
            MinimalOverhead: false,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Turns: [new SessionTurn(0, "claude", "hello", "hi there", DateTimeOffset.UtcNow, false, false)]);
        await InteractiveSessionMaterializer.SaveMetadataAsync(metadata, Path.Combine(taskDirectory, ".aer", "session.json"), cancellationToken);

        return taskDirectory;
    }

    [Fact]
    public async Task WebSocketSnapshot_OmitsSessionId_ForAnOrdinaryTaskDirectory()
    {
        // A plain (non-session) task directory has no .aer/session.json -- confirms the new sibling
        // is additive and doesn't leak a stale/wrong SessionId onto unrelated task pushes.
        var taskDirectory = await CreateTaskDirectoryWithArtifactAsync(
            "exec-plain-1", "result.txt", "The output.", TestContext.Current.CancellationToken);
        var openResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/open", new OpenTaskRequest(taskDirectory), TestContext.Current.CancellationToken);
        Assert.True(openResponse.IsSuccessStatusCode);

        var token = _client.DefaultRequestHeaders.Authorization!.Parameter!;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"{WsBaseUrl}/api/ws?token={token}"), TestContext.Current.CancellationToken);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var buffer = new byte[1024 * 64];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token);
        var payload = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement;

        Assert.Equal(taskDirectory, payload.GetProperty("DirectoryPath").GetString());
        Assert.False(payload.TryGetProperty("SessionId", out _));

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SendSessionMessage_WithEmptyMessage_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(DirectoryPath: _tempTaskDirectory, Message: ""),
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SendSessionMessage_WithNeitherDirectoryPathNorSessionId_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(Message: "hello"),
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SendSessionMessage_ForADirectoryThatIsNotASessionDirectory_ReturnsBadRequest()
    {
        // A real, existing directory (satisfies Directory.Exists) that was never materialized by
        // InteractiveSessionMaterializer -- no .aer/session.json, so metadata load must fail closed.
        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(DirectoryPath: _tempTaskDirectory, Message: "hello"),
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("gemini")]
    public async Task GetAdapterCapabilities_ReturnsOkWithTheRequestedVendor(string adapter)
    {
        // Neither adapter's DiscoverCapabilitiesAsync shells out in a way that throws when its CLI
        // is missing or unauthenticated -- Claude's is filesystem-only, Gemini's degrades each
        // subcommand to null on Win32Exception/InvalidOperationException (GeminiWorkerAdapter.cs) --
        // so this is safe to assert on regardless of what's installed on the host.
        var response = await _client.GetAsync($"{_baseUrl}/api/adapters/capabilities?adapter={adapter}", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var capabilities = await response.Content.ReadFromJsonAsync<WorkerCapabilities>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(capabilities);
        Assert.Equal(adapter, capabilities.Vendor);
        Assert.Contains(capabilities.Items, i => i.Name == "/compact");
    }

    [Fact]
    public async Task GetAdapterCapabilities_WithUnknownAdapterName_FallsBackToClaude()
    {
        var response = await _client.GetAsync($"{_baseUrl}/api/adapters/capabilities?adapter=not-a-real-vendor", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var capabilities = await response.Content.ReadFromJsonAsync<WorkerCapabilities>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(capabilities);
        Assert.Equal("claude", capabilities.Vendor);
    }

    [Fact]
    public async Task GetSessionCommands_ForAStartedSession_ReturnsCapabilities()
    {
        var (sessionId, _) = await StartASessionAsync();

        var response = await _client.GetAsync($"{_baseUrl}/api/sessions/{sessionId}/commands", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var capabilities = await response.Content.ReadFromJsonAsync<WorkerCapabilities>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(capabilities);
        Assert.Equal("claude", capabilities.Vendor);
    }

    private sealed record SessionCommandsResponse(string Vendor, List<WorkerCapabilityItem> Items, List<string> Models, List<string> RecentlyUsed);

    [Fact]
    public async Task RecordCommandUsed_ThenGetSessionCommands_SurfacesItAsRecentlyUsed()
    {
        var (sessionId, _) = await StartASessionAsync();

        var recordResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/{sessionId}/commands/record", new RecordCommandUsedRequest("/compact"), TestContext.Current.CancellationToken);
        Assert.True(recordResponse.IsSuccessStatusCode);

        var response = await _client.GetAsync($"{_baseUrl}/api/sessions/{sessionId}/commands", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var commands = await response.Content.ReadFromJsonAsync<SessionCommandsResponse>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(commands);
        Assert.Contains("/compact", commands.RecentlyUsed);
    }

    [Fact]
    public async Task SetSessionMode_ToAuto_UpdatesTheBoundPermissionGrant()
    {
        var (sessionId, taskDirectoryPath) = await StartASessionAsync();

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/{sessionId}/mode", new SetSessionModeRequest("auto"), TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(Path.Combine(taskDirectoryPath, "bindings.json"), TestContext.Current.CancellationToken);
        var entry = bindings[InteractiveSessionMaterializer.DefaultWorkerName];
        Assert.NotNull(entry.PermissionGrant);
        Assert.True(entry.PermissionGrant!.ReadFiles);
        Assert.True(entry.PermissionGrant.WriteFiles);
        Assert.True(entry.PermissionGrant.RunShellCommands);
        Assert.True(entry.PermissionGrant.NetworkAccess);
    }

    [Fact]
    public async Task SetSessionMode_ToPlan_MakesTheGrantReadOnly()
    {
        var (sessionId, taskDirectoryPath) = await StartASessionAsync();

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/{sessionId}/mode", new SetSessionModeRequest("plan"), TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(Path.Combine(taskDirectoryPath, "bindings.json"), TestContext.Current.CancellationToken);
        var entry = bindings[InteractiveSessionMaterializer.DefaultWorkerName];
        Assert.NotNull(entry.PermissionGrant);
        Assert.True(entry.PermissionGrant!.ReadFiles);
        Assert.False(entry.PermissionGrant.WriteFiles);
        Assert.False(entry.PermissionGrant.RunShellCommands);
    }

    [Fact]
    public async Task SetSessionMode_WithAnUnknownMode_ReturnsBadRequest()
    {
        var (sessionId, _) = await StartASessionAsync();

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/{sessionId}/mode", new SetSessionModeRequest("not-a-real-mode"), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed record SessionModeResponse(string Mode);

    [Fact]
    public async Task GetSessionMode_ForANewSession_ReturnsDefault()
    {
        var (sessionId, _) = await StartASessionAsync();

        var response = await _client.GetAsync($"{BaseUrl}/api/sessions/{sessionId}/mode", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var mode = await response.Content.ReadFromJsonAsync<SessionModeResponse>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(mode);
        Assert.Equal("default", mode.Mode);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("plan")]
    [InlineData("default")]
    public async Task SetSessionMode_ThenGetSessionMode_ReflectsTheChange(string mode)
    {
        var (sessionId, _) = await StartASessionAsync();

        var setResponse = await _client.PostAsJsonAsync($"{BaseUrl}/api/sessions/{sessionId}/mode", new SetSessionModeRequest(mode), TestContext.Current.CancellationToken);
        Assert.True(setResponse.IsSuccessStatusCode);

        var getResponse = await _client.GetAsync($"{BaseUrl}/api/sessions/{sessionId}/mode", TestContext.Current.CancellationToken);
        Assert.True(getResponse.IsSuccessStatusCode);
        var result = await getResponse.Content.ReadFromJsonAsync<SessionModeResponse>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(mode, result.Mode);
    }

    [Fact]
    public async Task GetSessionMode_ForANonexistentSession_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"{BaseUrl}/api/sessions/does-not-exist/mode", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ClearSession_ResetsTurnsAndForcesAFreshUnestablishedVendorSession()
    {
        var (sessionId, taskDirectoryPath) = await StartASessionAsync();
        var metadataPath = Path.Combine(taskDirectoryPath, ".aer", "session.json");

        // Simulate a session that already had real turns and an established native vendor session
        // -- clear must reset both without ever needing a real vendor call itself.
        var beforeClear = await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath, TestContext.Current.CancellationToken);
        Assert.NotNull(beforeClear);
        var withTurns = beforeClear with
        {
            Turns = [new SessionTurn(1, "claude", "hello", "hi", DateTimeOffset.UtcNow, false, false)],
            TurnCount = 1,
            VendorSessionEstablished = true,
        };
        await InteractiveSessionMaterializer.SaveMetadataAsync(withTurns, metadataPath, TestContext.Current.CancellationToken);
        var originalVendorSessionId = withTurns.CurrentVendorSessionId;

        var response = await _client.PostAsync($"{BaseUrl}/api/sessions/{sessionId}/clear", null, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var cleared = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(cleared);
        Assert.Empty(cleared.Turns);
        Assert.Equal(0, cleared.TurnCount);
        Assert.False(cleared.VendorSessionEstablished);
        // A fresh, distinct id -- not merely un-established -- so a leftover client-side reference
        // to the old id can never be mistaken for still-valid after a clear.
        Assert.NotEqual(originalVendorSessionId, cleared.CurrentVendorSessionId);
        Assert.NotNull(cleared.CurrentVendorSessionId);

        var onDisk = await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath, TestContext.Current.CancellationToken);
        Assert.NotNull(onDisk);
        Assert.Empty(onDisk.Turns);
    }

    [Fact]
    public async Task ClearSession_ForANonexistentSession_ReturnsNotFound()
    {
        var response = await _client.PostAsync($"{BaseUrl}/api/sessions/does-not-exist/clear", null, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StartSession_WithATaskNameAlreadyInUse_ReturnsBadRequestAndDoesNotClobberTheFirstSession()
    {
        var taskName = "collision-test-" + Guid.NewGuid().ToString("N");
        var (firstSessionId, _) = await StartASessionAsync(taskName);

        var secondRequest = new StartSessionRequest(Adapter: "claude", TaskName: taskName);
        var secondResponse = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/start", secondRequest, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, secondResponse.StatusCode);

        // The rejected second attempt must not have clobbered the first session -- it must still be
        // reachable by its original id with its original SessionId intact.
        var getResponse = await _client.GetAsync($"{_baseUrl}/api/sessions/{firstSessionId}", TestContext.Current.CancellationToken);
        Assert.True(getResponse.IsSuccessStatusCode);
        var metadata = await getResponse.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        Assert.Equal(firstSessionId, metadata.SessionId);
    }

    [Fact]
    public async Task GetFleet_ReturnsOkAndIncludesAStartedSessionByDefault()
    {
        var (_, taskDirectoryPath) = await StartASessionAsync();

        var response = await _client.GetAsync($"{_baseUrl}/api/tasks", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var items = await response.Content.ReadFromJsonAsync<List<TaskFleetItem>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(items);
        Assert.Contains(items, i => i.TaskDirectoryPath == taskDirectoryPath);
    }

    [Fact]
    public async Task ArchiveUnarchiveAndDelete_RoundTripThroughTheFleetAndLifecycleEndpoints()
    {
        var taskName = "fleet-lifecycle-" + Guid.NewGuid().ToString("N");
        var (_, taskDirectoryPath) = await StartASessionAsync(taskName);

        // Archiving hides it from the default fleet list but keeps it reachable with includeArchived.
        var archiveResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/archive", new TaskDirectoryRequest(taskDirectoryPath), TestContext.Current.CancellationToken);
        Assert.True(archiveResponse.IsSuccessStatusCode);

        var defaultList = await (await _client.GetAsync($"{_baseUrl}/api/tasks", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<List<TaskFleetItem>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(defaultList!, i => i.TaskDirectoryPath == taskDirectoryPath);

        var withArchived = await (await _client.GetAsync($"{_baseUrl}/api/tasks?includeArchived=true", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<List<TaskFleetItem>>(cancellationToken: TestContext.Current.CancellationToken);
        var archivedItem = Assert.Single(withArchived!, i => i.TaskDirectoryPath == taskDirectoryPath);
        Assert.True(archivedItem.IsArchived);

        // Archiving alone must not free the name for reuse -- workflow.json/session.json is still on disk.
        var collisionResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/start", new StartSessionRequest(Adapter: "claude", TaskName: taskName), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, collisionResponse.StatusCode);

        // Unarchiving reinstates it in the default list.
        var unarchiveResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/unarchive", new TaskDirectoryRequest(taskDirectoryPath), TestContext.Current.CancellationToken);
        Assert.True(unarchiveResponse.IsSuccessStatusCode);

        var reinstatedList = await (await _client.GetAsync($"{_baseUrl}/api/tasks", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<List<TaskFleetItem>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(reinstatedList!, i => i.TaskDirectoryPath == taskDirectoryPath && !i.IsArchived);

        // Only a real delete frees the directory and the name (M24 Phase 5 regression, #278).
        var deleteResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/delete", new TaskDirectoryRequest(taskDirectoryPath), TestContext.Current.CancellationToken);
        Assert.True(deleteResponse.IsSuccessStatusCode);
        Assert.False(Directory.Exists(taskDirectoryPath));

        var recentsAfterDelete = await (await _client.GetAsync($"{_baseUrl}/api/tasks/recent", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<IReadOnlyList<string>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(taskDirectoryPath, recentsAfterDelete!);

        var freshCollisionResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/start", new StartSessionRequest(Adapter: "claude", TaskName: taskName), TestContext.Current.CancellationToken);
        Assert.True(freshCollisionResponse.IsSuccessStatusCode);
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("unarchive")]
    [InlineData("delete")]
    public async Task TaskLifecycleEndpoints_WithADirectoryPathOutsideTasksOrSessionsRoots_ReturnBadRequest(string action)
    {
        // Same containment guard #250 added for RunTemplate's TaskName, applied here (review
        // follow-up): these endpoints are remote-reachable (mobile's DaemonClient included) and
        // delete does a real recursive Directory.Delete -- an uncontained DirectoryPath is strictly
        // worse than #250's traversal, since it needs no traversal trick, just any absolute path
        // outside ~/.aer/tasks and ~/.aer/sessions.
        var outsidePath = Path.Combine(_tempTaskDirectory!, "outside-managed-roots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsidePath);

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/{action}", new TaskDirectoryRequest(outsidePath), TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(Directory.Exists(outsidePath));
    }

    [Fact]
    public async Task DeleteTask_ForANonexistentDirectory_ReturnsNotFound()
    {
        // Must be under the managed ~/.aer/sessions root -- otherwise the containment guard now
        // rejects it as BadRequest before this handler's own NotFound check ever runs.
        var baseSessionsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aer", "sessions");
        var missingDirectory = Path.Combine(baseSessionsDir, "never-created-" + Guid.NewGuid().ToString("N"));

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/tasks/delete", new TaskDirectoryRequest(missingDirectory), TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RegisterProject_ThenListProjects_IncludesItAndCanBeCleanedUp()
    {
        var marker = "aer_daemon_test_project_" + Guid.NewGuid().ToString("N");
        var projectDirectory = Path.Combine(Path.GetTempPath(), marker);

        try
        {
            var postResponse = await _client.PostAsJsonAsync(
                $"{_baseUrl}/api/projects", new RegisterProjectRequest(projectDirectory, marker), TestContext.Current.CancellationToken);
            Assert.True(postResponse.IsSuccessStatusCode);

            var getResponse = await _client.GetAsync($"{_baseUrl}/api/projects", TestContext.Current.CancellationToken);
            Assert.True(getResponse.IsSuccessStatusCode);

            var projects = await getResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
            var matched = projects.EnumerateArray().Any(p =>
                (p.TryGetProperty("friendlyName", out var f) || p.TryGetProperty("FriendlyName", out f)) && f.GetString() == marker);
            Assert.True(matched);
        }
        finally
        {
            // KnownProjectsStore persists to the real per-user ~/.aer/projects.json -- this test
            // must not leave its synthetic entry behind on whatever machine runs it.
            var aerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aer");
            var projectsFile = Path.Combine(aerDir, "projects.json");
            if (File.Exists(projectsFile))
            {
                var json = await File.ReadAllTextAsync(projectsFile, TestContext.Current.CancellationToken);
                var remaining = JsonSerializer.Deserialize<List<JsonElement>>(json)!
                    .Where(p => !((p.TryGetProperty("friendlyName", out var f) || p.TryGetProperty("FriendlyName", out f)) && f.GetString() == marker))
                    .ToList();
                await File.WriteAllTextAsync(
                    projectsFile,
                    JsonSerializer.Serialize(remaining, new JsonSerializerOptions { WriteIndented = true }),
                    TestContext.Current.CancellationToken);
            }
        }
    }

    [Fact]
    public async Task ProgressWebSocket_AcceptsAConnectionWithoutRequiringAnOpenTask()
    {
        // Deliberately kept separate from /api/ws (M24 Phase 1) -- a client subscribing to live
        // in-turn progress has no TaskSession dependency, unlike the projection socket.
        var token = _client.DefaultRequestHeaders.Authorization!.Parameter!;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"{WsBaseUrl}/api/ws/progress?token={token}"), TestContext.Current.CancellationToken);

        Assert.Equal(WebSocketState.Open, socket.State);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
    }
}
