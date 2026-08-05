using System.Text.Json.Serialization;

namespace Aer.Flow.Domain;

/// <summary>
/// A logical execution target (e.g. <c>claude</c>, <c>agy</c>, <c>git</c>) bound to a typed
/// contract, not a vendor name (spec §4). A <see cref="WorkflowStepDefinition"/> declares which
/// contract it requires; the concrete binary is resolved via configuration external to the
/// workflow.
/// </summary>
public sealed record WorkerContract(
    string WorkerName,
    IReadOnlyList<string> RequiredInputs,
    IReadOnlyList<ProducedOutput> ProducedOutputs,
    IReadOnlyList<string> OptionalMetadata);

/// <summary>A named output file role a <see cref="WorkerContract"/> requires (spec §4).</summary>
/// <param name="Schema">
/// A declared document shape the file must parse as (spec §4.2, decision 0043) — the structural
/// sibling of <paramref name="Condition"/>. Serialized only when set, so contracts that predate
/// the field round-trip byte-identically.
/// </param>
public sealed record ProducedOutput
{
    public string Name { get; init; } = string.Empty;
    public OutputCondition? Condition { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public OutputSchema Schema { get; init; }

    [JsonConstructor]
    public ProducedOutput(string Name, OutputCondition? Condition = null, OutputSchema Schema = OutputSchema.None)
    {
        if (Name is not null && Name.StartsWith('.'))
        {
            throw new ArgumentException(
                $"ProducedOutput name '{Name}' is invalid: names starting with '.' are reserved for engine stream logs.",
                nameof(Name));
        }

        this.Name = Name ?? string.Empty;
        this.Condition = Condition;
        this.Schema = Schema;
    }
}

/// <summary>
/// The closed set of shapes a <see cref="ProducedOutput"/> can declare (spec §4.2). Validation is
/// parse-only in every case: the engine checks the file <i>is</i> the shape, and never reads its
/// content to route (Architecture Rule 1; decision 0043's boundary).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OutputSchema>))]
public enum OutputSchema
{
    /// <summary>No declared shape — existence (plus any <see cref="OutputCondition"/>) suffices.</summary>
    None,

    /// <summary>The output must parse per <see cref="ReviewVerdictSchema.TryParse"/>.</summary>
    ReviewVerdict,

    /// <summary>The output must parse per <see cref="UnifiedDiffSchema.TryParse"/>.</summary>
    Diff,
}

/// <summary>
/// Extends a <see cref="ProducedOutput"/>'s contract from "this file must exist" to "this file
/// must exist and say this" (spec §4.1). Satisfied only when the file exists, parses as JSON, the
/// <paramref name="Path"/> JSON Pointer resolves, and the resolved value equals
/// <paramref name="EqualsValue"/>.
/// </summary>
/// <param name="EqualsValue">
/// Named <c>EqualsValue</c> rather than <c>Equals</c> — a record positional parameter named
/// <c>Equals</c> collides with the record's synthesized <c>Equals</c> method (CS0102). Serializes
/// under the spec's own field name, <c>equals</c>.
/// </param>
public sealed record OutputCondition(string Path, [property: JsonPropertyName("equals")] JsonScalar EqualsValue);
