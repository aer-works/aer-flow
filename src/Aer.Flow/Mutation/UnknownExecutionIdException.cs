namespace Aer.Flow.Mutation;

/// <summary>
/// Raised when <c>MutationInterface.RequestCancellationAsync</c>'s target
/// <see cref="Domain.ExecutionId"/> was never admitted — no
/// <see cref="Domain.FlowEvent.ExecutionRequestAccepted"/> for it exists anywhere in the log (spec
/// §9). A <em>known but already-terminal</em> target is not this — it is the too-late no-op §9 step
/// 4 describes. Rejected, never silently widened: nothing is appended to the log when this is
/// thrown.
/// </summary>
public sealed class UnknownExecutionIdException : AerFlowException
{
    public UnknownExecutionIdException(string message)
        : base(message)
    {
    }
}
