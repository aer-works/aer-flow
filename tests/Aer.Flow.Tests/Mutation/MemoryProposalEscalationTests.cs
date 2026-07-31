using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Store;

namespace Aer.Flow.Tests.Mutation;

public class MemoryProposalEscalationTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _roomLogPath;
    private readonly string _captureDirectory;

    public MemoryProposalEscalationTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "aer_memory_proposal_esc_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _roomLogPath = Path.Combine(_tempDirectory, "room.jsonl");
        _captureDirectory = Path.Combine(_tempDirectory, "memory-proposals");
    }

    [Fact]
    public async Task A_captured_proposal_becomes_visible_held_work_in_the_room()
    {
        Directory.CreateDirectory(_captureDirectory);
        var captureFile = Path.Combine(_captureDirectory, "proposal-abc.json");
        await File.WriteAllTextAsync(
            captureFile,
            """{"Operation":"add","TargetPath":"new-fact.md","Content":"the fact","Rationale":"learned it"}""",
            TestContext.Current.CancellationToken);

        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);

        var state = await MemoryProposalEscalation.EscalateNewProposalsAsync(
            _captureDirectory, _tempDirectory, "operator", reader, writer, TestContext.Current.CancellationToken);

        var @ref = new HeldWorkRef(Path.GetFullPath(captureFile));
        Assert.Single(state.HeldWork);
        Assert.Equal(HeldWorkStatus.Dispatched, state.HeldWork[@ref].Status);
        Assert.Equal(MemoryProposalEscalation.MemoryProposalShape, state.HeldWork[@ref].Shape);
        Assert.Equal("operator", state.HeldWork[@ref].DeciderIdentity);
    }

    [Fact]
    public async Task Running_twice_against_the_same_capture_does_not_re_dispatch()
    {
        Directory.CreateDirectory(_captureDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(_captureDirectory, "proposal-abc.json"),
            """{"Operation":"delete","TargetPath":"stale.md","Content":null,"Rationale":"superseded"}""",
            TestContext.Current.CancellationToken);

        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);

        var first = await MemoryProposalEscalation.EscalateNewProposalsAsync(
            _captureDirectory, _tempDirectory, "operator", reader, writer, TestContext.Current.CancellationToken);
        var second = await MemoryProposalEscalation.EscalateNewProposalsAsync(
            _captureDirectory, _tempDirectory, "operator", reader, writer, TestContext.Current.CancellationToken);

        Assert.Single(first.HeldWork);
        Assert.Single(second.HeldWork);
    }

    [Fact]
    public async Task No_capture_directory_yields_no_held_work_and_does_not_throw()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);

        var state = await MemoryProposalEscalation.EscalateNewProposalsAsync(
            _captureDirectory, _tempDirectory, "operator", reader, writer, TestContext.Current.CancellationToken);

        Assert.Empty(state.HeldWork);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
