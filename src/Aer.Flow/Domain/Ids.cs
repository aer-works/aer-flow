using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aer.Flow.Domain;

/// <summary>
/// Serializes a string-backed strongly-typed ID as a plain JSON string (both as a value and as a
/// dictionary key), rather than as a wrapper object with a "Value" property.
/// </summary>
public abstract class StringIdJsonConverter<T> : JsonConverter<T>
{
    protected abstract T Create(string value);

    protected abstract string GetValue(T id);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Create(reader.GetString() ?? throw new JsonException($"Expected a string for {typeToConvert}."));

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(GetValue(value));

    public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Create(reader.GetString() ?? throw new JsonException($"Expected a string property name for {typeToConvert}."));

    public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WritePropertyName(GetValue(value));
}

/// <summary>
/// Globally unique, generated once by Flow (spec §3). The sole join key between Flow-owned and
/// Core-owned events for a given execution — never associated by timestamp or file order (§6).
/// </summary>
[JsonConverter(typeof(Converter))]
public readonly record struct ExecutionId(string Value)
{
    public override string ToString() => Value;

    private sealed class Converter : StringIdJsonConverter<ExecutionId>
    {
        protected override ExecutionId Create(string value) => new(value);
        protected override string GetValue(ExecutionId id) => id.Value;
    }
}

/// <summary>Identifies the workflow task an <see cref="ExecutionRequest"/> belongs to (spec §3).</summary>
[JsonConverter(typeof(Converter))]
public readonly record struct WorkflowId(string Value)
{
    public override string ToString() => Value;

    private sealed class Converter : StringIdJsonConverter<WorkflowId>
    {
        protected override WorkflowId Create(string value) => new(value);
        protected override string GetValue(WorkflowId id) => id.Value;
    }
}

/// <summary>
/// Identifies a step within a <see cref="WorkflowDefinition"/>. <c>DependsOn</c> references
/// <see cref="StepId"/>, never <see cref="ExecutionId"/> (spec §11.3).
/// </summary>
[JsonConverter(typeof(Converter))]
public readonly record struct StepId(string Value)
{
    public override string ToString() => Value;

    private sealed class Converter : StringIdJsonConverter<StepId>
    {
        protected override StepId Create(string value) => new(value);
        protected override string GetValue(StepId id) => id.Value;
    }
}

/// <summary>Identifies a <see cref="WorkflowDefinition"/> template, stable across versions (spec §11.1).</summary>
[JsonConverter(typeof(Converter))]
public readonly record struct WorkflowTemplateId(string Value)
{
    public override string ToString() => Value;

    private sealed class Converter : StringIdJsonConverter<WorkflowTemplateId>
    {
        protected override WorkflowTemplateId Create(string value) => new(value);
        protected override string GetValue(WorkflowTemplateId id) => id.Value;
    }
}

/// <summary>Globally unique, generated once by Flow when a template is bound to a task (spec §11.2).</summary>
[JsonConverter(typeof(Converter))]
public readonly record struct WorkflowDefinitionSnapshotId(string Value)
{
    public override string ToString() => Value;

    private sealed class Converter : StringIdJsonConverter<WorkflowDefinitionSnapshotId>
    {
        protected override WorkflowDefinitionSnapshotId Create(string value) => new(value);
        protected override string GetValue(WorkflowDefinitionSnapshotId id) => id.Value;
    }
}

/// <summary>Identifies an <see cref="FlowEvent.ExternalDecisionRecorded"/> event (spec §17.2).</summary>
[JsonConverter(typeof(Converter))]
public readonly record struct DecisionId(string Value)
{
    public override string ToString() => Value;

    private sealed class Converter : StringIdJsonConverter<DecisionId>
    {
        protected override DecisionId Create(string value) => new(value);
        protected override string GetValue(DecisionId id) => id.Value;
    }
}
