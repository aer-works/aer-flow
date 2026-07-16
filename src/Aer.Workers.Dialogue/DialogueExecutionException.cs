namespace Aer.Workers.Dialogue;

/// <summary>
/// Raised when a turn in the exchange fails in a way the outer engine must see as an ordinary
/// failed execution (M17 Phase 3, #166): a participant's vendor CLI exits non-zero, or produces no
/// text at all for a turn. <see cref="Program"/> maps this to a non-zero process exit and — because
/// the throw happens before <see cref="DialogueRunner"/> ever reaches its final-output write — the
/// declared <see cref="DialogueWorkerConfig.FinalOutputName"/> is never written either. Flow's
/// <c>OutcomeClassifier</c>/<c>ContractValidator</c> (spec §8) therefore classify a broken dialogue
/// exactly like any other failed worker on both counts at once — non-zero exit *and* a missing
/// declared output — deliberately redundant, not either-or, so the failure is unambiguous however a
/// caller happens to check it.
/// <para>
/// Per §18.2's stated tradeoff, this worker makes no attempt to work around it: whatever
/// <c>transcript.jsonl</c> lines were already appended for turns that succeeded *before* the failing
/// one stay on disk as a forensic record for a human to read, but the exchange itself has no
/// resumable state — a retry (via the step's ordinary <c>RetryPolicy</c>, spec §10) restarts the
/// whole exchange from turn one, exactly like any other worker's retry.
/// </para>
/// <para>
/// This worker owns no key-handling code and depends on nothing above the worker boundary (CLAUDE.md's
/// Adapter Isolation rule, applied inward), so this is its own exception root — like
/// <see cref="DialogueWorkerConfigException"/> — rather than <c>Aer.Flow.AerFlowException</c>.
/// </para>
/// </summary>
public sealed class DialogueExecutionException : Exception
{
    public DialogueExecutionException(string message)
        : base(message)
    {
    }
}
