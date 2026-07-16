namespace Aer.Workers.Dialogue;

/// <summary>
/// One line of <c>transcript.jsonl</c> — the M17 Phase 2 (#165) schema settled as this milestone's
/// data model, per UI spec §10: every visible conversation step must correspond to a durable
/// artifact, and this record is that artifact. One JSON object per turn, newline-delimited (see
/// <see cref="TranscriptWriter"/>), in the order the turns actually happened.
/// <para>
/// This is worker <i>output</i>, never resumable state (Flow spec §18.2's explicit tradeoff): a
/// crash mid-exchange restarts the whole dialogue from turn one, so nothing reads this file back
/// to resume a run — only M18's future conversation view, and this worker's own final assembly of
/// <see cref="DialogueWorkerConfig.FinalOutputName"/>, ever consume it.
/// </para>
/// </summary>
/// <param name="Sequence">1-based position of this turn in the exchange.</param>
/// <param name="Role">
/// The speaking participant's configured <see cref="DialogueParticipant.Role"/> (e.g.
/// <c>"initiator"</c>/<c>"responder"</c>) — never a vendor name; a role can be re-bound to a
/// different vendor without changing what a reader of the transcript calls it.
/// </param>
/// <param name="Vendor">The speaking participant's <see cref="DialogueParticipant.Vendor"/> for this turn.</param>
/// <param name="Prompt">The exact prompt text sent to the vendor CLI for this turn (preamble plus threaded context).</param>
/// <param name="Text">The turn text the vendor CLI produced.</param>
public sealed record TranscriptTurn(
    int Sequence,
    string Role,
    string Vendor,
    string Prompt,
    string Text);
