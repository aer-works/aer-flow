namespace Aer.Cli;

/// <summary>
/// Parsed arguments for <c>aer dispatch &lt;role&gt;</c> (#900, front-door rung 2): run a single
/// worker role from the catalog against a task spec, its outputs contract-checked by the same pump
/// <c>aer run</c> drives.
/// </summary>
/// <param name="RoleId">The catalog role to dispatch (e.g. <c>review</c>), resolved via <see cref="Aer.Adapters.WorkerRoleCatalog.For"/>.</param>
/// <param name="SpecFilePath">The file whose contents are the task prompt — what this role is asked to do.</param>
/// <param name="TaskDirectoryPath">
/// Where this dispatch's durable state lives. Defaults to a fresh, uniquely-named directory per
/// invocation (see <see cref="DispatchOptionsParser"/>) so a repeated self-dispatch runs anew rather
/// than resuming — and so replaying a prior terminal snapshot — the way an orchestrator (#778) issues
/// the same role many times. Pass an explicit value to resume a specific interrupted dispatch.
/// </param>
/// <param name="Adapter">
/// A vendor adapter to run the role on instead of its tier default — the escape hatch. Null keeps the
/// role's own tier-resolved adapter.
/// </param>
/// <param name="WorkflowId">A label forwarded to the run; defaults to the materialized template id.</param>
public sealed record DispatchOptions(
    string RoleId,
    string SpecFilePath,
    string TaskDirectoryPath,
    string? Adapter = null,
    string? WorkflowId = null);
