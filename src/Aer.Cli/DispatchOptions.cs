namespace Aer.Cli;

/// <summary>
/// Parsed arguments for the <c>aer dispatch</c> command — just the inputs. <see cref="DispatchCommand"/>
/// resolves <paramref name="Name"/> against the catalogs (one namespace) and drives the result through
/// the same pump <c>aer run</c> uses; what a role vs a template means, and why, lives there.
/// </summary>
/// <param name="Name">The catalog role or workflow template to dispatch (e.g. <c>review</c>), resolved by <see cref="DispatchCommand"/>.</param>
/// <param name="SpecFilePath">
/// The file whose contents are the task prompt — what a <em>role</em> is asked to do. Required for a
/// role, and rejected for a template (a template's phases already carry their instructions). Null when
/// not supplied.
/// </param>
/// <param name="RoomDirectoryPath">
/// Where this dispatch's durable state lives. Defaults to a fresh, uniquely-named directory per
/// invocation (see <see cref="DispatchOptionsParser"/>) so a repeated self-dispatch runs anew rather
/// than resuming — and so replaying a prior terminal snapshot — the way an orchestrator (#778) issues
/// the same name many times. Pass an explicit value to resume a specific interrupted dispatch.
/// </param>
/// <param name="Adapter">
/// A vendor adapter to run every role/phase on instead of its tier default — the escape hatch. Null
/// keeps each role's own tier-resolved adapter.
/// </param>
/// <param name="WorkflowId">A label forwarded to the run; defaults to the materialized template id.</param>
public sealed record DispatchOptions(
    string Name,
    string? SpecFilePath,
    string RoomDirectoryPath,
    string? Adapter = null,
    string? WorkflowId = null);
