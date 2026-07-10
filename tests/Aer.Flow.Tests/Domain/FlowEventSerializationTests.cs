using System.Text.Json;
using Aer.Flow.Domain;

namespace Aer.Flow.Tests.Domain;

public class FlowEventSerializationTests
{
    private static readonly ExecutionId ExecutionId = new("exec-1");
    private static readonly StepId StepId = new("build");

    public static IEnumerable<object[]> AllEventVariants()
    {
        var request = new ExecutionRequest(
            ExecutionId,
            new WorkflowId("wf-1"),
            StepId,
            "claude",
            Inputs: ["/artifacts/execution_1/goal.md"],
            Outputs: ["/artifacts/execution_2/plan.md"],
            Timeout: TimeSpan.FromMinutes(10),
            Environment:
            [
                new EnvironmentVariable.AerComputed("AER_OUTPUT_DIR", "/artifacts/execution_2"),
                new EnvironmentVariable.PassThrough("ANTHROPIC_API_KEY"),
            ],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId> { [new StepId("architect")] = new ExecutionId("exec-0") });

        yield return [new FlowEvent.ExecutionRequestAccepted(request)];
        yield return [new FlowEvent.ExecutionRequestRejected(ExecutionId, "concurrency cap reached")];
        yield return [new FlowEvent.ExecutionSucceeded(ExecutionId)];
        yield return [new FlowEvent.ExecutionFailed(ExecutionId, FailureClassification.Retryable)];
        yield return [new FlowEvent.ExecutionFailed(ExecutionId, FailureClassification: null)];
        yield return [new FlowEvent.ExecutionCancelled(ExecutionId)];
        yield return [new FlowEvent.CancellationRequested(ExecutionId)];
        yield return [new FlowEvent.WorkflowPaused(ExecutionId, StepId)];
        yield return
        [
            new FlowEvent.ExternalDecisionRecorded(
                new DecisionId("decision-1"),
                ExecutionId,
                DecisionType.Supersede,
                new StepId("architect"),
                new ExecutionId("exec-9"))
        ];
        yield return [new FlowEvent.WorkflowResumed(new DecisionId("decision-1"))];
    }

    [Theory]
    [MemberData(nameof(AllEventVariants))]
    public void RoundTrips_through_the_FlowEvent_base_type_without_data_loss(FlowEvent original)
    {
        var json = JsonSerializer.Serialize(original, typeof(FlowEvent));

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(json);
        Assert.NotNull(deserialized);

        var reserialized = JsonSerializer.Serialize(deserialized, typeof(FlowEvent));
        Assert.Equal(json, reserialized);
        Assert.Equal(original.GetType(), deserialized.GetType());
    }

    [Fact]
    public void Deserializing_an_unknown_event_type_discriminator_throws()
    {
        const string json = """{"eventType":"somethingElse"}""";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<FlowEvent>(json));
    }
}
