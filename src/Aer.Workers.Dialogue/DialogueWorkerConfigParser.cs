using System.Text.Json;

namespace Aer.Workers.Dialogue;

/// <summary>
/// Loads a <see cref="DialogueWorkerConfig"/> from a file (M17 Phase 2, #165). Mirrors
/// <c>Aer.Adapters.WorkerBindingConfigParser</c>'s conventions: the same <see cref="JsonSerializer"/>
/// defaults <c>Aer.Flow.Templates.WorkflowDefinitionParser</c> and <c>WorkerBindingConfigParser</c>
/// use (case-sensitive, PascalCase property names matching the record shapes exactly, no custom
/// naming policy), and the same "parse, then validate structurally" shape.
/// </summary>
public static class DialogueWorkerConfigParser
{
    /// <summary>Parses a dialogue-worker config from a JSON string.</summary>
    /// <exception cref="DialogueWorkerConfigException">The JSON is malformed, empty, or structurally invalid.</exception>
    public static DialogueWorkerConfig Parse(string json)
    {
        DialogueWorkerConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<DialogueWorkerConfig>(json);
        }
        catch (JsonException ex)
        {
            throw new DialogueWorkerConfigException($"Malformed dialogue-worker config JSON: {ex.Message}", ex);
        }

        if (config is null)
        {
            throw new DialogueWorkerConfigException("Dialogue-worker config file did not contain a JSON object.");
        }

        if (string.IsNullOrWhiteSpace(config.SeedPrompt))
        {
            throw new DialogueWorkerConfigException("Dialogue-worker config is missing 'SeedPrompt'.");
        }

        if (config.TurnBudget <= 0)
        {
            throw new DialogueWorkerConfigException("Dialogue-worker config's 'TurnBudget' must be positive.");
        }

        if (string.IsNullOrWhiteSpace(config.FinalOutputName))
        {
            throw new DialogueWorkerConfigException("Dialogue-worker config is missing 'FinalOutputName'.");
        }

        ValidateParticipant(config.Initiator, nameof(config.Initiator));
        ValidateParticipant(config.Responder, nameof(config.Responder));

        return config;
    }

    /// <summary>Reads <paramref name="path"/> and parses it as a dialogue-worker config.</summary>
    public static async Task<DialogueWorkerConfig> LoadFromFileAsync(
        string path, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(json);
    }

    private static void ValidateParticipant(DialogueParticipant? participant, string fieldName)
    {
        if (participant is null)
        {
            throw new DialogueWorkerConfigException($"Dialogue-worker config is missing '{fieldName}'.");
        }

        if (string.IsNullOrWhiteSpace(participant.Role))
        {
            throw new DialogueWorkerConfigException($"Dialogue-worker config's '{fieldName}' is missing 'Role'.");
        }

        if (string.IsNullOrWhiteSpace(participant.Vendor))
        {
            throw new DialogueWorkerConfigException($"Dialogue-worker config's '{fieldName}' is missing 'Vendor'.");
        }

        if (string.IsNullOrWhiteSpace(participant.Command))
        {
            throw new DialogueWorkerConfigException($"Dialogue-worker config's '{fieldName}' is missing 'Command'.");
        }

        if (participant.Args is null || !participant.Args.Contains(DialogueParticipant.PromptPlaceholder))
        {
            throw new DialogueWorkerConfigException(
                $"Dialogue-worker config's '{fieldName}.Args' must contain the literal " +
                $"'{DialogueParticipant.PromptPlaceholder}' placeholder.");
        }
    }
}
