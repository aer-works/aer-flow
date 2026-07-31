using System.Text.Json.Serialization;

namespace Aer.Workers.Dialogue;

/// <summary>
/// The dialogue worker's own config surface (M17 Phase 2, #165; generalized to N-party M23 Phase 1,
/// #270) — deliberately not a <c>Aer.Flow</c> or <c>Aer.Adapters</c> type: per the milestone's
/// discipline/intelligence inversion (Flow spec §18.2, CLAUDE.md rule #1), turn budget, per-side
/// preambles, and stop condition are the worker's own concept, never a workflow-template or engine
/// concern. How this config reaches the worker at all (a required-input file path vs. some other
/// seam) is Phase 4's open question, left unresolved on purpose here — this type only defines its
/// shape.
/// </summary>
/// <param name="SeedPrompt">The exchange's opening prompt, sent to <see cref="Participants"/>'s first entry as its first turn.</param>
/// <param name="TurnBudget">
/// The maximum number of turns <see cref="DialogueRunner"/> runs, round-robining through
/// <see cref="Participants"/> in list order starting from index 0. The exchange may end earlier than
/// this when a participant calls the <c>aer yield</c> MCP tool (#585/decision 0035 — see
/// <see cref="DialogueYieldWiring"/>; this replaced the original M17 Phase 3 text-sentinel mechanism,
/// retired from the config surface entirely by #820); it never runs more than
/// <see cref="HardTurnCeiling"/> turns regardless of this value (M23 Phase 1's "safe by default"
/// requirement) — a configured value above the ceiling is silently clamped, never a config error,
/// since the ceiling exists to bound worst case cost, not to reject authoring intent.
/// </param>
/// <param name="FinalOutputName">
/// The declared output file name this worker writes on completion — the "declared final output" the
/// phase plan names, present so a caller's <c>WorkerContract</c> has something to validate once
/// Phase 4 wires dispatch up. What it contains is <see cref="FinalOutputMode"/>'s call — see that
/// parameter.
/// </param>
/// <param name="Participants">
/// The exchange's sides, in speaking order — turn 1 goes to <c>Participants[0]</c>, turn 2 to
/// <c>Participants[1]</c>, ..., wrapping back to <c>Participants[0]</c> after the last entry
/// (M23 Phase 1's N-party generalization of the prior fixed Initiator/Responder shape). Must contain
/// at least two entries: a "dialogue" with one side is not an exchange.
/// </param>
/// <param name="TurnTimeout">
/// The maximum wall-clock duration allowed for a single turn's execution across all participants
/// (defaulting to 5 minutes if omitted or non-positive).
/// </param>
/// <param name="FinalOutputMode">
/// Which <see cref="Aer.Workers.Dialogue.FinalOutputMode"/> this dialogue uses (#736) — see that
/// type for what each value writes to <see cref="FinalOutputName"/>. Defaults to
/// <see cref="Aer.Workers.Dialogue.FinalOutputMode.FinalTurn"/> when omitted.
/// </param>
public sealed record DialogueWorkerConfig(
    string SeedPrompt,
    int TurnBudget,
    string FinalOutputName,
    IReadOnlyList<DialogueParticipant> Participants,
    TimeSpan? TurnTimeout = null,
    [property: JsonConverter(typeof(FinalOutputModeJsonConverter))] FinalOutputMode? FinalOutputMode = null)
{
    /// <summary>
    /// Default per-turn ceiling (5 minutes).
    /// </summary>
    public static readonly TimeSpan DefaultTurnTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Default <see cref="Dialogue.FinalOutputMode"/> when a config omits it — this worker's
    /// original, still-default behavior of writing only the last turn's text.
    /// </summary>
    public const FinalOutputMode DefaultFinalOutputMode = Dialogue.FinalOutputMode.FinalTurn;

    /// <summary>
    /// The hard safety ceiling on turns <see cref="DialogueRunner"/> will ever actually run,
    /// enforced unconditionally regardless of a config's own <see cref="TurnBudget"/> (M23 Phase 1,
    /// #270: "safe by default"). Exists so an authoring mistake — or a config carrying a very large
    /// <see cref="TurnBudget"/> — can never turn one dialogue step into an unbounded vendor-CLI spend
    /// or an unbounded <c>transcript.jsonl</c> growth. Deliberately generous for the exchanges this
    /// worker is built for (a "bounded" multi-turn correspondence, per the original M17 phase plan),
    /// not a tuning knob exposed to authors.
    /// </summary>
    public const int HardTurnCeiling = 50;
}
