namespace Aer.Workers.Dialogue;

/// <summary>
/// The dialogue worker's own config surface (M17 Phase 2, #165) — deliberately not a
/// <c>Aer.Flow</c> or <c>Aer.Adapters</c> type: per the milestone's discipline/intelligence
/// inversion (Flow spec §18.2, CLAUDE.md rule #1), turn budget, per-side preambles, and stop
/// condition are the worker's own concept, never a workflow-template or engine concern. How this
/// config reaches the worker at all (a required-input file path vs. some other seam) is Phase 4's
/// open question, left unresolved on purpose here — this type only defines its shape.
/// </summary>
/// <param name="SeedPrompt">The exchange's opening prompt, sent to <see cref="Initiator"/> as its first turn.</param>
/// <param name="TurnBudget">
/// The maximum number of turns <see cref="DialogueRunner"/> runs, alternating
/// <see cref="Initiator"/>/<see cref="Responder"/> starting with <see cref="Initiator"/>. The exchange
/// may end earlier than this if a turn's text contains <see cref="StopSentinel"/> (M17 Phase 3, #166);
/// it never runs more than this many turns.
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
/// is configured — the exchange always runs the full <see cref="TurnBudget"/>.
/// </param>
/// <param name="Initiator">The side that speaks first, turns 1, 3, 5, ...</param>
/// <param name="Responder">The side that replies, turns 2, 4, 6, ...</param>
public sealed record DialogueWorkerConfig(
    string SeedPrompt,
    int TurnBudget,
    string FinalOutputName,
    string? StopSentinel,
    DialogueParticipant Initiator,
    DialogueParticipant Responder);
