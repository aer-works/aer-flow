using System.Text;
using System.Text.Json;
using Aer.Flow.Domain;

namespace Aer.Flow.Tests.Domain;

/// <summary>
/// The parse floor of spec §4.2's <c>Diff</c> schema (#881): what must be present, what empty
/// inputs mean, and what bad shapes are refused.
/// </summary>
public class UnifiedDiffSchemaTests
{
    [Fact]
    public void A_real_multi_hunk_diff_parses_as_valid()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """
            --- a/src/File.cs
            +++ b/src/File.cs
            @@ -1,3 +1,3 @@
            -old line
            +new line
             context
            @@ -10,2 +10,2 @@
            -another old line
            +another new line
            """);

        Assert.True(UnifiedDiffSchema.TryParse(bytes, out var diff, out var error));
        Assert.Null(error);
        Assert.NotNull(diff);
        Assert.Contains("--- a/src/File.cs", diff);
    }

    [Fact]
    public void An_empty_file_parses_as_valid_meaning_no_change_proposed()
    {
        var bytes = Array.Empty<byte>();

        Assert.True(UnifiedDiffSchema.TryParse(bytes, out var diff, out var error));
        Assert.Null(error);
        Assert.Equal("", diff);
    }

    [Fact]
    public void A_whitespace_only_file_parses_as_valid()
    {
        var bytes = Encoding.UTF8.GetBytes("   \n\t  \r\n ");

        Assert.True(UnifiedDiffSchema.TryParse(bytes, out var diff, out var error));
        Assert.Null(error);
        Assert.NotNull(diff);
    }

    [Fact]
    public void Prose_mentioning_hunk_header_without_file_headers_is_refused_with_a_sentence()
    {
        var bytes = Encoding.UTF8.GetBytes("Prose description @@ -1,3 +1,3 @@ without headers");

        Assert.False(UnifiedDiffSchema.TryParse(bytes, out var diff, out var error));
        Assert.Null(diff);
        Assert.NotNull(error);
        Assert.Contains("No valid hunk header", error);
    }

    [Fact]
    public void Hunk_header_without_preceding_file_header_pair_is_refused_with_a_sentence()
    {
        var bytes = Encoding.UTF8.GetBytes("@@ -1,3 +1,3 @@\n-old\n+new");

        Assert.False(UnifiedDiffSchema.TryParse(bytes, out var diff, out var error));
        Assert.Null(diff);
        Assert.NotNull(error);
        Assert.Contains("without a preceding '--- '/'+++ ' file-header pair", error);
    }

    [Fact]
    public void A_diff_with_headers_but_no_hunk_is_refused_with_a_sentence()
    {
        var bytes = Encoding.UTF8.GetBytes("--- a/src/File.cs\n+++ b/src/File.cs\n");

        Assert.False(UnifiedDiffSchema.TryParse(bytes, out var diff, out var error));
        Assert.Null(diff);
        Assert.NotNull(error);
        Assert.Contains("No valid hunk header", error);
    }

    [Fact]
    public void Invalid_utf8_bytes_are_refused_without_throwing()
    {
        var bytes = new byte[] { 0xFF, 0xFE, 0xFD };

        Assert.False(UnifiedDiffSchema.TryParse(bytes, out var diff, out var error));
        Assert.Null(diff);
        Assert.NotNull(error);
        Assert.Contains("not valid UTF-8 text", error);
    }

    [Fact]
    public void ProducedOutput_serializes_Diff_Schema_as_a_string_and_deserializes()
    {
        var serialized = JsonSerializer.Serialize(new ProducedOutput("patch.diff", Schema: OutputSchema.Diff));
        Assert.Contains("\"Diff\"", serialized);

        var roundTripped = JsonSerializer.Deserialize<ProducedOutput>(serialized);
        Assert.Equal(OutputSchema.Diff, roundTripped!.Schema);

        var caseInsensitive = JsonSerializer.Deserialize<ProducedOutput>(
            """{"Name": "patch.diff", "Schema": "diff"}""");
        Assert.Equal(OutputSchema.Diff, caseInsensitive!.Schema);
    }
}
