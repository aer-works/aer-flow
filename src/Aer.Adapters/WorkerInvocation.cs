namespace Aer.Adapters;

/// <summary>
/// The vendor-neutral description of what a worker adapter must invoke a worker to do (CLAUDE.md
/// rule #2's canonical protocol, M11 Phase 1). Paired with the <see cref="Aer.Flow.Domain.WorkerContract"/>
/// a <see cref="IWorkerAdapter"/> resolves alongside it, which already carries the ordered input
/// role names and declared outputs — this record adds only what the contract doesn't: the
/// human-authored prompt and the vendor-facing invocation knobs.
/// <para>
/// Built once, when a worker-binding config entry is resolved into a <see cref="Aer.Flow.Mutation.WorkerBinding"/>
/// — not once per execution. The resulting <see cref="Aer.Flow.Dispatch.CoreDispatchTarget"/> is
/// reused by <c>Aer.Flow.Mutation.MutationInterface.StartWorkflowAsync</c> for every dispatch of
/// this worker role across the whole run, so nothing here may carry a resolved, execution-specific
/// file path. Per-execution dynamism (which files exist at <c>AER_INPUT_&lt;n&gt;</c>/
/// <c>AER_OUTPUT_DIR</c> right now) is carried entirely by the environment variables
/// <c>Aer.Flow.Artifacts.ArtifactManager</c> already resolves per dispatch (spec §16) — an adapter
/// references those variables by name (shell-expanded at dispatch time, e.g. via a shell-wrapped
/// <see cref="Aer.Flow.Dispatch.CoreDispatchTarget"/>), the same convention the shell-stub workers
/// already use.
/// </para>
/// </summary>
/// <param name="PromptTemplate">
/// The instructional text handed to the worker, authored per worker role in the worker-binding
/// config — what to do, not how to do it. How it references its inputs/output (env var name, cwd,
/// shell expansion, absolute-path interpolation) is entirely the adapter's concern; two vendors can
/// need different accommodations for the identical template (spike #21).
/// </param>
/// <param name="Model">The vendor model identifier to invoke, if the vendor takes one. Null when not applicable.</param>
/// <param name="PermissionScope">
/// The scoped permission grant to pre-authorize (e.g. Claude's <c>--allowedTools</c> value) — each
/// vendor's flag and vocabulary differs (spike #21), which is exactly why this is an opaque string
/// the adapter alone interprets, never a shared enum Aer.Flow or this record would have to version.
/// </param>
public sealed record WorkerInvocation(string PromptTemplate, string? Model = null, string? PermissionScope = null);
