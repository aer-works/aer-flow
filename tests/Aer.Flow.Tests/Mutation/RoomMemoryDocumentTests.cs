using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;

namespace Aer.Flow.Tests.Mutation;

public class RoomMemoryDocumentTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _roomLogPath;
    private readonly string _memoryRoot;

    public RoomMemoryDocumentTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "aer_room_memory_doc_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _roomLogPath = Path.Combine(_tempDirectory, "room.jsonl");
        _memoryRoot = Path.Combine(_tempDirectory, "memory");
    }

    private async Task<HeldWorkRef> DispatchMemoryProposalAsync(
        IRoomEventLogReader reader, IRoomEventLogWriter writer, string operation = "add", string targetPath = "fact.md", string content = "the fact")
    {
        var captureDir = Path.Combine(_tempDirectory, "artifacts", "execution_1", "memory-proposals");
        Directory.CreateDirectory(captureDir);
        var captureFile = Path.Combine(captureDir, "proposal-1.json");
        var contentJson = operation == "delete" ? "null" : $"\"{content}\"";
        await File.WriteAllTextAsync(
            captureFile,
            $$"""{"Operation":"{{operation}}","TargetPath":"{{targetPath}}","Content":{{contentJson}},"Rationale":"learned it"}""",
            TestContext.Current.CancellationToken);

        var @ref = new HeldWorkRef(Path.GetFullPath(captureFile));
        await RoomMutationInterface.DispatchHeldWorkAsync(
            _tempDirectory, @ref, MemoryProposalEscalation.MemoryProposalShape, MemoryProposalEscalation.NoBudget,
            "operator", reader, writer, TestContext.Current.CancellationToken);

        return @ref;
    }

    [Fact]
    public async Task Proposal_approved_updates_document_bumps_version_and_includes_attribution()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);
        var @ref = await DispatchMemoryProposalAsync(reader, writer, operation: "add", targetPath: "rules/rule1.md", content: "first rule");

        var docBefore = await RoomMemoryDocument.LoadAsync(_tempDirectory, TestContext.Current.CancellationToken);
        Assert.Equal(0, docBefore.Version);
        Assert.Empty(docBefore.FactFiles);
        Assert.Empty(docBefore.History);

        await MemoryProposalResolution.ResolveAsync(
            _tempDirectory, @ref, approve: true, reader, writer, TestContext.Current.CancellationToken);

        var docAfter = await RoomMemoryDocument.LoadAsync(_tempDirectory, TestContext.Current.CancellationToken);
        Assert.Equal(1, docAfter.Version);
        Assert.Single(docAfter.FactFiles);
        Assert.Equal("first rule", docAfter.FactFiles["rules/rule1.md"]);
        Assert.Single(docAfter.History);

        var versionRecord = docAfter.History[0];
        Assert.Equal(1, versionRecord.Version);
        Assert.Equal("add", versionRecord.Operation);
        Assert.Equal("rules/rule1.md", versionRecord.TargetPath);
        Assert.Equal("first rule", versionRecord.Content);
        Assert.Equal("learned it", versionRecord.Rationale);
        Assert.Equal("operator", versionRecord.Approver);
        Assert.False(string.IsNullOrWhiteSpace(versionRecord.Proposer));
    }

    [Fact]
    public async Task Proposal_rejected_leaves_document_untouched()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);
        var @ref = await DispatchMemoryProposalAsync(reader, writer, operation: "add", targetPath: "fact.md", content: "rejected fact");

        await MemoryProposalResolution.ResolveAsync(
            _tempDirectory, @ref, approve: false, reader, writer, TestContext.Current.CancellationToken);

        var doc = await RoomMemoryDocument.LoadAsync(_tempDirectory, TestContext.Current.CancellationToken);
        Assert.Equal(0, doc.Version);
        Assert.Empty(doc.FactFiles);
        Assert.Empty(doc.History);
    }

    [Fact]
    public async Task No_path_writes_document_except_through_resolution()
    {
        // Unapproved capture file sitting in execution directory
        var captureDir = Path.Combine(_tempDirectory, "artifacts", "execution_2", "memory-proposals");
        Directory.CreateDirectory(captureDir);
        var captureFile = Path.Combine(captureDir, "proposal-2.json");
        await File.WriteAllTextAsync(
            captureFile,
            """{"Operation":"add","TargetPath":"secret.md","Content":"secret","Rationale":"unapproved"}""",
            TestContext.Current.CancellationToken);

        // Load document - must be untouched (0, empty)
        var doc = await RoomMemoryDocument.LoadAsync(_tempDirectory, TestContext.Current.CancellationToken);
        Assert.Equal(0, doc.Version);
        Assert.False(doc.FactFiles.ContainsKey("secret.md"));
        Assert.Empty(doc.History);
    }

    [Fact]
    public async Task Archiving_and_reopening_room_dir_preserves_document()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        HeldWorkRef @ref;
        await using (var writer = new RoomEventLogWriter(_roomLogPath))
        {
            @ref = await DispatchMemoryProposalAsync(reader, writer, operation: "add", targetPath: "fact.md", content: "persistent fact");

            await MemoryProposalResolution.ResolveAsync(
                _tempDirectory, @ref, approve: true, reader, writer, TestContext.Current.CancellationToken);
        }

        var docOriginal = await RoomMemoryDocument.LoadAsync(_tempDirectory, TestContext.Current.CancellationToken);

        // Move room directory (simulating archive & reopen in a new location)
        var archiveDirectory = Path.Combine(Path.GetTempPath(), "aer_room_archive_" + Guid.NewGuid().ToString("N"));
        Directory.Move(_tempDirectory, archiveDirectory);

        try
        {
            var docArchived = await RoomMemoryDocument.LoadAsync(archiveDirectory, TestContext.Current.CancellationToken);
            Assert.Equal(docOriginal.Version, docArchived.Version);
            Assert.Equal(docOriginal.FactFiles, docArchived.FactFiles);
            Assert.Equal(docOriginal.History.Count, docArchived.History.Count);
            Assert.Equal(docOriginal.History[0].TargetPath, docArchived.History[0].TargetPath);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(archiveDirectory);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            DirectoryCleanup.DeleteRecursively(_tempDirectory);
        }
    }
}
