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

        // The authored args survive; the gate is added, never a replacement for what was written.
        Assert.Contains("--allowedTools", participant.Args);
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
    /// Relabelling the vendor is the one move that would otherwise reach a real vendor CLI ungated,
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
