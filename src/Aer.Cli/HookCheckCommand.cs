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
/// This enforces the category denial <see cref="Aer.Adapters.ClaudeWorkerAdapter.BuildHookDeniedTools"/>
/// computes. For reads, shell and network it is a second mechanism reaching the same names
/// <c>--disallowedTools</c> already carries; for <b>writes it is the only one</b>, since #649 moved
/// those names off that flag so this hook can allow the write landing in <c>AER_OUTPUT_DIR</c>. It
/// does not attempt to close the <c>Bash</c>-substitution gap #529 measured (a withheld write
/// category is still reachable through a granted shell) — that is explicitly out of scope here; see
/// #529's own doc comment on <c>BuildDisallowedTools</c>. What this buys is the mechanism 0029
/// requires — a <c>PreToolUse</c> hook that can exit 2 — wired up and independently verifiable,
/// which #532 needs a real positive control to check.
/// <para>
/// <b>Fails closed on every input it cannot judge</b> — unreadable stdin, empty stdin, malformed
/// JSON, a missing or empty <c>tool_name</c>, and any unhandled defect. Until #649 each of those
/// allowed, on the argument that <c>--disallowedTools</c> covered the same names anyway; once writes
/// ride this hook alone that argument is void, and a parse failure would be an ungated write.
/// <c>HookFailClosedTests</c> holds every one of those paths to exit 2.
/// </para>
/// <para>
/// What it still does not bound: the tool the model <em>substitutes</em>. A granted <c>Bash</c>
/// defeats a withheld write/read/network category regardless of what this decides, so this remains
/// a category gate rather than a security boundary — and the one failure it cannot reach at all is
/// its own command failing to start, which is measured to fail open on both vendors
/// (<c>gate.broken-hook-fails-open</c>) and is #532's.
/// </para>
/// </remarks>
public static class HookCheckCommand
{
    /// <summary>
    /// The environment variable this command reads for the current invocation's denied-tool list,
    /// comma-joined tool names exactly as <c>BuildHookDeniedTools</c> emits them, vendor-tagged (e.g.
    /// <c>"claude:Edit,Write,NotebookEdit"</c>). <see cref="Aer.Adapters"/> cannot reference
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
    /// <param name="outboxDirectory">
    /// This execution's <c>AER_OUTPUT_DIR</c> (#649). A withheld write whose target resolves inside it
    /// is allowed: that directory is AER's own, outside the workspace, and withholding "modify the
    /// workspace" was never meant to withhold "write your report". <see langword="null"/> disables the
    /// exemption entirely, so a hook that cannot tell where the outbox is denies as before.
    /// </param>
    public static int Execute(
        TextReader stdin, TextWriter stderr, string? deniedToolsRaw, string? outboxDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stderr);

        try
        {
            return Decide(stdin, stderr, deniedToolsRaw, outboxDirectory);
        }
        catch (Exception ex)
        {
            // A defect in Decide must not widen the grant it was installed to narrow. Claude Code
            // treats exit 2 as a blocking denial and *every other* non-zero code as a non-blocking
            // error it reports and then proceeds past -- so an unhandled exception's own exit code is
            // an allow. Naming DeniedExitCode here is what makes the failure closed.
            stderr.WriteLine(
                $"AER: the permission gate failed internally ({ex.GetType().Name}) and denied this " +
                "call rather than allowing it unchecked.");
            return DeniedExitCode;
        }
    }

    private static int Decide(
        TextReader stdin, TextWriter stderr, string? deniedToolsRaw, string? outboxDirectory)
    {
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
            return Deny(stderr, "could not read the hook payload");
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
            return Deny(stderr, "received an empty hook payload");
        }

        string? toolName;
        string? writeTarget = null;
        try
        {
            using var doc = JsonDocument.Parse(input);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("tool_name", out var toolNameProp))
            {
                return Deny(stderr, "could not find tool_name in the hook payload");
            }

            toolName = toolNameProp.GetString();
            writeTarget = ReadWriteTarget(doc.RootElement, toolName);
        }
        catch (JsonException)
        {
            return Deny(stderr, "could not parse the hook payload");
        }

        if (string.IsNullOrEmpty(toolName))
        {
            return Deny(stderr, "read an empty tool name from the hook payload");
        }

        if (denied.Contains(toolName))
        {
            // #649: the outbox is not the workspace. A withheld write landing in AER_OUTPUT_DIR is the
            // worker producing its declared output, which is the whole reason it was dispatched --
            // denying it is what forced every reviewing template to grant a workspace write it never
            // needed. Anything outside stays denied, and OutboxPath resolves both sides so neither a
            // traversal nor a link can walk back into the repo.
            if (OutboxPath.IsInsideOutbox(writeTarget, outboxDirectory))
            {
                return AllowedExitCode;
            }

            stderr.WriteLine(
                $"AER: the '{toolName}' tool is withheld by this session's permission grant.");
            return DeniedExitCode;
        }

        return AllowedExitCode;
    }

    /// <summary>
    /// The fail-closed exits. Every one of these was an <see cref="AllowedExitCode"/> until #649, on
    /// the argument that <c>--disallowedTools</c> independently covered the same tool names — which
    /// #649 made false for writes by moving them off that flag onto this hook alone.
    /// </summary>
    private static int Deny(TextWriter stderr, string what)
    {
        stderr.WriteLine($"AER: the permission gate {what} and denied this call rather than " +
                         "allowing it unchecked.");
        return DeniedExitCode;
    }

    /// <summary>Mirrors <c>ClaudeWorkerAdapter.DeniedToolsVendorTag</c>; see it for why (#600).</summary>
    private const string VendorTag = "claude";

    /// <summary>
    /// The filesystem path a write-family tool is targeting, or <see langword="null"/> for any other
    /// tool. Claude Code names it <c>file_path</c> on <c>Write</c>/<c>Edit</c> and
    /// <c>notebook_path</c> on <c>NotebookEdit</c>.
    /// </summary>
    /// <remarks>
    /// Gated on <paramref name="toolName"/>, not on the presence of the property: <c>Read</c> carries
    /// a <c>file_path</c> too, so keying off the field alone exempted reads inside the outbox from a
    /// withheld <c>ReadFiles</c> — a category #649 never meant to touch. The exemption exists because
    /// a withheld *write* still owes its declared output; nothing else claims it.
    /// </remarks>
    private static string? ReadWriteTarget(JsonElement root, string? toolName)
    {
        if (toolName is null || !WriteFamilyTools.Contains(toolName))
        {
            return null;
        }

        if (!root.TryGetProperty("tool_input", out var toolInput) ||
            toolInput.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in WriteTargetProperties)
        {
            if (toolInput.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static readonly string[] WriteTargetProperties = ["file_path", "notebook_path"];

    /// <summary>
    /// The tools the outbox exemption applies to — the same three
    /// <c>ClaudeWorkerAdapter.BuildHookDeniedTools</c> moves off <c>--disallowedTools</c>, and no
    /// others. <c>DeniedToolChannelTests</c> fails if the two lists drift apart.
    /// </summary>
    private static readonly HashSet<string> WriteFamilyTools =
        new(StringComparer.Ordinal) { "Edit", "Write", "NotebookEdit" };
}
