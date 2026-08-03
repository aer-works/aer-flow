using System.Text.Json;
using Aer.Cli;

namespace Aer.Cli.Tests;

public class TemplatesCommandTests
{
    [Fact]
    public async Task ExecuteAsync_WithJsonFlag_EmitsValidTemplateJson()
    {
        using var writer = new StringWriter();
        var exitCode = await TemplatesCommand.ExecuteAsync(["--json"], writer, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);

        var output = writer.ToString();
        Assert.False(string.IsNullOrWhiteSpace(output));

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("advise", out var advise));
        Assert.True(root.TryGetProperty("implement", out var implement));
        Assert.True(root.TryGetProperty("review", out var review));
        Assert.True(root.TryGetProperty("fact-check", out var factCheck));
        Assert.True(root.TryGetProperty("janitor", out var janitor));

        Assert.Equal("gemini", advise.GetProperty("adapter").GetString());
        Assert.Equal("gemini-3.6-flash-high", advise.GetProperty("model").GetString());
        Assert.Equal(25, advise.GetProperty("timeout_minutes").GetInt32());

        Assert.Equal("claude", review.GetProperty("adapter").GetString());
        Assert.Equal("sonnet", review.GetProperty("model").GetString());
        Assert.Equal("high", review.GetProperty("effort").GetString());
        Assert.True(review.GetProperty("verdict_schema").GetBoolean());
    }

    [Fact]
    public async Task ExecuteAsync_WithoutJsonFlag_PrintsHumanReadableSummary()
    {
        using var writer = new StringWriter();
        var exitCode = await TemplatesCommand.ExecuteAsync([], writer, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);

        var output = writer.ToString();
        Assert.Contains("Available built-in workflow templates:", output);
        Assert.Contains("advise", output);
        Assert.Contains("implement", output);
        Assert.Contains("review", output);
    }
}
