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
public sealed record ProducedOutput(string Name, OutputCondition? Condition = null);

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
