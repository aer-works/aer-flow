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
    /// <param name="json">The config document.</param>
    /// <param name="sourcePath">
    /// The file <paramref name="json"/> was read from, named in the error when the JSON is
    /// malformed (#562) — <c>null</c> for callers with no file.
    /// </param>
    /// <exception cref="WorkerBindingConfigException">The JSON is malformed or empty.</exception>
    public static IReadOnlyDictionary<string, WorkerBindingConfigEntry> Parse(string json, string? sourcePath = null)
    {
        Dictionary<string, WorkerBindingConfigEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<Dictionary<string, WorkerBindingConfigEntry>>(json);
        }
        catch (JsonException ex)
        {
            var location = sourcePath is null ? string.Empty : $" in '{sourcePath}'";
            const string shape =
                "A valid worker-binding config looks like: "
                + "{ \"<workerName>\": { \"Adapter\": \"<string>\", \"Contract\": { ... }, "
                + "\"PromptTemplate\": \"<string>\", \"Timeout\": \"<hh:mm:ss>\" } }.";
            throw new WorkerBindingConfigException($"Malformed worker-binding config JSON{location}: {ex.Message} {shape}", ex);
        }

        if (entries is null)
        {
            var location = sourcePath is null ? string.Empty : $" '{sourcePath}'";
            throw new WorkerBindingConfigException($"Worker-binding config file{location} did not contain a JSON object.");
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

            if (entry.WorkingDirectory is not null && string.IsNullOrWhiteSpace(entry.WorkingDirectory))
            {
                throw new WorkerBindingConfigException(
                    $"Worker-binding config entry for '{workerName}' has a blank 'WorkingDirectory' — omit the field entirely instead.");
            }

            // A non-positive Timeout is not a slow worker, it is an unrunnable one, and nothing
            // downstream treats it as an error: it reaches AerTask.WithTimeout as
            // Duration::from_millis(0), whose monitor thread kills the process tree immediately. An
            // *omitted* Timeout deserializes to default(TimeSpan) and lands in the same place, so the
            // most likely way to hit this is forgetting the field rather than typing a silly value.
            // Rejecting here also bounds what GeminiWorkerAdapter's --print-timeout can be derived
            // from (#588): a negative timeout would otherwise floor that flag at 1s while AER's own
            // limit misbehaves, inverting the very ordering that flag exists to establish.
            if (entry.Timeout <= TimeSpan.Zero)
            {
                throw new WorkerBindingConfigException(
                    $"Worker-binding config entry for '{workerName}' has a 'Timeout' of "
                    + $"'{entry.Timeout}' — it must be positive. Omitting the field leaves it zero, "
                    + "which would kill the worker the moment it starts.");
            }
        }

        return entries;
    }

    /// <summary>Reads <paramref name="path"/> and parses it as a worker-binding config.</summary>
    public static async Task<IReadOnlyDictionary<string, WorkerBindingConfigEntry>> LoadFromFileAsync(
        string path, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(json, path);
    }
}
