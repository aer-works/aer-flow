using System.Text;
using System.Text.RegularExpressions;

namespace Aer.Flow.Domain;

/// <summary>
/// The parse half of the unified diff schema contract (#881): turns bytes on disk into a validated
/// unified diff string or one sentence saying why they are not one.
/// <para>
/// An empty (or whitespace-only) file is valid and means "no change proposed." A reviewer or patch
/// worker that finds nothing must not have to fail its contract or fabricate a hunk, and an empty
/// patch is a clean no-op to <c>git apply</c>.
/// </para>
/// <para>
/// This validator is parse-only (decision 0043, Architecture Rule 1): it checks that non-empty
/// content matches the unified diff format (file-header pair <c>--- </c>/<c>+++ </c> followed by at
/// least one hunk header <c>@@ -n[,n] +n[,n] @@</c>). It does NOT prove that the patch applies
/// against any given tree; only <c>git apply --check</c> proves that, which is deliberately out of
/// scope here.
/// </para>
/// </summary>
public static class UnifiedDiffSchema
{
    private static readonly Regex HunkHeaderRegex = new(@"^@@ -\d+(?:,\d+)? \+\d+(?:,\d+)? @@", RegexOptions.Compiled);

    /// <summary>
    /// True with non-null <paramref name="diff"/> when <paramref name="bytes"/> parse and pass the
    /// unified diff parse-only floor (or are empty/whitespace-only); false with a human-readable
    /// <paramref name="error"/> sentence otherwise.
    /// Never throws on bad content — worker-written content must land as a classified failure,
    /// not an escaped exception.
    /// </summary>
    public static bool TryParse(byte[] bytes, out string? diff, out string? error)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        diff = null;

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (Exception)
        {
            error = "The diff document is not valid UTF-8 text.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            diff = text;
            error = null;
            return true;
        }

        var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var seenMinusHeader = false;
        var hasFileHeaderPair = false;
        var hunkCount = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                seenMinusHeader = true;
                hasFileHeaderPair = false;
            }
            else if (seenMinusHeader && line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                hasFileHeaderPair = true;
            }
            else if (HunkHeaderRegex.IsMatch(line))
            {
                if (!hasFileHeaderPair)
                {
                    error = "Found a hunk header without a preceding '--- '/'+++ ' file-header pair.";
                    return false;
                }

                hunkCount++;
            }
        }

        if (hunkCount == 0)
        {
            error = "No valid hunk header (@@ -n,n +n,n @@) found in diff.";
            return false;
        }

        diff = text;
        error = null;
        return true;
    }
}
