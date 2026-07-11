using System.Text.Json.Serialization;

namespace Aer.Flow.Domain;

/// <summary>
/// Core-originated lifecycle events (spec §5.1's <c>events.jsonl</c> half), recorded into the same
/// combined log Flow uses for its own events (M7 Phase 6's dual-log ownership decision — spec §5
/// permits a single storage backend as long as per-event-type ownership still holds) but kept a
/// wholly separate type from <see cref="FlowEvent"/>. Only the Core Dispatcher writes these — it
/// mirrors the <c>AerTask.EventRaised</c> callbacks from the aer-core M5 binding it P/Invokes into
/// (see <c>CLAUDE.md</c>'s P/Invoke Layer rule) — never any Flow-side mutation logic, and vice
/// versa. Deliberately minimal for M7: only the two lifecycle events this phase's acceptance
/// criteria need (<c>StdoutChunk</c>/<c>StderrChunk</c> capture is unused since M7 dispatches
/// without <c>WithCaptureOutput</c>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "eventType")]
[JsonDerivedType(typeof(ExecutionStarted), "executionStarted")]
[JsonDerivedType(typeof(ExecutionExited), "executionExited")]
public abstract record CoreEvent
{
    private CoreEvent()
    {
    }

    /// <summary>The Core-managed process for this execution has started.</summary>
    public sealed record ExecutionStarted(ExecutionId ExecutionId, uint Pid) : CoreEvent;

    /// <summary>The Core-managed process for this execution has exited.</summary>
    public sealed record ExecutionExited(ExecutionId ExecutionId, int ExitCode, CoreExitReason Reason) : CoreEvent;
}

/// <summary>
/// Mirrors aer-core M5's <c>AerExitReason</c> at Flow's event-log boundary, rather than reusing the
/// binding's enum directly — this is the boundary spec §8's failure model (<c>NaturalExit</c> |
/// <c>TimedOut</c> | <c>CancelRequested</c>) is defined against, and it must serialize stably in
/// <c>flow.jsonl</c> independent of however aer-core's own ABI numbering evolves.
/// </summary>
public enum CoreExitReason
{
    Natural,
    TimedOut,
    CancelRequested,
}
