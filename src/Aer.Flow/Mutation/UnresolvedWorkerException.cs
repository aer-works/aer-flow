namespace Aer.Flow.Mutation;

/// <summary>
/// Raised when a ready step's <c>Worker</c> role name has no corresponding <see cref="WorkerBinding"/>
/// among those supplied to <see cref="MutationInterface.StartWorkflowAsync"/>. Distinct from a
/// <c>WorkflowDefinition</c> validation error — the template itself is well-formed (spec §11.1
/// says nothing about which workers exist), this is a caller configuration gap discovered only at
/// dispatch time.
/// </summary>
public sealed class UnresolvedWorkerException : AerFlowException
{
    public UnresolvedWorkerException(string message)
        : base(message)
    {
    }

    public UnresolvedWorkerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
