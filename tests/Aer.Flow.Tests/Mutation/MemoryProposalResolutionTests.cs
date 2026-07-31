using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Store;

namespace Aer.Flow.Tests.Mutation;

public class MemoryProposalResolutionTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _roomLogPath;
    private readonly string _memoryRoot;

    public MemoryProposalResolutionTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "aer_memory_resolution_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _roomLogPath = Path.Combine(_tempDirectory, "room.jsonl");
        _memoryRoot = Path.Combine(_tempDirectory, "memory");
    }

    private async Task<HeldWorkRef> DispatchMemoryProposalAsync(
        IRoomEventLogReader reader, IRoomEventLogWriter writer, string operation = "add", string targetPath = "fact.md")
    {
        var captureDir = Path.Combine(_tempDirectory, "artifacts", "execution_1", "memory-proposals");
        Directory.CreateDirectory(captureDir);
        var captureFile = Path.Combine(captureDir, "proposal-1.json");
        var content = operation == "delete" ? "null" : "\"the fact\"";
        await File.WriteAllTextAsync(
            captureFile,
            $$"""{"Operation":"{{operation}}","TargetPath":"{{targetPath}}","Content":{{content}},"Rationale":"learned it"}""",
            TestContext.Current.CancellationToken);

        var @ref = new HeldWorkRef(Path.GetFullPath(captureFile));
        await RoomMutationInterface.DispatchHeldWorkAsync(
            _tempDirectory, @ref, MemoryProposalEscalation.MemoryProposalShape, MemoryProposalEscalation.NoBudget,
            "operator", reader, writer, TestContext.Current.CancellationToken);

        return @ref;
    }

    /// <summary>Approval of a memory-proposal-shaped item both applies the write and resolves the held-work item.</summary>
    [Fact]
    public async Task Approving_a_memory_proposal_applies_it_and_resolves_the_held_work()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);
        var @ref = await DispatchMemoryProposalAsync(reader, writer);

        var state = await MemoryProposalResolution.ResolveAsync(
            _tempDirectory, @ref, approve: true, reader, writer, TestContext.Current.CancellationToken);

        Assert.Equal(HeldWorkStatus.Resolved, state.HeldWork[@ref].Status);
        Assert.Equal("the fact", await File.ReadAllTextAsync(Path.Combine(_memoryRoot, "fact.md"), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The other polarity, paired with the approve test above: rejecting resolves the held-work
    /// item but leaves memory/ untouched -- specifically, not even created.
    /// </summary>
    [Fact]
    public async Task Rejecting_a_memory_proposal_resolves_it_and_leaves_memory_untouched()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);
        var @ref = await DispatchMemoryProposalAsync(reader, writer);

        var state = await MemoryProposalResolution.ResolveAsync(
            _tempDirectory, @ref, approve: false, reader, writer, TestContext.Current.CancellationToken);

        Assert.Equal(HeldWorkStatus.Resolved, state.HeldWork[@ref].Status);
        Assert.False(Directory.Exists(_memoryRoot));
    }

    /// <summary>
    /// Byte-identical polarity check against an existing memory/ tree: rejecting a proposal that
    /// would have edited an existing fact file must not touch that file at all.
    /// </summary>
    [Fact]
    public async Task Rejecting_leaves_an_existing_memory_tree_byte_identical()
    {
        Directory.CreateDirectory(_memoryRoot);
        await File.WriteAllTextAsync(Path.Combine(_memoryRoot, "fact.md"), "original", TestContext.Current.CancellationToken);
        var beforeBytes = await File.ReadAllBytesAsync(Path.Combine(_memoryRoot, "fact.md"), TestContext.Current.CancellationToken);

        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);
        var @ref = await DispatchMemoryProposalAsync(reader, writer, operation: "edit");

        await MemoryProposalResolution.ResolveAsync(
            _tempDirectory, @ref, approve: false, reader, writer, TestContext.Current.CancellationToken);

        var afterBytes = await File.ReadAllBytesAsync(Path.Combine(_memoryRoot, "fact.md"), TestContext.Current.CancellationToken);
        Assert.Equal(beforeBytes, afterBytes);
        Assert.False(File.Exists(Path.Combine(_memoryRoot, MemoryProposalApplier.IndexFileName)));
    }

    [Fact]
    public async Task Resolving_an_unknown_ref_throws_InvalidRoomMutationException()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);
        var unknown = new HeldWorkRef(Path.Combine(_tempDirectory, "nope.json"));

        await Assert.ThrowsAsync<InvalidRoomMutationException>(() => MemoryProposalResolution.ResolveAsync(
            _tempDirectory, unknown, approve: true, reader, writer, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The discriminating proof for the class's own "apply, then resolve" ordering claim: force
    /// the resolve step (the journal append) to throw AFTER apply has already succeeded, then
    /// assert the file landed but the held-work item is still NOT resolved. Under a
    /// resolve-then-apply ordering this exact test would instead show a resolved item with no
    /// file -- the opposite, invisible failure this ordering exists to avoid.
    /// </summary>
    [Fact]
    public async Task A_failure_between_apply_and_resolve_leaves_the_file_applied_but_the_item_still_pending()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var setupWriter = new RoomEventLogWriter(_roomLogPath);
        var @ref = await DispatchMemoryProposalAsync(reader, setupWriter);

        var throwingWriter = new ThrowingRoomEventLogWriter();
        await Assert.ThrowsAsync<IOException>(() => MemoryProposalResolution.ResolveAsync(
            _tempDirectory, @ref, approve: true, reader, throwingWriter, TestContext.Current.CancellationToken));

        Assert.Equal(
            "the fact", await File.ReadAllTextAsync(Path.Combine(_memoryRoot, "fact.md"), TestContext.Current.CancellationToken));

        var stateAfterCrash = RoomProjector.Project(
            await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HeldWorkStatus.Dispatched, stateAfterCrash.HeldWork[@ref].Status);
    }

    /// <summary>Simulates a crash between apply and resolve: AppendAsync (the resolve half) throws every time.</summary>
    private sealed class ThrowingRoomEventLogWriter : IRoomEventLogWriter
    {
        public Task AppendAsync(RoomEvent roomEvent, CancellationToken cancellationToken = default)
            => throw new IOException("simulated crash between apply and resolve");
    }

    [Fact]
    public async Task Double_resolving_the_same_ref_throws_on_the_second_call()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);
        var @ref = await DispatchMemoryProposalAsync(reader, writer);

        await MemoryProposalResolution.ResolveAsync(
            _tempDirectory, @ref, approve: true, reader, writer, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidRoomMutationException>(() => MemoryProposalResolution.ResolveAsync(
            _tempDirectory, @ref, approve: true, reader, writer, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// #672 review (blocking finding): rejecting an item and then calling approve on the SAME ref
    /// must refuse -- it is already resolved -- and must not write memory/ on the way to refusing.
    /// Before the fix this applied the proposal and only then threw, leaving the write behind
    /// despite the 400.
    /// </summary>
    [Fact]
    public async Task Approving_after_a_reject_on_the_same_ref_refuses_and_writes_nothing()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);
        var @ref = await DispatchMemoryProposalAsync(reader, writer);

        await MemoryProposalResolution.ResolveAsync(
            _tempDirectory, @ref, approve: false, reader, writer, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidRoomMutationException>(() => MemoryProposalResolution.ResolveAsync(
            _tempDirectory, @ref, approve: true, reader, writer, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(Path.Combine(_memoryRoot, "fact.md")));
    }

    /// <summary>
    /// #672 review: approving an 'edit' proposal, then a human hand-edits the resulting fact file,
    /// then a second approve call against the SAME already-resolved ref must refuse and must not
    /// clobber the hand edit.
    /// </summary>
    [Fact]
    public async Task Re_approving_an_already_resolved_edit_refuses_and_does_not_clobber_a_hand_edit()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);
        var @ref = await DispatchMemoryProposalAsync(reader, writer);

        await MemoryProposalResolution.ResolveAsync(
            _tempDirectory, @ref, approve: true, reader, writer, TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(_memoryRoot, "fact.md"), "operator edited by hand", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidRoomMutationException>(() => MemoryProposalResolution.ResolveAsync(
            _tempDirectory, @ref, approve: true, reader, writer, TestContext.Current.CancellationToken));

        Assert.Equal(
            "operator edited by hand",
            await File.ReadAllTextAsync(Path.Combine(_memoryRoot, "fact.md"), TestContext.Current.CancellationToken));
    }

    /// <summary>A non-memory-proposal shape resolves through the same surface with no filesystem side effect.</summary>
    [Fact]
    public async Task Approving_a_non_memory_proposal_shape_resolves_without_touching_memory()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);
        var laneRef = new HeldWorkRef(Path.Combine(_tempDirectory, "lanes", "lane-1"));
        await RoomMutationInterface.DispatchHeldWorkAsync(
            _tempDirectory, laneRef, "shape-1", TimeSpan.FromMinutes(10), "op-alice", reader, writer, TestContext.Current.CancellationToken);

        var state = await MemoryProposalResolution.ResolveAsync(
            _tempDirectory, laneRef, approve: true, reader, writer, TestContext.Current.CancellationToken);

        Assert.Equal(HeldWorkStatus.Resolved, state.HeldWork[laneRef].Status);
        Assert.False(Directory.Exists(_memoryRoot));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
