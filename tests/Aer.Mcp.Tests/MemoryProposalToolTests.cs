using System.Text.Json;
using Aer.Mcp.Host;

namespace Aer.Mcp.Tests;

public class MemoryProposalToolTests
{
    [Fact]
    public void MissingOperation_ReturnsErrorAndWritesNoCaptureFile()
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var result = tool.Call(Parse("{\"targetPath\":\"foo.md\",\"rationale\":\"why\",\"content\":\"x\"}"));

            Assert.True(result.IsError);
            Assert.Contains("operation", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    [Fact]
    public void UnknownOperation_ReturnsErrorAndWritesNoCaptureFile()
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var result = tool.Call(Parse(
                "{\"operation\":\"delete-everything\",\"targetPath\":\"foo.md\",\"rationale\":\"why\"}"));

            Assert.True(result.IsError);
            Assert.Contains("add", result.Text);
            Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    [Fact]
    public void RootedTargetPath_ReturnsErrorAndWritesNoCaptureFile()
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var result = tool.Call(Parse(
                "{\"operation\":\"add\",\"targetPath\":\"C:/etc/passwd\",\"rationale\":\"why\",\"content\":\"x\"}"));

            Assert.True(result.IsError);
            Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    [Fact]
    public void TraversalTargetPath_ReturnsErrorAndWritesNoCaptureFile()
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var result = tool.Call(Parse(
                "{\"operation\":\"add\",\"targetPath\":\"../outside.md\",\"rationale\":\"why\",\"content\":\"x\"}"));

            Assert.True(result.IsError);
            Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    [Fact]
    public void MissingContentForAdd_ReturnsErrorAndWritesNoCaptureFile()
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var result = tool.Call(Parse(
                "{\"operation\":\"add\",\"targetPath\":\"foo.md\",\"rationale\":\"why\"}"));

            Assert.True(result.IsError);
            Assert.Contains("content", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    [Fact]
    public void MissingRationale_ReturnsErrorAndWritesNoCaptureFile()
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var result = tool.Call(Parse(
                "{\"operation\":\"add\",\"targetPath\":\"foo.md\",\"content\":\"x\"}"));

            Assert.True(result.IsError);
            Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    [Fact]
    public void ValidDelete_DoesNotRequireContentAndCaptures()
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var result = tool.Call(Parse(
                "{\"operation\":\"delete\",\"targetPath\":\"stale-fact.md\",\"rationale\":\"superseded\"}"));

            Assert.False(result.IsError);
            var file = Assert.Single(Directory.GetFiles(dir));
            var captured = JsonSerializer.Deserialize<MemoryProposalCapture>(File.ReadAllText(file));
            Assert.NotNull(captured);
            Assert.Equal("delete", captured!.Operation);
            Assert.Equal("stale-fact.md", captured.TargetPath);
            Assert.Null(captured.Content);
            Assert.Equal("superseded", captured.Rationale);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    [Fact]
    public void ValidAdd_RoundTripsEveryFieldIntoTheCaptureFile()
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var result = tool.Call(Parse(
                "{\"operation\":\"add\",\"targetPath\":\"new-fact.md\",\"content\":\"the fact\",\"rationale\":\"learned it\"}"));

            Assert.False(result.IsError);
            var file = Assert.Single(Directory.GetFiles(dir));
            var captured = JsonSerializer.Deserialize<MemoryProposalCapture>(File.ReadAllText(file));
            Assert.NotNull(captured);
            Assert.Equal("add", captured!.Operation);
            Assert.Equal("new-fact.md", captured.TargetPath);
            Assert.Equal("the fact", captured.Content);
            Assert.Equal("learned it", captured.Rationale);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    [Fact]
    public void TwoValidCalls_BothCaptureAsDistinctFiles()
    {
        var dir = TempDir();
        try
        {
            var tool = new MemoryProposalTool(dir);

            var first = tool.Call(Parse(
                "{\"operation\":\"add\",\"targetPath\":\"a.md\",\"content\":\"a\",\"rationale\":\"why a\"}"));
            var second = tool.Call(Parse(
                "{\"operation\":\"add\",\"targetPath\":\"b.md\",\"content\":\"b\",\"rationale\":\"why b\"}"));

            Assert.False(first.IsError);
            Assert.False(second.IsError);
            Assert.Equal(2, Directory.GetFiles(dir).Length);
        }
        finally
        {
            DeleteIfExists(dir);
        }
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string TempDir() => Path.Combine(Path.GetTempPath(), $"aer-memory-proposal-tool-test-{Guid.NewGuid():N}");

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
