using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters;

/// <summary>
/// One thing a vendor CLI supports — a skill, a slash command, or a mode — surfaced by
/// <see cref="IWorkerAdapter.DiscoverCapabilitiesAsync"/> (M24 Phase 2) so a user can invoke it
/// instead of only typing plain prose.
/// </summary>
public sealed record WorkerCapabilityItem(
    string Name,
    string Kind,
    string Description,
    string? ParameterHint = null);

/// <summary>
/// The full set of skills/commands/modes and selectable models a vendor CLI reports for a given
/// (optional) working directory, as returned by <see cref="IWorkerAdapter.DiscoverCapabilitiesAsync"/>.
/// </summary>
public sealed record WorkerCapabilities(
    string Vendor,
    IReadOnlyList<WorkerCapabilityItem> Items,
    IReadOnlyList<string> Models);

/// <summary>
/// Maps a <see cref="WorkerInvocation"/> and its paired <see cref="WorkerContract"/> to a
/// <see cref="CoreDispatchTarget"/> — the seam CLAUDE.md's Adapter Isolation rule requires. Every
/// vendor quirk (flag vocabulary, cwd handling, stdin redirection, shell-wrapping to reference
/// <c>AER_INPUT_&lt;n&gt;</c>/<c>AER_OUTPUT_DIR</c>) lives behind an implementation of this
/// interface; <c>Aer.Flow</c> never learns a vendor exists.
/// </summary>
public interface IWorkerAdapter
{
    /// <summary>
    /// Resolves <paramref name="invocation"/> and <paramref name="contract"/> into the concrete
    /// command <c>Aer.Flow.Dispatch.CoreDispatcher</c> spawns. Called once per worker-binding config
    /// entry, not per execution — see <see cref="WorkerInvocation"/>'s remarks for why the result
    /// must not embed a resolved, execution-specific file path.
    /// </summary>
    CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract);

    /// <summary>
    /// Discovers the capabilities (skills, commands, models) this vendor's CLI actually supports
    /// (M24 Phase 2). Implementations that need to shell out to the CLI itself (e.g. Gemini's
    /// <c>agy models</c>) must do so here, not on the caller's thread — this is async precisely so
    /// an ASP.NET request thread never blocks on process I/O.
    /// </summary>
    Task<WorkerCapabilities> DiscoverCapabilitiesAsync(string? workingDirectory = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new WorkerCapabilities("unknown", Array.Empty<WorkerCapabilityItem>(), Array.Empty<string>()));
}
