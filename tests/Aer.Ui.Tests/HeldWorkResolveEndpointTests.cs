using System.Net;
using System.Net.Http.Json;
using Aer.Adapters;
using Aer.Daemon;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Aer.Ui.Tests.TestSupport;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// #672: the operator's HTTP decision surface for held work, and the specific polarity that
/// memory-proposal approval applies the write while rejection leaves memory/ untouched.
/// </summary>
[Collection("DaemonIntegrationTests")]
public class HeldWorkResolveEndpointTests : IAsyncLifetime
{
    private DaemonTestInstance? _daemon;
    private string _baseUrl = "";
    private readonly HttpClient _client = new();
    private string _roomDirectory = "";

    public async ValueTask InitializeAsync()
    {
        _daemon = await DaemonTestHost.StartAsync();
        _baseUrl = _daemon.BaseUrl;

        for (var i = 0; i < 30; i++)
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

        var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
        if (File.Exists(tokenFile))
        {
            var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        _roomDirectory = Path.Combine(Path.GetTempPath(), "aer_held_work_resolve_endpoint_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_roomDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        if (_daemon != null)
        {
            await _daemon.DisposeAsync();
        }

        _client.Dispose();

        if (Directory.Exists(_roomDirectory))
        {
            DirectoryCleanup.DeleteRecursively(_roomDirectory);
        }
    }

    private async Task<HeldWorkRef> DispatchMemoryProposalAsync(string operation = "add", string targetPath = "fact.md")
    {
        var captureDir = Path.Combine(_roomDirectory, "artifacts", "execution_1", "memory-proposals");
        Directory.CreateDirectory(captureDir);
        var captureFile = Path.Combine(captureDir, "proposal-1.json");
        var content = operation == "delete" ? "null" : "\"the fact\"";
        await File.WriteAllTextAsync(
            captureFile,
            $$"""{"Operation":"{{operation}}","TargetPath":"{{targetPath}}","Content":{{content}},"Rationale":"learned it"}""",
            TestContext.Current.CancellationToken);

        var @ref = new HeldWorkRef(Path.GetFullPath(captureFile));
        var roomLogPath = Path.Combine(_roomDirectory, "room.jsonl");
        var reader = new RoomEventLogReader(roomLogPath);
        await using var writer = new RoomEventLogWriter(roomLogPath);
        await RoomMutationInterface.DispatchHeldWorkAsync(
            _roomDirectory, @ref, MemoryProposalEscalation.MemoryProposalShape, MemoryProposalEscalation.NoBudget,
            "operator", reader, writer, TestContext.Current.CancellationToken);

        return @ref;
    }

    [Fact]
    public async Task Approving_a_memory_proposal_returns_ok_and_applies_the_write()
    {
        var @ref = await DispatchMemoryProposalAsync();

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/held-work/resolve",
            new ResolveHeldWorkRequest(_roomDirectory, @ref.Value, "approve"),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(
            "the fact",
            await File.ReadAllTextAsync(Path.Combine(_roomDirectory, "memory", "fact.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejecting_a_memory_proposal_returns_ok_and_leaves_memory_untouched()
    {
        var @ref = await DispatchMemoryProposalAsync();

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/held-work/resolve",
            new ResolveHeldWorkRequest(_roomDirectory, @ref.Value, "reject"),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        Assert.False(Directory.Exists(Path.Combine(_roomDirectory, "memory")));
    }

    /// <summary>
    /// #672 review: ConcurrencyGuard.Acquire unconditionally creates the directory it locks, so
    /// without an explicit existence guard a typo'd RoomDirectoryPath would silently create a
    /// stray directory (with a flow.lock inside it) before failing. Asserts neither happens.
    /// </summary>
    [Fact]
    public async Task Resolving_against_a_nonexistent_room_directory_returns_bad_request_and_creates_nothing()
    {
        var nonexistentRoom = Path.Combine(_roomDirectory, "does-not-exist");

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/held-work/resolve",
            new ResolveHeldWorkRequest(nonexistentRoom, Path.Combine(nonexistentRoom, "nope.json"), "approve"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Directory.Exists(nonexistentRoom));
    }

    [Fact]
    public async Task Resolving_an_unknown_ref_returns_bad_request()
    {
        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/held-work/resolve",
            new ResolveHeldWorkRequest(_roomDirectory, Path.Combine(_roomDirectory, "nope.json"), "approve"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Double_resolving_the_same_ref_returns_bad_request_on_the_second_call()
    {
        var @ref = await DispatchMemoryProposalAsync();

        var first = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/held-work/resolve",
            new ResolveHeldWorkRequest(_roomDirectory, @ref.Value, "approve"),
            TestContext.Current.CancellationToken);
        Assert.True(first.IsSuccessStatusCode);

        var second = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/held-work/resolve",
            new ResolveHeldWorkRequest(_roomDirectory, @ref.Value, "approve"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task An_invalid_outcome_value_returns_bad_request()
    {
        var @ref = await DispatchMemoryProposalAsync();

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/held-work/resolve",
            new ResolveHeldWorkRequest(_roomDirectory, @ref.Value, "maybe"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
