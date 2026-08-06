using Aer.Flow;

namespace Aer.Adapters;

/// <summary>
/// Raised by <see cref="WorkerBindingResolver.Resolve"/> when a <see cref="WorkerBindingConfigEntry.Adapter"/>
/// name has no corresponding entry in the supplied adapter registry — the config named an adapter
/// that was never registered (e.g. a typo, or a vendor not yet built). Mirrors
/// <c>Aer.Flow.Mutation.UnresolvedWorkerException</c>'s role one layer up, for worker roles with no
/// registered binding at all.
/// </summary>
public sealed class UnknownWorkerAdapterException : AerFlowException
{
    public string AdapterName { get; }

    public UnknownWorkerAdapterException(string adapterName)
        : base(string.Equals(adapterName, "gemini", StringComparison.OrdinalIgnoreCase)
            ? $"Unknown adapter '{adapterName}' — it was renamed to 'agy'."
            : $"No IWorkerAdapter registered for adapter name '{adapterName}'.")
    {
        AdapterName = adapterName;
    }
}
