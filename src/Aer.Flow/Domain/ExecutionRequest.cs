namespace Aer.Flow.Domain;

/// <summary>
/// The immutable unit of execution, shared by identity across Flow's and Core's halves of the
/// Event Store (spec §3). Immutable once emitted — never mutated, never reused; a retry is a
/// brand-new <see cref="ExecutionRequest"/> with a brand-new <see cref="ExecutionId"/> (§10).
/// </summary>
/// <param name="UpstreamExecutionIds">
/// For each <see cref="StepId"/> this step depends on, exactly which of that dependency's
/// <see cref="ExecutionId"/>s this request's <paramref name="Inputs"/> were derived from. This is
/// what makes staleness (§11.3, §17.5) derivable purely by reading the log.
/// </param>
public sealed record ExecutionRequest(
    ExecutionId ExecutionId,
    WorkflowId WorkflowId,
    StepId StepId,
    string Worker,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    TimeSpan Timeout,
    IReadOnlyList<EnvironmentVariable> Environment,
    IReadOnlyDictionary<StepId, ExecutionId> UpstreamExecutionIds);
