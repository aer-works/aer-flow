using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aer.Daemon;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Ui.Core;
using Xunit;

namespace Aer.Ui.Tests;

public class DaemonIntegrationTests : IAsyncLifetime
{
    private Task? _daemonTask;
    private const string BaseUrl = "http://localhost:5050";
    private readonly HttpClient _client = new();
    private string? _tempTaskDirectory;

    public async ValueTask InitializeAsync()
    {
        // Start Daemon on port 5050
        _daemonTask = DaemonHost.RunDaemonAsync(new[] { "--port", "5050" });

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
    public async Task Request_Without_Token_Is_Rejected_With_401()
    {
        using var remoteClient = new HttpClient();
        var response = await remoteClient.GetAsync($"{BaseUrl}/api/tasks/recent", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
