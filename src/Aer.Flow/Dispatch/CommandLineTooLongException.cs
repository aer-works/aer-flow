namespace Aer.Flow.Dispatch;

/// <summary>
/// Raised when the command line a dispatch would assemble is longer than the host OS will accept,
/// caught by <see cref="CoreDispatcher"/> before it ever reaches aer-core (#598). Both worker
/// adapters embed the whole prompt inline as a single argument
/// (<c>GeminiWorkerAdapter</c>'s <c>["-p", prompt]</c>, <c>ClaudeWorkerAdapter</c>'s <c>"-p", prompt</c>),
/// so a long enough prompt hits a limit that has nothing to do with the prompt being wrong.
/// <para>
/// This exists to name the failure. Without it the spawn is attempted and fails inside aer-core,
/// which maps every spawn error alike to <c>AerError::SpawnFailed</c> — surfacing to the operator as
/// an OS-authored message about a filename being too long, naming neither the prompt, its size, nor
/// the limit it crossed. <c>Aer.Cli</c>'s top-level <c>catch (AerFlowException)</c> renders this one
/// as an ordinary AER error instead.
/// </para>
/// </para>
/// <para>
/// Caught in <c>MutationInterface.DispatchAndRecordOutcomeAsync</c> and recorded as an
/// <c>ExecutionFailed</c> event carrying the refusal message with <c>FailureClassification.Permanent</c>,
/// preventing <c>flow.jsonl</c> from being left stuck at <c>ExecutionRequestAccepted</c> (#747).
/// </para>
/// </summary>
public sealed class CommandLineTooLongException : AerFlowException
{
    public CommandLineTooLongException(string message)
        : base(message)
    {
    }

    public CommandLineTooLongException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
