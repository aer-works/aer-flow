namespace Aer.Flow.Templates;

/// <summary>
/// Raised when a persisted <see cref="Domain.WorkflowDefinitionSnapshot"/> file fails to parse:
/// malformed JSON or an empty document. Mirrors <see cref="WorkflowDefinitionValidationException"/>'s
/// role for the frozen-snapshot half of a task's on-disk state, read back by
/// <see cref="SnapshotBinder.LoadFromFileAsync"/> when a resumed <c>aer run</c> finds a task
/// directory already bound to one.
/// </summary>
public sealed class SnapshotLoadException : AerFlowException
{
    public SnapshotLoadException(string message)
        : base(message)
    {
    }

    public SnapshotLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
