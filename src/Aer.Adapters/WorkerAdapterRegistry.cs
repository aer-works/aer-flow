namespace Aer.Adapters;

/// <summary>
/// The production adapter registry <c>Aer.Cli</c> wires <see cref="WorkerBindingResolver.Resolve"/>
/// with (M11 Phase 3) — the concrete answer to Phase 1's deferred "no adapter-registration
/// mechanism was built" note, now that a real caller (the CLI) needs one. One vendor registered so
/// far: <c>"claude"</c> (Phase 2); the Gemini/<c>agy</c> adapter (M12) registers here the same way.
/// </summary>
public static class WorkerAdapterRegistry
{
    public static IReadOnlyDictionary<string, IWorkerAdapter> Default { get; } = new Dictionary<string, IWorkerAdapter>
    {
        ["claude"] = new ClaudeWorkerAdapter(),
    };
}
