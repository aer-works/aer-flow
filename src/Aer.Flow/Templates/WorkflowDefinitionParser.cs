using System.Text.Json;
using Aer.Flow.Domain;

namespace Aer.Flow.Templates;

/// <summary>
/// Loads a <see cref="WorkflowDefinition"/> template from a file (spec §11.1) and validates it.
/// <para>
/// <b>File format convention:</b> templates are plain JSON, deserialized through the same
/// <see cref="JsonSerializer"/> converters the rest of <c>Aer.Flow</c> already uses for
/// <c>flow.jsonl</c> and every other domain record. The spec leaves the format
/// implementation-defined; JSON was chosen over TOML specifically to avoid a second
/// serialization stack for one file type, not because a template is itself a JSON Lines stream —
/// a template is a single document, so it is <c>.json</c>, not <c>.jsonl</c>.
/// </para>
/// </summary>
public static class WorkflowDefinitionParser
{
    /// <summary>Parses and validates a template from a JSON string.</summary>
    /// <exception cref="WorkflowDefinitionValidationException">
    /// The JSON is malformed, empty, or the parsed <see cref="WorkflowDefinition"/> fails
    /// structural validation (see <see cref="WorkflowDefinitionValidator.Validate"/>).
    /// </exception>
    public static WorkflowDefinition Parse(string json)
    {
        WorkflowDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize<WorkflowDefinition>(json);
        }
        catch (JsonException ex)
        {
            throw new WorkflowDefinitionValidationException([$"Malformed template JSON: {ex.Message}"], ex);
        }

        if (definition is null)
        {
            throw new WorkflowDefinitionValidationException(["Template file did not contain a WorkflowDefinition object."]);
        }

        WorkflowDefinitionValidator.Validate(definition);
        return definition;
    }

    /// <summary>Reads <paramref name="path"/> and parses it as a <see cref="WorkflowDefinition"/> template.</summary>
    public static async Task<WorkflowDefinition> LoadFromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(json);
    }
}
