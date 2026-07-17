namespace Aer.Workers.Dialogue;

/// <summary>
/// The known vendors' participant invocation shapes (M19 Phase 4, issue #189) — the same
/// one-shot-text-turn flags <c>ClaudeWorkerAdapter</c>/<c>GeminiWorkerAdapter</c> build for a
/// top-level dispatch, minus Flow's <c>AER_INPUT_&lt;n&gt;</c>/<c>AER_OUTPUT_DIR</c> convention
/// (see <see cref="DialogueParticipant"/>'s remarks), previously duplicated by hand in every
/// smoke test and now owned here: the worker that invokes the participants is where their
/// invocation knowledge lives, so the UI's guided authoring can offer vendor presets without
/// re-encoding any adapter quirk (the phase's named open question).
/// </summary>
public static class DialogueParticipantPresets
{
    public static readonly IReadOnlyList<string> KnownVendors = ["claude", "gemini"];

    /// <summary>Builds a real vendor participant; throws for a vendor no preset exists for — callers offer <see cref="KnownVendors"/>, they never free-type.</summary>
    public static DialogueParticipant For(string vendor, string role, string preamble, string? model)
    {
        ArgumentException.ThrowIfNullOrEmpty(vendor);
        ArgumentException.ThrowIfNullOrEmpty(role);
        ArgumentException.ThrowIfNullOrEmpty(preamble);

        return vendor switch
        {
            "claude" => new DialogueParticipant(
                role, vendor, model, preamble, "claude",
                model is null
                    ? ["-p", DialogueParticipant.PromptPlaceholder, "--allowedTools", "Write", "--output-format", "text"]
                    : ["-p", DialogueParticipant.PromptPlaceholder, "--allowedTools", "Write", "--output-format", "text", "--model", model]),
            "gemini" => new DialogueParticipant(
                role, vendor, model, preamble, "agy",
                model is null
                    ? ["-p", DialogueParticipant.PromptPlaceholder, "--mode", "accept-edits"]
                    : ["-p", DialogueParticipant.PromptPlaceholder, "--mode", "accept-edits", "--model", model]),
            _ => throw new ArgumentException($"No participant preset exists for vendor '{vendor}'.", nameof(vendor)),
        };
    }
}
