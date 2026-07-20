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
/// this if a turn's text contains <see cref="StopSentinel"/> (M17 Phase 3, #166); it never runs more
/// than <see cref="HardTurnCeiling"/> turns regardless of this value (M23 Phase 1's "safe by
/// default" requirement) — a configured value above the ceiling is silently clamped, never a config
/// error, since the ceiling exists to bound worst case cost, not to reject authoring intent.
/// </param>
/// <param name="FinalOutputName">
/// The declared output file name this worker writes on completion (the last turn's text) — the
/// "declared final output" the phase plan names, present so a caller's <c>WorkerContract</c> has
/// something to validate once Phase 4 wires dispatch up.
/// </param>
/// <param name="StopSentinel">
/// A literal string a turn's text may contain to signal the exchange is done early (M17 Phase 3,
/// #166) — checked against each turn's raw text after it is confirmed non-empty; if present, the
/// sentinel substring is stripped from the recorded/threaded text and the exchange ends after that
/// turn, before <see cref="TurnBudget"/> is necessarily exhausted. Null or empty means no early stop
/// is configured — the exchange always runs the full (ceiling-clamped) <see cref="TurnBudget"/>.
/// </param>
/// <param name="Participants">
/// The exchange's sides, in speaking order — turn 1 goes to <c>Participants[0]</c>, turn 2 to
/// <c>Participants[1]</c>, ..., wrapping back to <c>Participants[0]</c> after the last entry
/// (M23 Phase 1's N-party generalization of the prior fixed Initiator/Responder shape). Must contain
/// at least two entries: a "dialogue" with one side is not an exchange.
/// </param>
public sealed record DialogueWorkerConfig(
    string SeedPrompt,
    int TurnBudget,
    string FinalOutputName,
    string? StopSentinel,
    IReadOnlyList<DialogueParticipant> Participants)
{
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
