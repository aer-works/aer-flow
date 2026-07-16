namespace Aer.Workers.Dialogue;

/// <summary>
/// Raised when a candidate dialogue-worker config fails to parse or fails structural validation:
/// malformed JSON, a missing required field, or a <c>{PROMPT}</c> placeholder missing from a
/// participant's <see cref="DialogueParticipant.Args"/>. This worker owns no key-handling code and
/// depends on nothing above the worker boundary (CLAUDE.md's Adapter Isolation rule, applied
/// inward), so this is its own exception root rather than <c>Aer.Flow.AerFlowException</c>.
/// </summary>
public sealed class DialogueWorkerConfigException : Exception
{
    public DialogueWorkerConfigException(string message)
        : base(message)
    {
    }

    public DialogueWorkerConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
