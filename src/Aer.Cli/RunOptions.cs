namespace Aer.Cli;

/// <summary>
/// Parsed arguments for <c>aer run</c> (M11 Phase 3, §21's "the CLI is the pump").
/// </summary>
/// <param name="WorkflowFilePath">The <c>WorkflowDefinition</c> template file (spec §11.1).</param>
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
    string WorkflowFilePath,
    string BindingsFilePath,
    string TaskDirectoryPath,
    string? WorkflowId = null);
