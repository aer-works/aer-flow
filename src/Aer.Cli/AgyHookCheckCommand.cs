using System.Text.Json;

namespace Aer.Cli;

/// <summary>
/// <c>aer agy-hook-check</c> (#554): the executable target of the <c>PreToolUse</c> hook
/// <see cref="Aer.Adapters.GeminiWorkerAdapter"/> writes into every spawned agy worker's workspace
/// <c>.agents/hooks.json</c>. Not an operator-facing subcommand — <c>agy</c> invokes it itself, on
/// every matched tool call, and it receives the event JSON on stdin.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is deliberately not a shared implementation with <see cref="HookCheckCommand"/>, and the
/// two must not be merged.</b> The vendors agree on the idea and on nothing else: agy nests the tool
/// name at <c>toolCall.name</c> in camelCase where claude puts <c>tool_name</c> at the root, and agy
/// signals a verdict by writing a <c>decision</c> field to <b>stdout</b> where claude signals denial
/// by <b>exiting 2</b> with stderr as the reason. Running claude's command under agy would exit 2,
/// print nothing agy parses, and — per the measured behaviour below — be read as an <i>allow</i>.
/// </para>
/// <para>
/// <b>Fail-closed, unlike its claude sibling, and for a measured reason.</b>
/// <see cref="HookCheckCommand"/> argues its fail-open is acceptable because <c>--disallowedTools</c>
/// independently covers the same tool names. agy has no such flag: permission rules there are
/// global-only (<c>agy.permissions-are-global-only</c>, decision 0029), so this hook is the
/// <i>only</i> per-worker gate and anything it lets through is ungated outright.
/// </para>
/// <para>
/// <b>The specific failure that shapes this code</b> is measured by
/// <c>agy.hook-malformed-stdout-fails-open</c>: agy <i>allows</i> when hook stdout is unparseable or
/// absent, and <i>denies</i> when it parses but carries an unrecognised <c>decision</c> value. So a
/// crash, an unhandled exception writing a stack trace, or a silent exit is an <b>allow</b> — while
/// merely getting the verdict string wrong is safe. Everything here is therefore arranged so that a
/// syntactically valid JSON object reaches stdout on <i>every</i> path including the catch-all
/// handler, and nothing else is ever written to stdout.
/// </para>
/// <para>
/// Like its claude sibling this enforces category denial by tool name only. It does not inspect tool
/// arguments and does not attempt to close the substitution gap (#529's claude-side analogue; #596
/// is the agy-side over-grant this hook is a prerequisite for fixing). Withholding a category while
/// granting <c>run_command</c> still leaves that category reachable through the shell.
/// </para>
/// </remarks>
public static class AgyHookCheckCommand
{
    /// <summary>
    /// The environment variable carrying this invocation's denied-tool list — the same contract
    /// <see cref="HookCheckCommand.DeniedToolsEnvironmentVariable"/> uses, deliberately reused
    /// rather than duplicated per vendor: a worker is only ever one vendor, so the <i>values</i>
    /// differ (agy's <c>run_command</c> versus claude's <c>Bash</c>) while the channel need not.
    /// </summary>
    /// <remarks>
    /// That this channel works at all on agy is measured, not assumed:
    /// <c>agy.hook-env-inherited</c> (a sentinel) confirms an agy hook subprocess inherits the
    /// environment agy was spawned with. agy's own documentation
    /// (<c>.vendor-survey/corpus/agy__hooks.md</c>) is silent on inheritance — claude's states it
    /// explicitly — so carrying claude's answer across would have been exactly the population-scope
    /// mistake CLAUDE.md gate 4 names.
    /// </remarks>
    public const string DeniedToolsEnvironmentVariable =
        HookCheckCommand.DeniedToolsEnvironmentVariable;

