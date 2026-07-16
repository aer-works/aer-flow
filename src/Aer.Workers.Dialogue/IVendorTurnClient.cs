namespace Aer.Workers.Dialogue;

/// <summary>
/// Sends one turn's prompt to a participant's configured vendor CLI and returns the turn text it
/// produced (M17 Phase 2, #165). Extracted from <see cref="DialogueRunner"/> so tests can substitute
/// a stub without spawning any real process, the same reasoning
/// <c>Aer.Flow.Dispatch.ICoreDispatcher</c> already establishes for Flow's own dispatch seam.
/// </summary>
public interface IVendorTurnClient
{
    /// <summary>Runs <paramref name="participant"/>'s configured command with <paramref name="prompt"/> substituted in, returning its captured stdout.</summary>
    Task<string> SendTurnAsync(DialogueParticipant participant, string prompt, CancellationToken cancellationToken = default);
}
