using System.Collections;
using Aer.Flow.Mutation;

namespace Aer.Adapters;

/// <summary>
/// Backs <see cref="WorkerBindingResolver.ResolveLazily"/>: resolves (and refuses) each config entry
/// only the first time its worker name is looked up, never merely because the entry is present in the
/// file (#662). Every consumer in <c>Aer.Flow</c> — <c>MutationInterface</c>, the outcome detectors —
/// only ever calls <see cref="TryGetValue"/> or the indexer for a specific, already-known worker name;
/// none enumerates the whole map, which is what makes deferring resolution to that lookup safe here.
/// </summary>
internal sealed class LazyWorkerBindings : IReadOnlyDictionary<string, WorkerBinding>
{
    private readonly IReadOnlyDictionary<string, WorkerBindingConfigEntry> _config;
    private readonly Dictionary<string, Lazy<WorkerBinding>> _resolved;

    public LazyWorkerBindings(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> config,
        Func<string, WorkerBindingConfigEntry, WorkerBinding> resolveEntry)
    {
        _config = config;
        _resolved = config.ToDictionary(
            kv => kv.Key,
            kv => new Lazy<WorkerBinding>(() => resolveEntry(kv.Key, kv.Value)));
    }

    public bool TryGetValue(string key, out WorkerBinding value)
    {
        if (_resolved.TryGetValue(key, out var lazy))
        {
            value = lazy.Value;
            return true;
        }

        value = null!;
        return false;
    }

    public WorkerBinding this[string key] =>
        TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

    public bool ContainsKey(string key) => _config.ContainsKey(key);

    public int Count => _config.Count;

    public IEnumerable<string> Keys => _config.Keys;

    // Forcing every entry's Lazy<T> here is the one path that reintroduces #662's eager refusal — but
    // only for a caller that enumerates the whole map rather than looking up a worker by name, which
    // no shipped consumer does (see class remarks).
    public IEnumerable<WorkerBinding> Values => _resolved.Values.Select(l => l.Value);

    public IEnumerator<KeyValuePair<string, WorkerBinding>> GetEnumerator()
    {
        foreach (var key in _config.Keys)
        {
            yield return new KeyValuePair<string, WorkerBinding>(key, _resolved[key].Value);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
