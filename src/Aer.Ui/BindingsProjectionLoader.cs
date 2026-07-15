using Aer.Adapters;

namespace Aer.Ui;

/// <summary>
/// Opens a worker-bindings config file directly for the bindings editor (M16 Phase 4, issue #153)
/// — the bindings counterpart to <see cref="TemplateProjectionLoader"/>. Reuses
/// <see cref="WorkerBindingConfigParser.LoadFromFileAsync"/> exactly as every other consumer does —
/// never a second parser — the same "consume the read model as a library" seam
/// <see cref="TaskProjectionLoader"/> and <see cref="TemplateProjectionLoader"/> already established.
/// <para>
/// Only wraps the missing-file case: <see cref="WorkerBindingConfigParser.LoadFromFileAsync"/> reads
/// the file with a plain <see cref="File.ReadAllTextAsync(string, CancellationToken)"/> call, which
/// throws an un-typed <see cref="FileNotFoundException"/> rather than an
/// <see cref="Aer.Flow.AerFlowException"/> — this loader raises
/// <see cref="WorkerBindingConfigException"/> instead, so <c>MainWindow</c>'s bindings-editor open
/// path can catch <see cref="Aer.Flow.AerFlowException"/> uniformly, exactly like
/// <see cref="TemplateProjectionLoader.LoadAsync"/> already does for a missing template file.
/// </para>
/// </summary>
public static class BindingsProjectionLoader
{
    /// <exception cref="WorkerBindingConfigException">
    /// <paramref name="bindingsFilePath"/> does not exist, or its contents fail to parse or
    /// structurally validate as a worker-binding config.
    /// </exception>
    public static async Task<IReadOnlyDictionary<string, WorkerBindingConfigEntry>> LoadAsync(
        string bindingsFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bindingsFilePath);

        if (!File.Exists(bindingsFilePath))
        {
            throw new WorkerBindingConfigException($"No such worker-bindings file: '{bindingsFilePath}'");
        }

        return await WorkerBindingConfigParser.LoadFromFileAsync(bindingsFilePath, cancellationToken)
            .ConfigureAwait(false);
    }
}
