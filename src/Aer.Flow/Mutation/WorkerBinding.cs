using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Flow.Mutation;

/// <summary>
/// Resolves a <see cref="WorkflowStepDefinition.Worker"/> role name (e.g. <c>"architect"</c>) to
/// what the <c>MutationInterface</c> needs to dispatch and classify it: the concrete binary to
/// spawn, the <see cref="WorkerContract"/> to classify its outcome against, and how long a single
/// execution may run. Spec §4 leaves this resolution to "configuration external to the workflow" —
/// <c>Aer.Adapters</c> doesn't exist yet (no milestone), so callers supply it directly for M7.
/// </summary>
public sealed record WorkerBinding(WorkerContract Contract, CoreDispatchTarget Target, TimeSpan Timeout);
