namespace Aer.Workers.Dialogue;

/// <summary>
/// What a participant's vendor CLI produced for one turn (M17 Phase 3, #166): the captured stdout
/// text, the process's exit code, and any captured stderr — enough for <see cref="DialogueRunner"/>
/// to classify the turn as successful or failed without <see cref="IVendorTurnClient"/> itself
/// having to know what a "failed turn" means. Mirrors the discipline/intelligence split the phase
/// plan draws inside the worker boundary: the client reports mechanically, the runner interprets.
/// </summary>
/// <param name="Text">Captured stdout, trimmed of a trailing newline. May be empty — an empty turn is <see cref="DialogueRunner"/>'s concern, not this type's.</param>
/// <param name="ExitCode">The spawned process's exit code. Non-zero is <see cref="DialogueRunner"/>'s signal to fail the exchange, the same "exit code alone is not success" reasoning <c>Aer.Flow.Outcomes.OutcomeClassifier</c> applies one layer up.</param>
/// <param name="StandardError">Captured stderr, trimmed of a trailing newline. Never parsed for meaning — carried only so a non-zero-exit failure message can show a human what the vendor CLI actually said.</param>
/// <param name="TimedOut">True ONLY if AER's own turn timeout mechanism killed the child process due to exceeding <c>TurnTimeout</c>.</param>
/// <param name="SessionId">
/// The vendor-native session identifier this participant's session now sits at (decision 0039): for
/// <c>claude</c>, the id just established (turn 1) or the same id passed in (a resumed turn); for
/// <c>agy</c>, the id scraped from its <c>--log-file</c> output on the turn that established it (turn
/// 1) or the same id passed in (a resumed turn) — or <see langword="null"/> if a fresh <c>agy</c> turn
/// produced no parseable id, meaning the session is still unestablished and the next turn tries again
/// the same way. <see cref="DialogueRunner"/> carries this forward as the <c>sessionId</c> it passes to
/// this participant's next turn; a vendor this worker's client does not special-case echoes back
/// whatever it was given.
/// </param>
public sealed record VendorTurnResult(string Text, int ExitCode, string StandardError, bool TimedOut = false, string? SessionId = null);

