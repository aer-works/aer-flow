using Aer.Flow.Mutation;

namespace Aer.Adapters;

/// <summary>
/// Turns a parsed worker-binding config into the <c>Aer.Flow.Mutation.WorkerBinding</c> dictionary
/// <c>MutationInterface.StartWorkflowAsync</c> needs — the "adapter resolution into WorkerBinding"
/// M11 Phase 1 names, kept out of <c>Aer.Flow</c> entirely per CLAUDE.md's Adapter Isolation rule.
/// Every entry resolves to <see cref="WorkerBinding.Process"/>: a worker-binding config describes
/// a real vendor invocation, never a non-process party (<c>Aer.Flow.Mutation.WorkerBinding.NonProcess</c>,
/// spec §17.3) — those are constructed directly by whatever caller needs one, same as before this
/// seam existed.
/// </summary>
public static class WorkerBindingResolver
{
    /// <exception cref="UnknownWorkerAdapterException">
    /// An entry names an <see cref="WorkerBindingConfigEntry.Adapter"/> not present in <paramref name="adapters"/>.
    /// </exception>
    public static IReadOnlyDictionary<string, WorkerBinding> Resolve(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> config,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(adapters);

        var bindings = new Dictionary<string, WorkerBinding>(config.Count);
        foreach (var (workerName, entry) in config)
        {
            if (!adapters.TryGetValue(entry.Adapter, out var adapter))
            {
                throw new UnknownWorkerAdapterException(entry.Adapter);
            }

            var invocation = new WorkerInvocation(entry.PromptTemplate, entry.Model, entry.PermissionScope, entry.PermissionGrant);
            var target = adapter.Resolve(invocation, entry.Contract);

            bindings[workerName] = new WorkerBinding.Process(entry.Contract, target, entry.Timeout);
        }

        return bindings;
    }
}
