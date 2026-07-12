namespace Aer.Cli;

/// <summary>
/// Parsed arguments for <c>aer cancel</c> (M12 Phase 2, §9's on-demand cancellation surface exposed
/// on the CLI).
/// </summary>
/// <param name="TaskDirectoryPath">
/// An already-started task's durable state directory — <c>aer cancel</c> never binds a fresh
/// snapshot the way <c>aer run</c> does (§11.2's "mutation commands never bind fresh" rule).
/// </param>
/// <param name="ExecutionId">The target execution's <c>ExecutionId</c> to request cancellation for.</param>
/// <param name="BindingsFilePath">The worker-binding config file (M11 Phase 1's sidecar shape).</param>
/// <param name="WorkflowId">
/// Defaults to the bound snapshot's <c>WorkflowTemplateId</c> when not given, same as <c>aer run</c>.
/// </param>
public sealed record CancelOptions(
    string TaskDirectoryPath,
    string ExecutionId,
    string BindingsFilePath,
    string? WorkflowId = null);
