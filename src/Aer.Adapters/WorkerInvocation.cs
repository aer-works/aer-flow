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
/// The raw, hand-typed permission grant to pre-authorize (e.g. Claude's <c>--allowedTools</c>
/// value) — each vendor's flag and vocabulary differs (spike #21), which is exactly why this is an
/// opaque string the adapter alone interprets, never a shared enum Aer.Flow or this record would
/// have to version. Superseded by <paramref name="PermissionGrant"/> when both are set (M21 Phase
/// 1) — kept only as the bindings editor's "Advanced" escape hatch for vendor vocabulary the
/// structured model can't yet express.
/// </param>
/// <param name="PermissionGrant">
/// The structured, vendor-neutral permission grant (M21 Phase 1) — the bindings editor's builder-UI
/// primary path. When set, an <see cref="IPermissionGrantTranslator"/>-implementing adapter's
/// <c>Resolve</c> translates it into the vendor-native flag value itself, ignoring
/// <paramref name="PermissionScope"/> entirely (<see cref="PermissionGrant"/>'s own docs record this
/// precedence). Null means "no structured grant configured" — the same "fall through to the
/// adapter's own default" behavior a null <paramref name="PermissionScope"/> already has.
/// </param>
/// <param name="WorkingDirectory">
/// The real, already-resolved absolute directory the spawned process should run in (M23 Phase 3,
/// #272) — resolved by <see cref="WorkerBindingResolver.Resolve"/> from
/// <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> (a rooted path used directly, or a
/// per-machine profile name looked up in the local, never-portable profile mapping) before this
/// record is ever constructed, so every adapter receives the same real path regardless of which
/// machine or profile named it. Null keeps the prior default (no explicit cwd — AER's own scratch
/// artifacts folder). Every <see cref="IWorkerAdapter"/> forwards this into the
/// <see cref="Aer.Flow.Dispatch.CoreDispatchTarget"/> it builds unchanged — it carries no
/// vendor-specific meaning, unlike <paramref name="PromptTemplate"/>.
/// </param>
/// <param name="BindingsFileDirectory">
/// The directory the worker-bindings config file this invocation was resolved from lives in, if
/// known (M23 Phase 3, #272) — plain context, not an instruction: most adapters ignore it entirely
/// (<paramref name="PromptTemplate"/> is prose to them). <see cref="DialogueWorkerAdapter"/> is the
/// one adapter that repurposes <paramref name="PromptTemplate"/> as a file path (its config
/// sidecar's), so it alone resolves a non-rooted <paramref name="PromptTemplate"/> against this
/// directory — the fix for the sidecar-path portability bug a bindings file copied to a new machine
/// (or a different directory on the same one) otherwise hits.
/// </param>
/// <param name="SessionId">
/// The native vendor session identifier or session Guid for interactive sessions (M24 Phase 1).
/// </param>
/// <param name="ResumeSession">
/// <see langword="true"/> to resume an existing native session (Claude <c>--resume</c>, Gemini <c>--conversation</c>);
/// <see langword="false"/> to initialize a new native session (Claude <c>--session-id</c>).
/// </param>
/// <param name="MinimalOverhead">
/// <see langword="true"/> to enable minimal-overhead dispatch (e.g. Claude <c>--bare</c>).
/// </param>
/// <param name="StreamJson">
/// <see langword="true"/> to emit real-time stream-json output for live in-turn progress streaming (Claude <c>--output-format stream-json</c>).
/// </param>
/// <param name="LogFilePath">
/// The path to a log file where the vendor CLI writes side-channel logs (e.g. Gemini <c>--log-file</c> for capturing conversation id).
/// </param>
public sealed record WorkerInvocation(
    string PromptTemplate,
    string? Model = null,
    string? PermissionScope = null,
    PermissionGrant? PermissionGrant = null,
    string? WorkingDirectory = null,
    string? BindingsFileDirectory = null,
    string? SessionId = null,
    bool ResumeSession = false,
    bool MinimalOverhead = false,
    bool StreamJson = false,
    string? LogFilePath = null);

