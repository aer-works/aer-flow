using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters.Tests.TestSupport;

/// <summary>
/// Stands in for a real vendor adapter (M11 Phase 1 excludes the Claude adapter, #85) — asserts the
/// canonical <see cref="WorkerInvocation"/>/<see cref="WorkerContract"/> → <see cref="CoreDispatchTarget"/>
/// mapping without a real vendor or live process, per the phase's stated deliverable. Deterministic:
/// echoes every field it received onto the command line so a test can assert on them directly,
/// instead of hiding them behind vendor-specific flag/shell mechanics.
/// </summary>
internal sealed class FakeEchoWorkerAdapter : IWorkerAdapter
{
    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract) => new(
        "echo",
        [
            invocation.PromptTemplate,
            invocation.Model ?? "(no-model)",
            invocation.PermissionScope ?? "(no-permission-scope)",
            contract.WorkerName,
            .. contract.RequiredInputs,
            .. contract.ProducedOutputs.Select(o => o.Name),
        ],
        invocation.WorkingDirectory);
}
