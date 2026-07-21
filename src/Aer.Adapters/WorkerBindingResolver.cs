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
    /// <param name="config">The parsed worker-binding config to resolve.</param>
    /// <param name="adapters">The registered adapters each entry's <see cref="WorkerBindingConfigEntry.Adapter"/> looks up through.</param>
    /// <param name="profiles">
    /// The local per-machine profile mapping (M23 Phase 3, #272; see <see cref="AerProfileStore"/>),
    /// consulted only for an entry whose <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> is a
    /// non-rooted profile name rather than a literal path. Null (the default) behaves exactly like an
    /// empty map — every entry naming a profile then throws <see cref="UnknownWorkingDirectoryProfileException"/>,
    /// while an entry with no <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> at all, or a
    /// rooted one, is entirely unaffected.
    /// </param>
    /// <param name="bindingsFileDirectory">
    /// The directory <paramref name="config"/> was loaded from, if known (M23 Phase 3, #272) —
    /// forwarded verbatim into every resolved <see cref="WorkerInvocation.BindingsFileDirectory"/>.
    /// Only <see cref="DialogueWorkerAdapter"/> uses it (to resolve its config sidecar's path
    /// portably); every other adapter ignores it.
    /// </param>
    /// <param name="onWorkerStdoutLine">
    /// M24 Phase 1's live in-turn streaming seam: when supplied, every resolved
    /// <see cref="CoreDispatchTarget"/> gets this wrapped as its <see cref="CoreDispatchTarget.OnStdoutLine"/>,
    /// called with the worker's name and each raw stdout line as its dispatch runs live. Null (the
    /// default) for every caller that has no live consumer for that — <c>aer run</c>/<c>aer decide</c>
    /// from the CLI, any non-interactive workflow — since capturing output at all has a real cost
    /// (<see cref="Aer.Flow.Dispatch.CoreDispatcher"/> only turns on stdout capture when
    /// <c>OnStdoutLine</c> is non-null). What this callback actually does with a line — parse it,
    /// broadcast it — is entirely the caller's concern; this seam only ever forwards raw text.
    /// </param>
    /// <exception cref="UnknownWorkerAdapterException">
    /// An entry names an <see cref="WorkerBindingConfigEntry.Adapter"/> not present in <paramref name="adapters"/>.
    /// </exception>
    /// <exception cref="UnknownWorkingDirectoryProfileException">
    /// An entry's <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> names a profile with no
    /// entry in <paramref name="profiles"/>.
    /// </exception>
    public static IReadOnlyDictionary<string, WorkerBinding> Resolve(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> config,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        IReadOnlyDictionary<string, string>? profiles = null,
        string? bindingsFileDirectory = null,
        Action<string, string>? onWorkerStdoutLine = null)
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

            var workingDirectory = ResolveWorkingDirectory(workerName, entry.WorkingDirectory, profiles);
            var invocation = new WorkerInvocation(
                entry.PromptTemplate, entry.Model, entry.PermissionScope, entry.PermissionGrant,
                workingDirectory, bindingsFileDirectory, entry.SessionId, entry.ResumeSession,
                entry.MinimalOverhead, entry.StreamJson, entry.LogFilePath);
            var target = adapter.Resolve(invocation, entry.Contract);

            if (onWorkerStdoutLine is not null)
            {
                var capturedWorkerName = workerName;
                target = target with { OnStdoutLine = line => onWorkerStdoutLine(capturedWorkerName, line) };
            }

            bindings[workerName] = new WorkerBinding.Process(entry.Contract, target, entry.Timeout);
        }

        return bindings;
    }

    /// <summary>
    /// A rooted path passes through unchanged; a non-rooted one is a profile name, looked up in
    /// <paramref name="profiles"/> — the "portable bindings via per-machine profile mapping"
    /// mechanism (M23 Phase 3, #272). Null stays null: most entries never set a
    /// <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> at all.
    /// </summary>
    private static string? ResolveWorkingDirectory(
        string workerName, string? workingDirectory, IReadOnlyDictionary<string, string>? profiles)
    {
        if (workingDirectory is null)
        {
            return null;
        }

        if (Path.IsPathRooted(workingDirectory))
        {
            return workingDirectory;
        }

        if (profiles is null || !profiles.TryGetValue(workingDirectory, out var resolved))
        {
            throw new UnknownWorkingDirectoryProfileException(workerName, workingDirectory);
        }

        return resolved;
    }
}
