namespace Aer.Adapters;

/// <summary>
/// The production adapter registry <c>Aer.Cli</c> wires <see cref="WorkerBindingResolver.Resolve"/>
/// with (M11 Phase 3) — the concrete answer to Phase 1's deferred "no adapter-registration
/// mechanism was built" note, now that a real caller (the CLI) needs one. Two vendors registered so
/// far, each keyed by vendor name rather than binary name (<c>"claude"</c> invokes the <c>claude</c>
/// binary directly; <c>"gemini"</c> invokes the <c>agy</c> binary — the registry key names who
/// you're talking to, not what you type to reach them).
/// </summary>
public static class WorkerAdapterRegistry
{
    public static IReadOnlyDictionary<string, IWorkerAdapter> Default { get; } = new Dictionary<string, IWorkerAdapter>
    {
        ["claude"] = new ClaudeWorkerAdapter(),
        ["gemini"] = new GeminiWorkerAdapter(),
    };
}
