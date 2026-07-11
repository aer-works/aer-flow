using System.Text.Json;
using Aer.Flow.Domain;

namespace Aer.Flow.Outcomes;

/// <summary>
/// Walks a <see cref="WorkerContract"/>'s <c>ProducedOutputs</c> and asserts each is satisfied on
/// disk (spec §8, §4.1): the file must exist and, if it declares an <see cref="OutputCondition"/>,
/// the JSON Pointer in that condition must resolve to a value equal to the condition's literal.
/// Exit code 0 is necessary but not sufficient for <c>ExecutionSucceeded</c> — this is the
/// "sufficient" half the <see cref="OutcomeClassifier"/> consults.
/// </summary>
public static class ContractValidator
{
    /// <summary>
    /// True when every entry in <paramref name="contract"/>'s <c>ProducedOutputs</c> exists at
    /// <paramref name="outputDirectory"/> and satisfies its declared <see cref="OutputCondition"/>,
    /// if any.
    /// </summary>
    public static bool IsSatisfied(WorkerContract contract, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        foreach (var output in contract.ProducedOutputs)
        {
            var path = Path.Combine(outputDirectory, output.Name);
            if (!File.Exists(path))
            {
                return false;
            }

            if (output.Condition is not null && !IsConditionSatisfied(output.Condition, path))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsConditionSatisfied(OutputCondition condition, string path)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch (JsonException)
        {
            // §4.1 clause 2: a condition may only be declared on a JSON output. A file that fails
            // to parse as JSON fails the condition, exactly like a missing file.
            return false;
        }

        using (document)
        {
            return TryResolvePointer(document.RootElement, condition.Path, out var resolved)
                && ScalarEquals(resolved, condition.EqualsValue);
        }
    }

    /// <summary>Resolves an RFC 6901 JSON Pointer against a parsed document.</summary>
    private static bool TryResolvePointer(JsonElement root, string pointer, out JsonElement resolved)
    {
        resolved = root;

        if (pointer.Length == 0)
        {
            return true;
        }

        if (pointer[0] != '/')
        {
            throw new FormatException($"JSON Pointer '{pointer}' must start with '/' (RFC 6901).");
        }

        var current = root;
        foreach (var rawToken in pointer[1..].Split('/'))
        {
            var token = rawToken.Replace("~1", "/").Replace("~0", "~");

            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(token, out var property))
            {
                current = property;
            }
            else if (current.ValueKind == JsonValueKind.Array &&
                     int.TryParse(token, out var index) &&
                     index >= 0 &&
                     index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                return false;
            }
        }

        resolved = current;
        return true;
    }

    private static bool ScalarEquals(JsonElement resolved, JsonScalar expected) => expected switch
    {
        JsonScalar.String s => resolved.ValueKind == JsonValueKind.String && resolved.GetString() == s.Value,
        JsonScalar.Number n => resolved.ValueKind == JsonValueKind.Number && resolved.GetDouble() == n.Value,
        JsonScalar.Boolean b => resolved.ValueKind is JsonValueKind.True or JsonValueKind.False && resolved.GetBoolean() == b.Value,
        JsonScalar.Null => resolved.ValueKind == JsonValueKind.Null,
        _ => throw new ArgumentOutOfRangeException(nameof(expected), expected, "Unknown JsonScalar case."),
    };
}
