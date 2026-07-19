using Aer.Flow.Domain;

namespace Aer.Adapters;

/// <summary>
/// One worker role's entry in a worker-binding config file (M11 Phase 1's open question: "where
/// worker-binding config lives"). A workflow names abstract worker roles (e.g. <c>"architect"</c>);
/// this is the run-time sidecar mapping — worker name → {adapter, model, permission scope, prompt
/// template} — deliberately kept out of the frozen <see cref="WorkflowDefinitionSnapshot"/>, the
/// same way M7 Phase 7 kept a worker's <c>Timeout</c> off the step.
/// </summary>
/// <param name="Adapter">
/// The registered adapter name (e.g. <c>"claude"</c>) this entry resolves through — looked up in
/// the <see cref="IWorkerAdapter"/> registry <see cref="WorkerBindingResolver.Resolve"/> is given,
/// never hardcoded to a vendor here.
/// </param>
/// <param name="Contract">This worker role's <see cref="WorkerContract"/> — required inputs, declared outputs, optional metadata.</param>
/// <param name="PromptTemplate">Forwarded verbatim into the resolved <see cref="WorkerInvocation"/>.</param>
/// <param name="Timeout">The per-execution timeout carried on the resolved <c>Aer.Flow.Mutation.WorkerBinding.Process</c>.</param>
/// <param name="Model">Forwarded verbatim into the resolved <see cref="WorkerInvocation"/>.</param>
/// <param name="PermissionScope">Forwarded verbatim into the resolved <see cref="WorkerInvocation"/>.</param>
/// <param name="PermissionGrant">Forwarded verbatim into the resolved <see cref="WorkerInvocation"/> — see its docs for precedence over <paramref name="PermissionScope"/>.</param>
public sealed record WorkerBindingConfigEntry(
    string Adapter,
    WorkerContract Contract,
    string PromptTemplate,
    TimeSpan Timeout,
    string? Model = null,
    string? PermissionScope = null,
    PermissionGrant? PermissionGrant = null);
