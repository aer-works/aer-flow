using System.Diagnostics;
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

    /// <summary>
    /// #856: the lexical guard is `Path.GetFullPath` + prefix check, which never asks the
    /// filesystem whether a path component is a reparse point. A junction placed under memory/
    /// pointing outside it passes that string check -- proven red here against unmodified code
    /// (before the fix, this test fails: the write lands in <c>outsideDirectory</c>). On
    /// Windows, directory junctions are creatable with plain filesystem write access (no admin,
    /// no Developer Mode) via <c>mklink /J</c>; that's the mechanism actually measured here. On
    /// Linux/macOS, `Directory.CreateSymbolicLink` for a directory symlink needs no elevation
    /// either, so the same escape shape is provable with a real symlink instead.
    /// </summary>
    [Fact]
    public async Task A_reparse_point_under_memory_that_resolves_outside_it_is_refused()
    {
        Directory.CreateDirectory(_memoryRoot);
        var outsideDirectory = Path.Combine(_tempDirectory, "outside");
        Directory.CreateDirectory(outsideDirectory);

        var linkPath = Path.Combine(_memoryRoot, "escape");
        if (!TryCreateDirectoryReparsePoint(linkPath, outsideDirectory, out var skipReason))
        {
            Assert.Skip(skipReason);
            return;
        }

        try
        {
            var capture = WriteCapture(
                """{"Operation":"add","TargetPath":"escape/pwned.md","Content":"pwned","Rationale":"malicious"}""");

            await Assert.ThrowsAsync<InvalidRoomMutationException>(
                () => MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken));

            Assert.False(File.Exists(Path.Combine(outsideDirectory, "pwned.md")));
        }
        finally
        {
            // Recursive delete of a directory containing a reparse point is flaky on Windows
            // (Access to the path denied) -- unlink it explicitly first so Dispose()'s recursive
            // delete of _tempDirectory only ever walks real directories.
            RemoveDirectoryLink(linkPath);
        }
    }

    /// <summary>
    /// Other polarity (#856 item 2): a reparse point under memory/ that resolves back inside
    /// memory/ -- e.g. someone symlinking one fact file to another for their own reasons -- must
    /// keep working. Refusing every reparse point would be a different, broader behaviour change
    /// than closing the outside-escape above.
    /// </summary>
    [Fact]
    public async Task A_reparse_point_under_memory_that_resolves_back_inside_it_is_allowed()
    {
        Directory.CreateDirectory(_memoryRoot);
        var realDirectory = Path.Combine(_memoryRoot, "real");
        Directory.CreateDirectory(realDirectory);

        var linkPath = Path.Combine(_memoryRoot, "alias");
        if (!TryCreateDirectoryReparsePoint(linkPath, realDirectory, out var skipReason))
        {
            Assert.Skip(skipReason);
            return;
        }

        try
        {
            var capture = WriteCapture(
                """{"Operation":"add","TargetPath":"alias/fact.md","Content":"via alias","Rationale":"learned it"}""");

            await MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken);

            Assert.Equal(
                "via alias",
                await File.ReadAllTextAsync(Path.Combine(realDirectory, "fact.md"), TestContext.Current.CancellationToken));
        }
        finally
        {
            RemoveDirectoryLink(linkPath);
        }
    }

    /// <summary>
    /// #856 regression, symmetric-resolution requirement documented on
    /// <see cref="MemoryProposalApplier.ResolveReparsePointsIgnoringMissingTail"/>: the room
    /// directory itself is reached through a junction here, not just a path under memory/.
    /// </summary>
    [Fact]
    public async Task A_room_directory_reached_through_a_junction_still_allows_an_in_tree_alias()
    {
        var realRoomDirectory = Path.Combine(Path.GetTempPath(), "aer_memory_applier_room_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(realRoomDirectory);
        var roomAlias = Path.Combine(Path.GetTempPath(), "aer_memory_applier_room_alias_" + Guid.NewGuid().ToString("N"));

        try
        {
            if (!TryCreateDirectoryReparsePoint(roomAlias, realRoomDirectory, out var skipReason))
            {
                Assert.Skip(skipReason);
                return;
            }

            try
            {
                var memoryRoot = Path.Combine(roomAlias, MemoryProposalApplier.MemoryDirectoryName);
                Directory.CreateDirectory(memoryRoot);
                var realDirectory = Path.Combine(memoryRoot, "real");
                Directory.CreateDirectory(realDirectory);

                var aliasPath = Path.Combine(memoryRoot, "alias");
                if (!TryCreateDirectoryReparsePoint(aliasPath, realDirectory, out var innerSkipReason))
                {
                    Assert.Skip(innerSkipReason);
                    return;
                }

                try
                {
                    var capturePath = Path.Combine(_tempDirectory, "proposal-through-room-alias.json");
                    await File.WriteAllTextAsync(
                        capturePath,
                        """{"Operation":"add","TargetPath":"alias/fact.md","Content":"via room alias","Rationale":"learned it"}""",
                        TestContext.Current.CancellationToken);

                    await MemoryProposalApplier.ApplyAsync(roomAlias, capturePath, TestContext.Current.CancellationToken);

                    Assert.Equal(
                        "via room alias",
                        await File.ReadAllTextAsync(Path.Combine(realDirectory, "fact.md"), TestContext.Current.CancellationToken));
                }
                finally
                {
                    RemoveDirectoryLink(aliasPath);
                }
            }
            finally
            {
                RemoveDirectoryLink(roomAlias);
            }
        }
        finally
        {
            Directory.Delete(realRoomDirectory, recursive: true);
        }
    }

    /// <summary>
    /// #856's chain-following claim, which its own doc comment on
    /// <see cref="MemoryProposalApplier.ResolveReparsePointsIgnoringMissingTail"/> makes
    /// (<c>returnFinalTarget: true</c>) but nothing exercised until here. The shape is chosen so it
    /// can only pass by following the whole chain: the first hop lands <b>inside</b> memory/, so a
    /// single-hop resolution would find an in-tree path and allow the write, while the write itself
    /// would still land outside. A test using a first hop that already points outside would pass
    /// either way and prove nothing.
    /// </summary>
    [Fact]
    public async Task A_chained_reparse_point_whose_first_hop_stays_inside_memory_is_still_refused()
    {
        Directory.CreateDirectory(_memoryRoot);
        var outsideDirectory = Path.Combine(_tempDirectory, "outside");
        Directory.CreateDirectory(outsideDirectory);

        // hop2 sits inside memory/ and points outside; hop1 sits inside memory/ and points at hop2.
        var secondHop = Path.Combine(_memoryRoot, "hop2");
        if (!TryCreateDirectoryReparsePoint(secondHop, outsideDirectory, out var secondHopSkipReason))
        {
            Assert.Skip(secondHopSkipReason);
            return;
        }

        try
        {
            var firstHop = Path.Combine(_memoryRoot, "hop1");
            if (!TryCreateDirectoryReparsePoint(firstHop, secondHop, out var firstHopSkipReason))
            {
                Assert.Skip(firstHopSkipReason);
                return;
            }

            try
            {
                var capture = WriteCapture(
                    """{"Operation":"add","TargetPath":"hop1/pwned.md","Content":"pwned","Rationale":"malicious"}""");

                await Assert.ThrowsAsync<InvalidRoomMutationException>(
                    () => MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken));

                Assert.False(File.Exists(Path.Combine(outsideDirectory, "pwned.md")));
            }
            finally
            {
                RemoveDirectoryLink(firstHop);
            }
        }
        finally
        {
            RemoveDirectoryLink(secondHop);
        }
    }

    /// <summary>
    /// Covers the file half of <c>ResolveIfReparsePoint</c>'s <c>isDirectory ? Directory... :
    /// File...</c> branch, which the directory tests above leave unexercised. A file symlink is
    /// unprivileged on Linux/macOS but needs admin or Developer Mode on Windows, so this skips
    /// rather than fakes where it cannot be created.
    /// </summary>
    [Fact]
    public async Task A_file_reparse_point_under_memory_that_resolves_outside_it_is_refused()
    {
        Directory.CreateDirectory(_memoryRoot);
        var outsideDirectory = Path.Combine(_tempDirectory, "outside");
        Directory.CreateDirectory(outsideDirectory);
        var outsideFile = Path.Combine(outsideDirectory, "real.md");
        await File.WriteAllTextAsync(outsideFile, "outside content", TestContext.Current.CancellationToken);

        var linkPath = Path.Combine(_memoryRoot, "fact-link.md");
        try
        {
            File.CreateSymbolicLink(linkPath, outsideFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Could not create a file symlink in this environment: {ex.Message}");
            return;
        }

        try
        {
            var capture = WriteCapture(
                """{"Operation":"edit","TargetPath":"fact-link.md","Content":"pwned","Rationale":"malicious"}""");

            await Assert.ThrowsAsync<InvalidRoomMutationException>(
                () => MemoryProposalApplier.ApplyAsync(_tempDirectory, capture, TestContext.Current.CancellationToken));

            Assert.Equal("outside content", await File.ReadAllTextAsync(outsideFile, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(linkPath);
        }
    }

    /// <summary>
    /// Unlinks a reparse point created by <see cref="TryCreateDirectoryReparsePoint"/> without
    /// touching its target -- <c>Directory.Delete(path, recursive: false)</c> on a junction or
    /// directory symlink removes the link itself, never the linked-to contents.
    /// </summary>
    private static void RemoveDirectoryLink(string linkPath)
    {
        if (Directory.Exists(linkPath))
        {
            Directory.Delete(linkPath, recursive: false);
        }
    }

    /// <summary>
    /// Creates a directory reparse point at <paramref name="linkPath"/> pointing at <paramref
    /// name="targetPath"/>: a junction on Windows (via <c>mklink /J</c>, spawned directly rather
    /// than through a shell so path quoting cannot mangle it -- unprivileged, unlike a Windows
    /// directory symlink which needs admin or Developer Mode), or a directory symlink elsewhere
    /// (unprivileged on Linux/macOS). Returns false with a reason if the environment cannot host
    /// either -- the caller must skip rather than fake the arm.
    /// </summary>
    private static bool TryCreateDirectoryReparsePoint(string linkPath, string targetPath, out string skipReason)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                Directory.CreateSymbolicLink(linkPath, targetPath);
                skipReason = "";
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipReason = $"Could not create a directory symlink in this environment: {ex.Message}";
                return false;
            }
        }

        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)!;
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            skipReason = $"'mklink /J' exited {process.ExitCode}: {process.StandardError.ReadToEnd()}";
            return false;
        }

        skipReason = "";
        return true;
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
