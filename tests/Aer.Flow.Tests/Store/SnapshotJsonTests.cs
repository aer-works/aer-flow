using System.Text.Json;
using System.Text.Json.Nodes;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Flow.Templates;

namespace Aer.Flow.Tests.Store;

/// <summary>
/// #619: the snapshot's wire contract. <c>snapshot.json</c> is durable, unreconstructable state.
/// Enums used to persist as ordinals so reordering a declaration reinterpreted every snapshot on disk.
/// </summary>
public class SnapshotJsonTests
{
    private static WorkflowDefinitionSnapshot SampleSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snap-1"),
        new WorkflowTemplateId("template-1"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(
                new StepId("step-1"),
                "worker-1",
                Inputs: ["in1"],
                Outputs: ["out1"],
                DependsOn: [],
                RetryPolicy: new RetryPolicy(3, BackoffPolicy.Steady),
                PausePoint: new PausePoint([], PausePointKind.NeedsInput)),
        ]);

    [Fact]
    public void Enums_persist_by_name_so_reordering_a_declaration_cannot_reinterpret_the_snapshot()
    {
        var snapshot = SampleSnapshot();
        var json = JsonSerializer.Serialize(snapshot, SnapshotJson.Options);

        Assert.Contains("\"NeedsInput\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Kind\":1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_intact_snapshot_round_trips()
    {
        var original = SampleSnapshot();
        var json = JsonSerializer.Serialize(original, SnapshotJson.Options);
        var deserialized = JsonSerializer.Deserialize<WorkflowDefinitionSnapshot>(json, SnapshotJson.Options);

        Assert.NotNull(deserialized);
        Assert.Equal(
            json, JsonSerializer.Serialize(deserialized, SnapshotJson.Options));
    }

    [Theory]
    [InlineData(0, PausePointKind.ReadyForReview)]
    [InlineData(1, PausePointKind.NeedsInput)]
    public void A_snapshot_written_before_this_change_still_replays_its_ordinal_enums(
        int ordinal, PausePointKind expected)
    {
        var legacy = $$"""
            {
                "WorkflowDefinitionSnapshotId": "snap-1",
                "WorkflowTemplateId": "template-1",
                "WorkflowTemplateVersion": 1,
                "Steps": [
                    {
                        "StepId": "step-1",
                        "Worker": "worker-1",
                        "Inputs": [],
                        "Outputs": [],
                        "DependsOn": [],
                        "RetryPolicy": { "MaxAttempts": 1, "Backoff": "steady" },
                        "PausePoint": { "SupersedeTargets": [], "Kind": {{ordinal}} }
                    }
                ]
            }
            """;

        var deserialized = JsonSerializer.Deserialize<WorkflowDefinitionSnapshot>(legacy, SnapshotJson.Options);

        Assert.NotNull(deserialized);
        var step = Assert.Single(deserialized.Steps);
        Assert.NotNull(step.PausePoint);
        Assert.Equal(expected, step.PausePoint.Kind);
    }

    [Fact]
    public void The_ordinals_legacy_snapshots_carry_still_mean_what_they_meant_when_written()
    {
        Assert.Equal(0, (int)PausePointKind.ReadyForReview);
        Assert.Equal(1, (int)PausePointKind.NeedsInput);

        Assert.Equal(0, (int)JitterMode.None);
        Assert.Equal(1, (int)JitterMode.Half);
    }

    [Fact]
    public void Every_enum_reachable_from_a_snapshot_is_pinned_by_these_tests()
    {
        var pinned = new[] { typeof(PausePointKind), typeof(JitterMode) };

        var reachable = new HashSet<Type>();
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>([typeof(WorkflowDefinitionSnapshot), typeof(WorkflowDefinition)]);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type))
            {
                continue;
            }

            foreach (var parameter in type.GetConstructors().SelectMany(c => c.GetParameters()))
            {
                foreach (var candidate in Unwrap(parameter.ParameterType))
                {
                    if (candidate.IsEnum)
                    {
                        reachable.Add(candidate);
                    }
                    else if (candidate.Namespace?.StartsWith("Aer.Flow", StringComparison.Ordinal) == true)
                    {
                        queue.Enqueue(candidate);
                    }
                }
            }

            foreach (var prop in type.GetProperties())
            {
                foreach (var candidate in Unwrap(prop.PropertyType))
                {
                    if (candidate.IsEnum)
                    {
                        reachable.Add(candidate);
                    }
                    else if (candidate.Namespace?.StartsWith("Aer.Flow", StringComparison.Ordinal) == true)
                    {
                        queue.Enqueue(candidate);
                    }
                }
            }
        }

        Assert.Equal(pinned.OrderBy(t => t.Name), reachable.OrderBy(t => t.Name));
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            yield return underlying;
            yield break;
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var inner in Unwrap(argument))
                {
                    yield return inner;
                }
            }

            yield break;
        }

        yield return type;
    }
}
