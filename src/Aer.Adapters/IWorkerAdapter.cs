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
/// One canonical, vendor-agnostic fact about an in-flight turn, recovered from a raw stdout line by
/// <see cref="IWorkerAdapter.TryParseProgressEvent"/> (M24 Phase 1's live in-turn streaming). This is
/// the seam Adapter Isolation actually requires here: a vendor's streaming JSON envelope (e.g.
/// Claude's <c>stream-json</c> shape) is interpreted once, inside the adapter that understands it,
/// and everything downstream (the daemon's WebSocket push, a future UI) only ever sees this shape.
/// </summary>
/// <param name="Kind">
/// <c>"status"</c> (a lifecycle marker — session started, waiting on the vendor), <c>"text"</c> (a
/// chunk of the assistant's reply, complete or partial per <paramref name="IsPartial"/>), or
/// <c>"tool"</c> (the assistant is invoking a tool — <paramref name="Text"/> names it). Deliberately
/// a small closed-ish vocabulary a UI can switch on, not the vendor's own raw event-type string.
/// </param>
/// <param name="Text">Human-readable text for this event — the delta/message text for <c>"text"</c>, the tool name for <c>"tool"</c>, or a short status label. Never null.</param>
/// <param name="IsPartial">
/// True for a token-level delta that will be followed by more text in the same turn (Claude's
/// <c>--include-partial-messages</c> stream events); false for a complete, already-whole unit (a
/// full assistant message block, a status marker). A renderer appends partial text in place and
/// starts a new line on a non-partial one.
/// </param>
public sealed record WorkerProgressEvent(string Kind, string Text, bool IsPartial = false);

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

    /// <summary>
    /// Attempts to interpret one raw stdout line from a live dispatch as a <see cref="WorkerProgressEvent"/>
    /// (M24 Phase 1's live in-turn streaming) — only ever called for a line captured via the
    /// <see cref="CoreDispatchTarget.OnStdoutLine"/> seam, itself only wired up when the invocation
    /// requested a structured streaming output format. An adapter with no such format (or a line that
    /// doesn't match the expected shape — a stray log line, a partial JSON fragment split across a
    /// buffer boundary) returns false: the default here, since most adapters never produce anything
    /// to parse in the first place.
    /// </summary>
    bool TryParseProgressEvent(string rawLine, out WorkerProgressEvent? progressEvent)
    {
        progressEvent = null;
        return false;
    }
}
