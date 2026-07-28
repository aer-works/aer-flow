using System.Text.Json;

namespace Aer.Cli;

/// <summary>
/// <c>aer hook-check</c> (#543): the executable target of the <c>PreToolUse</c> hook
/// <see cref="Aer.Adapters.ClaudeWorkerAdapter"/> writes into every spawned worker's
/// <c>claude-settings.json</c>. Not an operator-facing subcommand — Claude Code invokes this
/// itself, on every tool call, spawned directly with no shell (exec form: <c>args</c> is set on
/// the hook handler), so it receives the event JSON on stdin exactly as documented in
/// <c>.vendor-survey/corpus/claude__hooks.md</c>.
/// </summary>
/// <remarks>
/// This enforces the same category denial <see cref="Aer.Adapters.ClaudeWorkerAdapter.BuildDisallowedTools"/>
/// already computes for <c>--disallowedTools</c> — it is a second, independent mechanism reaching
/// the same tool names, not a wider one. It does not inspect <c>tool_input</c> and does not attempt
/// to close the <c>Bash</c>-substitution gap #529 measured (a withheld write category is still
/// reachable through a granted shell) — that is explicitly out of scope here; see #529's own doc
/// comment on <c>BuildDisallowedTools</c>. What this buys is the mechanism 0029 requires — a
/// <c>PreToolUse</c> hook that can exit 2 — wired up and independently verifiable, which #532 needs
/// a real positive control to check.
/// <para>
/// Fails open on any input it cannot parse (empty stdin, malformed JSON, a missing
/// <c>tool_name</c> field). <c>--disallowedTools</c> covers the exact same tool names, so a parse
/// failure here does not create a hole wider than what already exists there — but neither mechanism
/// is a security boundary for those names to begin with (see <c>BuildDisallowedTools</c>'s own doc
/// comment, #529, measured: a granted <c>Bash</c> defeats a withheld write/read/network category
/// regardless of which of these two enforces the write/read/network tool names directly). This
/// method's fail-open only means "no worse than the pre-existing gap," never "safe."
/// </para>
/// </remarks>
public static class HookCheckCommand
{
    /// <summary>
    /// The environment variable this command reads for the current invocation's denied-tool list,
    /// comma-joined tool names exactly as <c>BuildDisallowedTools</c> emits them (e.g.
    /// <c>"Edit,Write,NotebookEdit"</c>). <see cref="Aer.Adapters"/> cannot reference
    /// <see cref="Aer.Cli"/> (the CLI depends on the adapters, never the reverse), so this name is a
    /// plain string contract mirrored on <c>ClaudeWorkerAdapter.DeniedToolsVariable</c> — both sides
    /// assert the literal value in their own test suite, and the two must agree.
    /// </summary>
    public const string DeniedToolsEnvironmentVariable = "AER_HOOK_DENIED_TOOLS";

    /// <summary>
    /// Exit code 2, fed back to Claude Code as a blocking <c>PreToolUse</c> error (stderr becomes
    /// the reason shown to the model) — the only exit code that mechanism treats as a denial.
    /// </summary>
    public const int DeniedExitCode = 2;

    public const int AllowedExitCode = 0;

    /// <summary>
    /// Runs the check. Takes <paramref name="stdin"/>/<paramref name="stderr"/> and the raw env var
    /// value as parameters, rather than reading <see cref="Console"/>/<see cref="Environment"/>
    /// directly, so the decision logic is testable without a real subprocess.
    /// </summary>
    public static int Execute(TextReader stdin, TextWriter stderr, string? deniedToolsRaw)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stderr);

        // Always drain stdin before deciding anything, even when there is nothing to check
        // against below: Claude Code is the writer on the other end of this pipe, and exiting
        // before reading its full payload risks a broken-pipe/blocked-write on its side for any
        // tool_input large enough to fill the pipe buffer (a real Edit/Write payload can be).
        string input;
        try
        {
            input = stdin.ReadToEnd();
        }
        catch (IOException)
        {
            return AllowedExitCode;
        }

        var deniedList = DeniedToolList.Parse(deniedToolsRaw, VendorTag);
        if (deniedList.Status != DeniedToolListStatus.Present)
        {
            // #600: absent, or another vendor's list. Either way this gate cannot say what is
            // withheld, and the old behaviour — allow — made a broken channel look like a working one.
            return DeniedExitCode;
        }

        var denied = deniedList.Tools;
        if (denied.Count == 0)
        {
            // AER set the list and nothing is withheld. BuildDisallowedTools returns empty whenever
            // PermissionGrant is null (the raw PermissionScope escape hatch), which is the ordinary
            // `aer run` shape, so this must allow.
            return AllowedExitCode;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return AllowedExitCode;
        }

        string? toolName;
        try
        {
            using var doc = JsonDocument.Parse(input);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("tool_name", out var toolNameProp))
            {
                return AllowedExitCode;
            }

            toolName = toolNameProp.GetString();
        }
        catch (JsonException)
        {
            return AllowedExitCode;
        }

        if (toolName is not null && denied.Contains(toolName))
        {
            stderr.WriteLine(
                $"AER: the '{toolName}' tool is withheld by this session's permission grant.");
            return DeniedExitCode;
        }

        return AllowedExitCode;
    }

    /// <summary>Mirrors <c>ClaudeWorkerAdapter.DeniedToolsVendorTag</c>; see it for why (#600).</summary>
    private const string VendorTag = "claude";
}
