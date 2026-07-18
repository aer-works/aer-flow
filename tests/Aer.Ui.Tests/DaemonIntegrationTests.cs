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
                var response = await _client.GetAsync($"{BaseUrl}/api/tasks/recent");
                if (response.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch
            {
                await Task.Delay(100);
            }
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
        var response = await _client.GetAsync($"{BaseUrl}/api/tasks/recent");
        Assert.True(response.IsSuccessStatusCode);
        var recent = await response.Content.ReadFromJsonAsync<IReadOnlyList<string>>();
        Assert.NotNull(recent);
    }

    [Fact]
    public async Task OpenTask_WithMissingDirectory_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync($"{BaseUrl}/api/tasks/open", new OpenTaskRequest(""));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OpenTask_WithInvalidDirectory_ReturnsBadRequest()
    {
        var invalidDir = Path.Combine(_tempTaskDirectory!, "non_existent_folder_abc_123");
        var response = await _client.PostAsJsonAsync($"{BaseUrl}/api/tasks/open", new OpenTaskRequest(invalidDir));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
