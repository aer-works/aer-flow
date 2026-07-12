using System.Text.Json;

namespace Aer.Adapters;

/// <summary>
/// Loads a worker-binding config from a file (M11 Phase 1's open question: "where worker-binding
/// config lives" — a run-time sidecar, not the frozen workflow template).
/// <para>
/// <b>File format convention:</b> a single JSON object keyed by worker role name, each value a
/// <see cref="WorkerBindingConfigEntry"/> — deserialized through the same <see cref="JsonSerializer"/>
/// defaults <c>Aer.Flow.Templates.WorkflowDefinitionParser</c> uses for templates (case-sensitive,
/// PascalCase property names matching the record shapes exactly, no custom naming policy).
/// </para>
/// </summary>
public static class WorkerBindingConfigParser
{
    /// <summary>Parses a worker-binding config from a JSON string.</summary>
    /// <exception cref="WorkerBindingConfigException">The JSON is malformed or empty.</exception>
    public static IReadOnlyDictionary<string, WorkerBindingConfigEntry> Parse(string json)
    {
        Dictionary<string, WorkerBindingConfigEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<Dictionary<string, WorkerBindingConfigEntry>>(json);
        }
        catch (JsonException ex)
        {
            throw new WorkerBindingConfigException($"Malformed worker-binding config JSON: {ex.Message}", ex);
        }

        if (entries is null)
        {
            throw new WorkerBindingConfigException("Worker-binding config file did not contain a JSON object.");
        }

        foreach (var (workerName, entry) in entries)
        {
            if (entry is null)
            {
                throw new WorkerBindingConfigException($"Worker-binding config entry for '{workerName}' is null.");
            }

            if (string.IsNullOrWhiteSpace(entry.Adapter))
            {
                throw new WorkerBindingConfigException($"Worker-binding config entry for '{workerName}' is missing 'Adapter'.");
            }

            if (entry.Contract is null)
            {
                throw new WorkerBindingConfigException($"Worker-binding config entry for '{workerName}' is missing 'Contract'.");
            }

            if (string.IsNullOrWhiteSpace(entry.PromptTemplate))
            {
                throw new WorkerBindingConfigException($"Worker-binding config entry for '{workerName}' is missing 'PromptTemplate'.");
            }
        }

        return entries;
    }

    /// <summary>Reads <paramref name="path"/> and parses it as a worker-binding config.</summary>
    public static async Task<IReadOnlyDictionary<string, WorkerBindingConfigEntry>> LoadFromFileAsync(
        string path, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(json);
    }
}
