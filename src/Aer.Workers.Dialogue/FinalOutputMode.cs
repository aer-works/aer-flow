namespace Aer.Workers.Dialogue;

/// <summary>
/// What <see cref="DialogueRunner"/> writes to <see cref="DialogueWorkerConfig.FinalOutputName"/>
/// once the exchange ends (#736, field note 7 on #665). A dialogue's real value sometimes lives in
/// the exchange itself — a critic's objections and the resolutions that followed — not only in
/// whoever spoke last; this lets a config declare which one is the worker's actual product.
/// </summary>
public enum FinalOutputMode
{
    /// <summary>
    /// The declared final output is the last turn's text alone — this worker's original and still
    /// default behavior (M17 Phase 3, #166). What a config gets when <c>FinalOutputMode</c> is absent.
    /// </summary>
    FinalTurn,

    /// <summary>
    /// The declared final output is the full role-attributed exchange, every turn in order, each
    /// prefixed with its speaker's role — the same "Role: Text" line shape
    /// <see cref="DialogueRunner"/> already threads into each next turn's prompt, reused rather than
    /// rendered a second way.
    /// </summary>
    Transcript,
}
