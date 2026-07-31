using System.Text.Json;
using Aer.Flow.Mutation;

namespace Aer.Flow.Tests.Mutation;

public class MemoryProposalApplierTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _memoryRoot;

    public MemoryProposalApplierTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "aer_memory_applier_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _memoryRoot = Path.Combine(_tempDirectory, "memory");
    }

    private string WriteCapture(string json, string fileName = "proposal-1.json")
    {
        var path = Path.Combine(_tempDirectory, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task Add_writes_content_to_target_path_under_memory()
    {
        var capture = WriteCapture("""{"Operation":"add","TargetPath":"fact.md","Content":"the fact","Rationale":"learned it"}""");

        await MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken);

        Assert.Equal("the fact", await File.ReadAllTextAsync(Path.Combine(_memoryRoot, "fact.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Edit_overwrites_an_existing_fact_file()
    {
        Directory.CreateDirectory(_memoryRoot);
        await File.WriteAllTextAsync(Path.Combine(_memoryRoot, "fact.md"), "stale", TestContext.Current.CancellationToken);
        var capture = WriteCapture("""{"Operation":"edit","TargetPath":"fact.md","Content":"fresh","Rationale":"corrected"}""");

        await MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken);

        Assert.Equal("fresh", await File.ReadAllTextAsync(Path.Combine(_memoryRoot, "fact.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_removes_an_existing_fact_file()
    {
        Directory.CreateDirectory(_memoryRoot);
        await File.WriteAllTextAsync(Path.Combine(_memoryRoot, "fact.md"), "gone soon", TestContext.Current.CancellationToken);
        var capture = WriteCapture("""{"Operation":"delete","TargetPath":"fact.md","Content":null,"Rationale":"superseded"}""");

        await MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(_memoryRoot, "fact.md")));
    }

    /// <summary>#672's explicit requirement: a delete against a target that is not there is a LOUD failure, never a silent success.</summary>
    [Fact]
    public async Task Delete_of_a_nonexistent_target_throws_loudly()
    {
        var capture = WriteCapture("""{"Operation":"delete","TargetPath":"never-existed.md","Content":null,"Rationale":"superseded"}""");

        await Assert.ThrowsAsync<InvalidRoomMutationException>(
            () => MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 0044 review finding: 'add' and 'edit' must not be synonymous, or an approved 'add' can
    /// silently overwrite a fact nobody decided to overwrite. Both-polarity pair with the test
    /// below.
    /// </summary>
    [Fact]
    public async Task Add_against_an_existing_target_throws_loudly_and_does_not_overwrite()
    {
        Directory.CreateDirectory(_memoryRoot);
        await File.WriteAllTextAsync(Path.Combine(_memoryRoot, "fact.md"), "original", TestContext.Current.CancellationToken);
        var capture = WriteCapture("""{"Operation":"add","TargetPath":"fact.md","Content":"clobber","Rationale":"r"}""");

        await Assert.ThrowsAsync<InvalidRoomMutationException>(
            () => MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken));

        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(_memoryRoot, "fact.md"), TestContext.Current.CancellationToken));
    }

    /// <summary>Other polarity: 'edit' against a target that does not exist must not silently create it.</summary>
    [Fact]
    public async Task Edit_against_a_nonexistent_target_throws_loudly_and_creates_nothing()
    {
        var capture = WriteCapture("""{"Operation":"edit","TargetPath":"never-existed.md","Content":"new","Rationale":"r"}""");

        await Assert.ThrowsAsync<InvalidRoomMutationException>(
            () => MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(Path.Combine(_memoryRoot, "never-existed.md")));
    }

    /// <summary>
    /// The discriminating red: a naive apply that just does <c>Path.Combine(memoryRoot, targetPath)</c>
    /// with no containment check would happily write outside memory/ here. This is the guard's
    /// non-negotiable case (#672) -- proven with a real filesystem write attempt, not a string check.
    /// </summary>
    [Fact]
    public async Task A_traversal_targetPath_is_refused_and_writes_nothing_outside_memory()
    {
        var escapeTarget = Path.Combine(_tempDirectory, "escaped.md");
        var capture = WriteCapture(
            """{"Operation":"add","TargetPath":"../escaped.md","Content":"pwned","Rationale":"malicious"}""");

        await Assert.ThrowsAsync<InvalidRoomMutationException>(
            () => MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(escapeTarget));
    }

    [Fact]
    public async Task A_rooted_targetPath_is_refused()
    {
        var rooted = OperatingSystem.IsWindows() ? "C:\\evil.md" : "/etc/evil.md";
        var capture = Path.Combine(_tempDirectory, "proposal-rooted.json");
        File.WriteAllText(capture, JsonSerializer.Serialize(
            new MemoryProposalCapture("add", rooted, "pwned", "malicious")));

        await Assert.ThrowsAsync<InvalidRoomMutationException>(
            () => MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken));
    }

    /// <summary>Positive polarity paired with the traversal-refusal tests above: an ordinary nested targetPath inside memory/ is allowed.</summary>
    [Fact]
    public async Task A_nested_targetPath_inside_memory_is_allowed()
    {
        var capture = WriteCapture(
            """{"Operation":"add","TargetPath":"topics/fact.md","Content":"nested fact","Rationale":"learned it"}""");

        await MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken);

        Assert.Equal(
            "nested fact",
            await File.ReadAllTextAsync(Path.Combine(_memoryRoot, "topics", "fact.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Applying_regenerates_the_index_with_one_line_per_fact_file()
    {
        var first = WriteCapture(
            """{"Operation":"add","TargetPath":"a.md","Content":"a","Rationale":"r"}""", "proposal-a.json");
        await MemoryProposalApplier.ApplyAsync(_tempDirectory, first, TestContext.Current.CancellationToken);

        var second = WriteCapture(
            """{"Operation":"add","TargetPath":"b.md","Content":"b","Rationale":"r"}""", "proposal-b.json");
        await MemoryProposalApplier.ApplyAsync(_tempDirectory, second, TestContext.Current.CancellationToken);

        var index = await File.ReadAllTextAsync(
            Path.Combine(_memoryRoot, MemoryProposalApplier.IndexFileName), TestContext.Current.CancellationToken);

        Assert.Contains("- a.md", index);
        Assert.Contains("- b.md", index);
    }

    [Fact]
    public async Task Deleting_the_only_fact_file_regenerates_an_empty_index()
    {
        var add = WriteCapture(
            """{"Operation":"add","TargetPath":"a.md","Content":"a","Rationale":"r"}""", "proposal-a.json");
        await MemoryProposalApplier.ApplyAsync(_tempDirectory, add, TestContext.Current.CancellationToken);

        var delete = WriteCapture(
            """{"Operation":"delete","TargetPath":"a.md","Content":null,"Rationale":"r"}""", "proposal-b.json");
        await MemoryProposalApplier.ApplyAsync(_tempDirectory, delete, TestContext.Current.CancellationToken);

        var index = await File.ReadAllTextAsync(
            Path.Combine(_memoryRoot, MemoryProposalApplier.IndexFileName), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("- a.md", index);
    }

    [Fact]
    public async Task A_missing_capture_file_throws_loudly()
    {
        var missing = Path.Combine(_tempDirectory, "does-not-exist.json");

        await Assert.ThrowsAsync<InvalidRoomMutationException>(
            () => MemoryProposalApplier.ApplyAsync(_tempDirectory, missing, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
