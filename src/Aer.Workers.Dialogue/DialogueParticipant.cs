namespace Aer.Workers.Dialogue;

/// <summary>
/// One side of the exchange (M17 Phase 2, #165): the vendor CLI to invoke on this participant's
/// turns, plus how to invoke it. Two of these make up a <see cref="DialogueWorkerConfig"/> — this
/// is deliberately not the two vendor <c>Aer.Adapters</c> already knows how to invoke via a shell
/// wrapper (<c>ClaudeWorkerAdapter</c>/<c>GeminiWorkerAdapter</c>): those exist to satisfy Flow's
/// <c>AER_INPUT_&lt;n&gt;</c>/<c>AER_OUTPUT_DIR</c> convention for a top-level dispatch, which has
/// no meaning for a per-turn call made entirely inside this worker's own process. This
/// <see cref="Command"/>/<see cref="Args"/> shape is deliberately generic — a real vendor CLI's own
/// flag vocabulary (e.g. <c>claude</c>/<c>agy</c>'s, spike #21's realities) is authored directly into
/// a config's <see cref="Args"/> list, not hardcoded here — so the same shape points at a stub CLI in
/// tests without any shell involved (see <see cref="ProcessVendorTurnClient"/>).
/// </summary>
/// <param name="Role">
/// This side's logical name in the exchange (e.g. <c>"initiator"</c>/<c>"responder"</c>) — recorded
/// on every <see cref="TranscriptTurn"/> this participant produces. Never a vendor name: a
/// transcript reader should be able to tell who is *arguing which side*, independent of which
/// vendor currently plays that side.
/// </param>
/// <param name="Vendor">
/// The vendor this participant is bound to (e.g. <c>"claude"</c>, <c>"gemini"</c>) — recorded on
/// every turn, opaque to this worker beyond that (the same "adapter alone interprets it" reasoning
/// <c>Aer.Adapters.WorkerInvocation.PermissionScope</c> already establishes).
/// </param>
/// <param name="Model">The vendor model identifier to invoke, if the vendor takes one. Null when not applicable.</param>
/// <param name="Preamble">
/// This side's own per-turn instructional preamble (what this participant is arguing/reviewing
/// for), prepended to the threaded conversation context before each of its turns.
/// </param>
/// <param name="Command">The executable to spawn for this participant's turns (e.g. <c>claude</c>, <c>agy</c>, or a test stub binary/script).</param>
/// <param name="Args">
/// The literal argument list passed to <see cref="Command"/>, with exactly one element equal to
/// the literal token <c>"{PROMPT}"</c> substituted with the turn's actual prompt text at spawn
/// time (see <see cref="ProcessVendorTurnClient"/>). Every element is its own process argument —
/// no shell is involved, so no quoting/escaping question exists for this skeleton the way it does
/// for <c>Aer.Adapters</c>'s shell-wrapped invocations.
/// </param>
public sealed record DialogueParticipant(
    string Role,
    string Vendor,
    string? Model,
    string Preamble,
    string Command,
    IReadOnlyList<string> Args)
{
    /// <summary>The literal <see cref="Args"/> token <see cref="ProcessVendorTurnClient"/> substitutes with the actual prompt text.</summary>
    public const string PromptPlaceholder = "{PROMPT}";
}
