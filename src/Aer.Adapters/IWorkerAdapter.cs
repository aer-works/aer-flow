using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Adapters;

/// <summary>
/// Maps a <see cref="WorkerInvocation"/> and its paired <see cref="WorkerContract"/> to a
/// <see cref="CoreDispatchTarget"/> — the seam CLAUDE.md's Adapter Isolation rule requires. Every
/// vendor quirk (flag vocabulary, cwd handling, stdin redirection, shell-wrapping to reference
/// <c>AER_INPUT_&lt;n&gt;</c>/<c>AER_OUTPUT_DIR</c>) lives behind an implementation of this
/// interface; <c>Aer.Flow</c> never learns a vendor exists.
/// </summary>
public sealed record WorkerCapabilityItem(
    string Name,
    string Kind,
    string Description,
    string? ParameterHint = null);

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
    /// Discovers the capabilities (skills, commands, models) supported by this vendor (M24 Phase 2).
    /// </summary>
    WorkerCapabilities DiscoverCapabilities(string? workingDirectory = null) =>
        new("unknown", Array.Empty<WorkerCapabilityItem>(), Array.Empty<string>());
}
