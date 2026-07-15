using System.Text.Json;

namespace Aer.Adapters;

/// <summary>
/// Writes a worker-binding config to a file (UI spec §9; M16 Phase 4, issue #153) — the first
/// bindings write path anywhere in the stack, and the counterpart to
/// <see cref="WorkerBindingConfigParser"/>.
/// <para>
/// <b>Placement decision of record:</b> the writer lives here, beside its parser in
/// <c>Aer.Adapters</c>, not inside <c>Aer.Ui</c> or <c>Aer.Flow.Templates</c> — the bindings shape
/// (adapter names, <see cref="Aer.Flow.Domain.WorkerContract"/>, prompt/timeout/model/permission
/// scope) lives entirely in this assembly (Adapter Isolation, the repo's own architecture rule),
/// exactly mirroring <c>Aer.Flow.Templates.WorkflowDefinitionWriter</c>'s placement reasoning
/// beside <c>WorkflowDefinitionParser</c> for templates (M16 Phase 1). <c>Aer.Ui</c> is the
/// writer's only caller, exactly as UI spec §4 assigns.
/// </para>
/// <para>
/// <b>Validation decision of record:</b> there is no separate <c>WorkerBindingConfigValidator</c> —
/// <see cref="WorkerBindingConfigParser.Parse"/>'s own field checks (non-blank <c>Adapter</c>, a
/// present <c>Contract</c>, non-blank <c>PromptTemplate</c>) are this format's only validation.
/// <see cref="Serialize"/> proves them by round-tripping its own output through that exact parser
/// before ever returning it, so "write nothing on failure" holds the same way
/// <c>Aer.Flow.Templates.WorkflowDefinitionWriter.Serialize</c> holds it via
/// <c>WorkflowDefinitionValidator.Validate</c> — just using the parser itself as the validation
/// step, since this format has no separate one.
/// </para>
/// <para>
/// Output is indented for the same reason a template's is: a hand-editable file (spec §11.1's
/// framing extends naturally to this sidecar config). The round-trip bar is parse-level fidelity,
/// never byte-level.
/// </para>
/// </summary>
public static class WorkerBindingConfigWriter
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>Serializes <paramref name="config"/> as indented bindings JSON, validating it by parsing it back first.</summary>
    /// <exception cref="WorkerBindingConfigException">
    /// <paramref name="config"/> fails to round-trip through <see cref="WorkerBindingConfigParser.Parse"/>
    /// (e.g. a blank <c>Adapter</c> or <c>PromptTemplate</c> on some entry).
    /// </exception>
    public static string Serialize(IReadOnlyDictionary<string, WorkerBindingConfigEntry> config)
    {
        var json = JsonSerializer.Serialize(config, IndentedOptions);
        WorkerBindingConfigParser.Parse(json);
        return json;
    }

    /// <summary>
    /// Persists <paramref name="config"/> as bindings JSON at <paramref name="bindingsFilePath"/>,
    /// creating parent directories as needed — the same shape as
    /// <c>Aer.Flow.Templates.WorkflowDefinitionWriter.SaveToFileAsync</c>.
    /// </summary>
    /// <exception cref="WorkerBindingConfigException">
    /// <paramref name="config"/> fails to round-trip through the parser; nothing is written.
    /// </exception>
    public static async Task SaveToFileAsync(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> config,
        string bindingsFilePath,
        CancellationToken cancellationToken = default)
    {
        var json = Serialize(config);

        var directory = Path.GetDirectoryName(bindingsFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(bindingsFilePath, json, cancellationToken).ConfigureAwait(false);
    }
}
