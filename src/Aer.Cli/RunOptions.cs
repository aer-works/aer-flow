namespace Aer.Cli;

/// <summary>
/// Parsed arguments for <c>aer run</c> (M11 Phase 3, §21's "the CLI is the pump").
/// </summary>
/// <param name="WorkflowFilePath">
/// The <c>WorkflowDefinition</c> template file (spec §11.1). <b>Bound</b> from only when
/// <paramref name="TaskDirectoryPath"/> has no persisted snapshot yet — a fresh start. So
/// <c>null</c> is valid for a resume-only call (M15 Phase 1, issue #137): the CLI still requires it
/// positionally (a terminal invocation names a workflow file whether fresh or resumed), but an
/// in-process caller resuming a known task directory has no reason to ask the user for one.
/// <para>
/// Resuming does still <b>read</b> it, to refuse a directory bound to a different template (#628) —
/// but only when it names a file that exists, because not every caller supplying this supplies a
/// path. The desktop writes the bound template's bare <em>id</em> here when a task directory has no
/// recorded <c>.aer/workflow-path</c>. This was previously the canonical statement that a resume
/// never reads this value at all, which is what made that safe and what made #628 invisible.
/// </para>
/// </param>
/// <param name="BindingsFilePath">The worker-binding config file (M11 Phase 1's sidecar shape).</param>
/// <param name="TaskDirectoryPath">
/// Where this task's durable state lives — <c>snapshot.json</c>, <c>flow.jsonl</c>, <c>artifacts/</c>,
/// <c>flow.lock</c>. Running <c>aer run</c> again against the same directory resumes it from the
/// log rather than starting over (§7, §21): a second invocation is how a laptop sleep or a closed
/// terminal is recovered from, not an error.
/// </param>
/// <param name="WorkflowId">
/// Defaults to the bound snapshot's <c>WorkflowTemplateId</c> when not given — just a label
/// (<c>ExecutionRequest.WorkflowId</c>, spec §3), not an identity a task's own directory doesn't
/// already carry.
/// </param>
public sealed record RunOptions(
    string? WorkflowFilePath,
    string BindingsFilePath,
    string TaskDirectoryPath,
    string? WorkflowId = null);