    /// <summary>
    /// agy reads the verdict from stdout and the process exit code carries no gating meaning — a
    /// denial and an allow both exit 0. Compare <see cref="HookCheckCommand.DeniedExitCode"/>, where
    /// the exit code <i>is</i> the signal.
    /// </summary>
    public const int ExitCode = 0;

    private const string AllowJson = """{"decision":"allow"}""";

    /// <summary>
    /// Runs the check, writing exactly one JSON object to <paramref name="stdout"/>. Takes its
    /// streams and the raw env value as parameters rather than touching <see cref="Console"/> or
    /// <see cref="Environment"/> directly, so the decision logic is testable without a subprocess.
    /// </summary>
    public static int Execute(TextReader stdin, TextWriter stdout, string? deniedToolsRaw)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);

        try
        {
            stdout.Write(Decide(stdin, deniedToolsRaw));
        }
        catch (Exception ex)
        {
            // Deny, and say why in the reason agy shows the model. Reaching here means a defect in
            // Decide -- but an exception escaping this method would print a .NET stack trace to
            // stderr, leave stdout empty, and be read by agy as an ALLOW
            // (`agy.hook-malformed-stdout-fails-open`). On the vendor where this hook is the only
            // gate, a bug must not silently widen the grant it was installed to narrow. Serialized
            // rather than interpolated so a quote or newline in the message cannot produce the
            // unparseable output this handler exists to prevent.
            stdout.Write(DenyJson($"AER: the permission gate failed internally ({ex.GetType().Name}) " +
                                  "and denied this call rather than allowing it unchecked."));
        }

        return ExitCode;
    }

    private static string Decide(TextReader stdin, string? deniedToolsRaw)
    {
        // Drain stdin first and unconditionally: agy is the writer on the other end of this pipe,
        // and exiting before reading its full payload risks a blocked write on its side for any
        // tool_input large enough to fill the pipe buffer.
        string input;
        try
        {
            input = stdin.ReadToEnd();
        }
        catch (IOException)
        {
            // Could not read the payload, so the tool name is unknowable and this cannot be judged.
            // The claude sibling allows here; this must not.
            return DenyJson("AER: the permission gate could not read the hook payload and denied " +
                            "this call rather than allowing it unchecked.");
        }

        var denied = ParseDeniedTools(deniedToolsRaw);
        if (denied.Count == 0)
        {
            // Nothing is withheld for this invocation, so there is nothing this gate can object to.
            // Distinct from the failure paths above: this is a known-empty grant, not an unknown one.
            return AllowJson;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return DenyJson("AER: the permission gate received an empty hook payload and denied " +
                            "this call rather than allowing it unchecked.");
        }

        string? toolName;
        try
        {
            using var doc = JsonDocument.Parse(input);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("toolCall", out var toolCall) ||
                toolCall.ValueKind != JsonValueKind.Object ||
                !toolCall.TryGetProperty("name", out var nameProp))
            {
                return DenyJson("AER: the permission gate could not find toolCall.name in the hook " +
                                "payload and denied this call rather than allowing it unchecked.");
            }

            toolName = nameProp.GetString();
        }
        catch (JsonException)
        {
            return DenyJson("AER: the permission gate could not parse the hook payload and denied " +
                            "this call rather than allowing it unchecked.");
        }

        if (string.IsNullOrEmpty(toolName))
        {
            return DenyJson("AER: the permission gate read an empty tool name from the hook payload " +
                            "and denied this call rather than allowing it unchecked.");
        }

        return denied.Contains(toolName)
            ? DenyJson($"AER: the '{toolName}' tool is withheld by this session's permission grant.")
            : AllowJson;
    }

    /// <summary>
    /// Builds the denial object through <see cref="JsonSerializer"/> rather than string
    /// interpolation, so no reason text can produce output agy cannot parse — which it would read as
    /// an allow.
    /// </summary>
    private static string DenyJson(string reason) =>
        JsonSerializer.Serialize(new { decision = "deny", reason });

    private static HashSet<string> ParseDeniedTools(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);
}
