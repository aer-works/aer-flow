namespace Aer.Workers.Dialogue;

/// <summary>
/// Sends one turn's prompt to a participant's configured vendor CLI and returns what it produced
/// (M17 Phase 2, #165). Extracted from <see cref="DialogueRunner"/> so tests can substitute
/// a stub without spawning any real process, the same reasoning
/// <c>Aer.Flow.Dispatch.ICoreDispatcher</c> already establishes for Flow's own dispatch seam.
/// </summary>
public interface IVendorTurnClient
{
    /// <summary>
    /// Runs <paramref name="participant"/>'s configured command for one turn. What
    /// <paramref name="prompt"/> means depends on which placeholder <paramref name="participant"/>'s
    /// <see cref="DialogueParticipant.Args"/> use: with <see cref="DialogueParticipant.PromptPlaceholder"/>
    /// it is the literal prompt text, substituted directly into argv. With
    /// <see cref="DialogueParticipant.PromptFilePlaceholder"/> it is a path to a file already
    /// containing the prompt text (see <see cref="DialogueRunner"/>, which writes that file before
    /// calling this) — only the short path crosses the process boundary, which is what keeps a long
    /// exchange from ever exceeding the host OS's command-line length limit (#579).
    /// </summary>
    Task<VendorTurnResult> SendTurnAsync(DialogueParticipant participant, string prompt, CancellationToken cancellationToken = default);
}
