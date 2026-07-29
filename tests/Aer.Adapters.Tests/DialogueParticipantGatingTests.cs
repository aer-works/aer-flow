using System.Text.Json;
using Aer.Flow.Domain;
using Aer.Workers.Dialogue;

namespace Aer.Adapters.Tests;

/// <summary>
/// #703: the dialogue worker spawns vendor CLIs itself, and until this existed it spawned them with
/// no <c>PreToolUse</c> gate at all. <see cref="DialogueWorkerAdapter"/> rewrites the authored config
/// so the worker only ever reads gated participants.
/// </summary>
[Collection(LaunchConfigCollection.Name)]
public class DialogueParticipantGatingTests : IDisposable
{
    private static readonly WorkerContract DebateContract = new("debate", [], [new ProducedOutput("verdict.md")], []);

    private readonly string root = Directory.CreateTempSubdirectory("dialogue-gating-").FullName;

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>A second side, so the config is legal — a dialogue of one is refused by the parser.</summary>
    private static readonly DialogueParticipant Foil = new(
        "foil", "stub", Model: null, "You reply.", "powershell", ["-File", "foil.ps1", "{PROMPT}"]);

    /// <summary>Writes a config whose FIRST participant is the one under test, with <see cref="Foil"/> second.</summary>
    private string WriteConfig(DialogueParticipant subject)
    {
        var path = Path.Combine(root, "dialogue-config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new DialogueWorkerConfig(
            SeedPrompt: "Open with your position.",
            TurnBudget: 2,
            FinalOutputName: "verdict.md",
            StopSentinel: null,
            Participants: [subject, Foil])));
        return path;
    }

    /// <summary>Reads back whatever config the adapter actually pointed the worker at.</summary>
    private DialogueWorkerConfig Resolved(string authoredPath, PermissionGrant? grant = null)
    {
        var target = new DialogueWorkerAdapter().Resolve(
            new WorkerInvocation(authoredPath, PermissionGrant: grant), DebateContract);

        // The config path is the argument after the worker dll, in both the cmd and sh shapes.
        var flat = string.Join(' ', target.Args);
        var start = flat.IndexOf(".json", StringComparison.Ordinal);
        Assert.True(start > 0, $"No config path found in resolved args: {flat}");

        var quoted = flat[..(start + ".json".Length)];
        var path = quoted[(quoted.LastIndexOfAny([' ', '"']) + 1)..];
        return DialogueWorkerConfigParser.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void A_claude_participant_reaches_the_worker_carrying_the_gate()
    {
        var authored = WriteConfig(new DialogueParticipant(
            "initiator", "claude", Model: null, "You argue for.", "claude",
            ["-p", "Read {PROMPT_FILE}", "--allowedTools", "Write,Read"]));

        var participant = Resolved(authored).Participants[0];

        Assert.Contains("--settings", participant.Args);
        Assert.NotNull(participant.Environment);
        Assert.Equal("0", participant.Environment[ClaudeWorkerAdapter.SimpleModeVariable]);

        // Args come from the preset, NOT from the config. An authored --bare or --mode yolo would sit
        // beside the gate and undo it, so a declared vendor means AER owns the whole invocation.
        var preset = DialogueParticipantPresets.For("claude", "initiator", "You argue for.", model: null);
        Assert.Equal(preset.Args, participant.Args.Take(preset.Args.Count));
    }

    /// <summary>
    /// The other half of the same rule: a declared vendor may not run an arbitrary command. Without
    /// this, AER would install claude's gate onto a process that ignores it and report an enforcement
    /// it does not have — worse than a known gap, because it reads as covered.
    /// </summary>
    [Fact]
    public void Declaring_a_vendor_while_running_something_else_is_refused()
    {
        var authored = WriteConfig(new DialogueParticipant(
            "initiator", "claude", Model: null, "You argue for.", "powershell",
            ["-File", "pretend.ps1", "{PROMPT}"]));

        var error = Assert.Throws<DialogueWorkerConfigException>(
            () => new DialogueWorkerAdapter().Resolve(new WorkerInvocation(authored), DebateContract));

        Assert.Contains("the only command it can run is 'claude'", error.Message, StringComparison.Ordinal);
        Assert.Contains("give it a Vendor of its own", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agy_participant_reaches_the_worker_carrying_its_add_dir_hook_directory()
    {
        var authored = WriteConfig(new DialogueParticipant(
            "responder", "gemini", Model: null, "You argue against.", "agy",
            ["-p", "Read {PROMPT_FILE}"]));

        var participant = Resolved(authored).Participants[0];

        Assert.Contains("--add-dir", participant.Args);
        Assert.Contains(participant.Args, arg => arg.Contains(GeminiWorkerAdapter.AgyWorkspaceDirectoryName, StringComparison.Ordinal));
    }

    /// <summary>
    /// The CONTROL, and the reason every existing dialogue test still passes untouched: a participant
    /// AER ships no gate for is left exactly as authored, so stub scripts keep working.
    /// </summary>
    [Fact]
    public void A_stub_participant_is_left_alone_and_no_rewritten_config_is_produced()
    {
        var authored = WriteConfig(new DialogueParticipant(
            "initiator", "stub", Model: null, "You argue for.", "powershell",
            ["-File", "initiator.ps1", "{PROMPT}"]));

        var target = new DialogueWorkerAdapter().Resolve(new WorkerInvocation(authored), DebateContract);

        Assert.Contains(authored, string.Join(' ', target.Args));
        var participant = Resolved(authored).Participants[0];
        Assert.Equal(["-File", "initiator.ps1", "{PROMPT}"], participant.Args);
        Assert.Null(participant.Environment);
    }

    /// <summary>
    /// Three lists must move together, and prose saying so is not a check (#703). Add a vendor to
    /// the presets and forget <see cref="VendorGate"/>, and a participant declaring it takes the
    /// unrecognised-vendor branch and runs UNGATED, silently — the exact defect this all exists to
    /// close, reintroduced by an omission with nothing red anywhere.
    /// </summary>
    [Fact]
    public void Every_known_vendor_has_a_gate_and_a_refusable_command_name()
    {
        Assert.NotEmpty(DialogueParticipantPresets.KnownVendors);

        foreach (var vendor in DialogueParticipantPresets.KnownVendors)
        {
            Assert.NotNull(VendorGate.For(vendor, grant: null));

            // And the command that vendor's preset actually runs must be one the refusal recognises,
            // or a participant could name it under a bogus vendor label and pass through untouched.
            var command = Path.GetFileNameWithoutExtension(
                DialogueParticipantPresets.For(vendor, "role", "preamble", model: null).Command);
            Assert.Contains(command, DialogueWorkerAdapter.GatedVendorCommands, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A shell wrapper reaches a vendor CLI as cheaply as relabelling does, and the first version of
    /// this refusal scanned only <c>Command</c> — so <c>cmd /c claude …</c> went straight through.
    /// </summary>
    [Theory]
    [InlineData("cmd", new[] { "/c", "claude", "-p", "{PROMPT}" })]
    // {PROMPT_FILE} rather than {PROMPT}: the parser requires {PROMPT} to be a whole argument, and a
    // shell -c string embeds its placeholder inside one.
    [InlineData("sh", new[] { "-c", "agy -p {PROMPT_FILE}" })]
    [InlineData("npx", new[] { "claude", "-p", "{PROMPT}" })]
    // Everything below this line reached a real vendor CLI UNREFUSED until a reviewer of #705
    // constructed them against the shipped code. None is adversarial — each is what someone writes
    // by habit, and the first is one character from the `sh -c` fixture three lines up.
    [InlineData("sh", new[] { "-lc", "agy -p {PROMPT_FILE}" })]          // clustered short flags
    [InlineData("bash", new[] { "-ec", "claude -p {PROMPT_FILE}" })]     // ditto, other order
    [InlineData("sh", new[] { "-c", "cd /repo && claude -p {PROMPT_FILE}" })]  // not the first segment
    [InlineData("sh", new[] { "-c", "exec agy -p {PROMPT_FILE}" })]      // not the first token
    [InlineData("sh", new[] { "-c", "FOO=1 claude -p {PROMPT_FILE}" })]  // assignment prefix
    [InlineData("sh", new[] { "-c", "true; agy -p {PROMPT_FILE}" })]     // second statement
    // Base64 UTF-16 for `claude -p hi`. -EncodedCommand was LISTED as handled while structurally
    // unable to match anything, which is the documentation defect of a true sentence read wrongly.
    // {PROMPT} rides as its own argument because the parser requires a VISIBLE placeholder — which
    // is itself a second, independent reason this shape cannot reach a vendor, found by writing the
    // test: without it the config is refused, but for placeholder reasons and not gate reasons.
    [InlineData("powershell", new[] { "-EncodedCommand", "YwBsAGEAdQBkAGUAIAAtAHAAIABoAGkA", "{PROMPT}" })]
    public void A_vendor_CLI_named_in_Args_behind_a_wrapper_is_refused(string command, string[] args)
    {
        var authored = WriteConfig(new DialogueParticipant(
            "wrapped", "definitely-not-a-vendor", Model: null, "p", command, args));

        var error = Assert.Throws<DialogueWorkerConfigException>(
            () => new DialogueWorkerAdapter().Resolve(new WorkerInvocation(authored), DebateContract));

        Assert.Contains("invokes a vendor CLI", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CONTROL for the refusal above, and the reason it does not simply scan every word.
    /// </summary>
    /// <remarks>
    /// Widening the scan is only safe while it stays in executable POSITIONS. A participant whose
    /// prompt or arguments merely mention a vendor is common and harmless, and refusing it would make
    /// the whole mechanism unusable — so each of these must resolve. If a future widening turns any
    /// of them red, the widening went too far, which is a defect and not an acceptable cost.
    /// </remarks>
    [Theory]
    [InlineData("powershell", new[] { "-File", "s.ps1", "{PROMPT}" })]
    // The vendor name in an ARGUMENT position of a shell string, never the executable position.
    [InlineData("sh", new[] { "-c", "echo 'argue as claude would' > {PROMPT_FILE}" })]
    [InlineData("sh", new[] { "-c", "./stub.sh --persona agy --input {PROMPT_FILE}" })]
    // A value that merely starts with a dash and ends in c must not read as a shell command switch.
    [InlineData("powershell", new[] { "-File", "s.ps1", "-abc", "claude is a model", "{PROMPT}" })]
    public void A_participant_that_only_MENTIONS_a_vendor_is_not_refused(string command, string[] args)
    {
        var authored = WriteConfig(new DialogueParticipant(
            "mentions", "stub", Model: null, "You reply.", command, args));

        var participant = Resolved(authored).Participants[0];

        Assert.Equal(args, participant.Args);
        Assert.Null(participant.Environment);
    }

    /// <summary>
    /// Relabelling the vendor is one move that would otherwise reach a real vendor CLI ungated,
    /// so it is refused rather than passed through as an unrecognised vendor.
    /// </summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("agy")]
    public void Claiming_an_unknown_vendor_while_running_a_real_vendor_CLI_is_refused(string command)
    {
        var authored = WriteConfig(new DialogueParticipant(
            "sneaky", "definitely-not-a-vendor", Model: null, "p", command, ["-p", "{PROMPT}"]));

        var error = Assert.Throws<DialogueWorkerConfigException>(
            () => new DialogueWorkerAdapter().Resolve(new WorkerInvocation(authored), DebateContract));

        // The message has to say what to do instead. A refusal reading only "unsupported" is a reason
        // to stop using AER and call the vendor CLI directly, which is the ungated state (#704).
        Assert.Contains("Set Vendor to one of", error.Message, StringComparison.Ordinal);
        Assert.Contains("claude", error.Message, StringComparison.Ordinal);
    }
}
