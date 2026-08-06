namespace Aer.Workers.Dialogue;

/// <summary>
/// One side of the exchange (M17 Phase 2, #165): the vendor CLI to invoke on this participant's
/// turns, plus how to invoke it. Two of these make up a <see cref="DialogueWorkerConfig"/> — this
/// is deliberately not the two vendor <c>Aer.Adapters</c> already knows how to invoke via a shell
/// wrapper (<c>ClaudeWorkerAdapter</c>/<c>AgyWorkerAdapter</c>): those exist to satisfy Flow's
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
/// The vendor this participant is bound to (e.g. <c>"claude"</c>, <c>"agy"</c>) — recorded on
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
/// The literal argument list passed to <see cref="Command"/>, with the element equal to
/// <see cref="PromptPlaceholder"/> (<c>"{PROMPT}"</c>) substituted with this turn's bounded prompt
/// text at spawn time (see <see cref="ProcessVendorTurnClient"/>). Every element is its own process
/// argument — no shell is involved, so no quoting/escaping question exists for this skeleton the way
/// it does for <c>Aer.Adapters</c>'s shell-wrapped invocations. <see cref="ProcessVendorTurnClient"/>
/// separately injects the vendor-native session-continuation flags (<c>--session-id</c>/<c>--resume</c>
/// for <c>claude</c>, <c>--conversation</c> for <c>agy</c>) — those are never authored here, the same
/// "vendor differences stay inside the preset/client layer" reasoning <see cref="DialogueYieldWiring"/>
/// already applies to MCP wiring (decision 0039).
/// </param>
/// <param name="Environment">
/// Extra environment variables to set on this participant's spawned process, or <see langword="null"/>
/// to add none. Opaque here by design: the names and values are a vendor concern
/// (<c>CLAUDE.md</c> Architecture Rule 2), so <c>Aer.Adapters</c> computes them and this worker only
/// applies them. It is how the gate's denied-tools channel and its
/// <c>CLAUDE_CODE_SIMPLE=0</c> neutralisation reach a participant's process (#703) — the parts of the
/// gate that do travel in the environment, as opposed to <c>claude</c>'s <c>--settings</c> argument
/// and <c>agy</c>'s workspace <c>.agents/hooks.json</c>, which do not and are carried by
/// <see cref="Command"/>/<see cref="Args"/> instead.
/// <para>
/// Added rather than replacing the inherited environment: narrowing what a worker can see is
/// #549's question and is decided for every spawn path at once, not here.
/// </para>
/// </param>
public sealed record DialogueParticipant(
    string Role,
    string Vendor,
    string? Model,
    string Preamble,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string>? Environment = null)
{
    /// <summary>The literal <see cref="Args"/> token <see cref="ProcessVendorTurnClient"/> substitutes with the actual prompt text.</summary>
    public const string PromptPlaceholder = "{PROMPT}";
}
