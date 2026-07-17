namespace Aer.Adapters;

/// <summary>
/// The production adapter registry <c>Aer.Cli</c> wires <see cref="WorkerBindingResolver.Resolve"/>
/// with (M11 Phase 3) — the concrete answer to Phase 1's deferred "no adapter-registration
/// mechanism was built" note, now that a real caller (the CLI) needs one. Three workers registered so
/// far, each keyed by the capability name rather than a binary name (<c>"claude"</c> invokes the
/// <c>claude</c> binary directly; <c>"gemini"</c> invokes the <c>agy</c> binary; <c>"dialogue"</c>
/// invokes <c>Aer.Workers.Dialogue</c>, itself a two-vendor Case 2 worker, not a vendor CLI at all
/// (M17 Phase 4, #167) — the registry key names the capability you're dispatching to, not what you
/// type to reach it).
/// </summary>
public static class WorkerAdapterRegistry
{
    public static IReadOnlyDictionary<string, IWorkerAdapter> Default { get; } = new Dictionary<string, IWorkerAdapter>
    {
        ["claude"] = new ClaudeWorkerAdapter(),
        ["gemini"] = new GeminiWorkerAdapter(),
        ["dialogue"] = new DialogueWorkerAdapter(),
    };
}
