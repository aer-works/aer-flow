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
/// <para>
/// <b>Not a recorded outcome, and deliberately not made into one.</b> Like every other pre-spawn
/// failure this propagates out of <c>CoreDispatcher.DispatchAsync</c> instead of becoming an
/// <c>ExecutionFailed</c> event, because throwing before the spawn lands in the state spec §7
/// explicitly names as the safe one: intent durably recorded, no execution trace, nothing ever ran.
/// That is <i>not</i> §7's orphan, which requires an <c>ExecutionStarted</c> with no matching
/// <c>ExecutionExited</c> — and aer-core emits no event at all when a spawn fails
/// (<c>task.rs</c>: "if spawning fails, <c>on_event</c> is never called"), so no
/// <c>ExecutionStarted</c> exists to orphan. <c>MutationInterface.DispatchAndRecordOutcomeAsync</c>'s
/// <c>finally</c> still unregisters the execution, so nothing leaks in memory either.
/// </para>
/// <para>
/// Manufacturing a terminal outcome here would therefore be a regression rather than a fix: it would
/// convert a state the spec calls recoverable-by-re-submission into a permanent
/// <c>ExecutionFailed</c>, and it would do so for over-long command lines only, leaving every other
/// spawn error on the original path while making the handling look uniform.
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
