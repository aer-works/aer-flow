using System.Text;
using System.Text.Json;
using Aer.Workers.Dialogue;

namespace Aer.Workers.Dialogue.Tests;

public class TranscriptWriterTests
{
    [Fact]
    public async Task Appends_one_newline_terminated_JSON_object_per_turn()
    {
        using var stream = new MemoryStream();
        await using (var writer = new TranscriptWriter(stream, leaveOpen: true))
        {
            await writer.AppendAsync(new TranscriptTurn(1, "initiator", "claude", "prompt one", "text one"));
            await writer.AppendAsync(new TranscriptTurn(2, "responder", "gemini", "prompt two", "text two"));
        }

        var content = Encoding.UTF8.GetString(stream.ToArray());
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.True(content.EndsWith('\n'));

        var first = JsonSerializer.Deserialize<TranscriptTurn>(lines[0])!;
        Assert.Equal(1, first.Sequence);
        Assert.Equal("initiator", first.Role);
        Assert.Equal("claude", first.Vendor);
        Assert.Equal("prompt one", first.Prompt);
        Assert.Equal("text one", first.Text);

        var second = JsonSerializer.Deserialize<TranscriptTurn>(lines[1])!;
        Assert.Equal(2, second.Sequence);
        Assert.Equal("responder", second.Role);
    }

    [Fact]
    public async Task Creates_parent_directories_when_writing_to_a_file_path()
    {
        var root = Path.Combine(Path.GetTempPath(), $"transcript-writer-{Guid.NewGuid():N}");
        var transcriptPath = Path.Combine(root, "nested", "transcript.jsonl");
        try
        {
            await using (var writer = new TranscriptWriter(transcriptPath))
            {
                await writer.AppendAsync(new TranscriptTurn(1, "initiator", "claude", "p", "t"));
            }

            Assert.True(File.Exists(transcriptPath));
            var lines = await File.ReadAllLinesAsync(transcriptPath);
            Assert.Single(lines);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
