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
    /// Runs <paramref name="participant"/>'s configured command for one turn, substituting
    /// <paramref name="prompt"/> directly into the argv element equal to
    /// <see cref="DialogueParticipant.PromptPlaceholder"/>.
    /// <paramref name="sessionId"/> is this participant's vendor-native session id as of the
    /// <em>start</em> of this turn: <see langword="null"/> on that participant's first turn (nothing
    /// to resume yet), or the id <see cref="VendorTurnResult.SessionId"/> returned from that
    /// participant's immediately preceding turn (decision 0039). The returned
    /// <see cref="VendorTurnResult.SessionId"/> is what the caller passes back in on this
    /// participant's <em>next</em> turn.
    /// </summary>
    Task<VendorTurnResult> SendTurnAsync(
        DialogueParticipant participant, string prompt, string? sessionId = null, CancellationToken cancellationToken = default);
}
