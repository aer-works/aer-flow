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
public sealed record VendorTurnResult(string Text, int ExitCode, string StandardError);
