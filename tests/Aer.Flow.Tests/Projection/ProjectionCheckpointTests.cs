using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Tests.Shared;

namespace Aer.Flow.Tests.Projection;

public class ProjectionCheckpointTests
{
    private static readonly StepId Step1 = new("step1");
    private static readonly StepId Step2 = new("step2");

    private static WorkflowDefinitionSnapshot TestSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-checkpoint-test"),
        new WorkflowTemplateId("template-1"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Step1, "worker1", [], ["output1"], DependsOn: [], RetryPolicy: new RetryPolicy(2)),
            new WorkflowStepDefinition(Step2, "worker2", ["output1"], ["output2"], DependsOn: [Step1], RetryPolicy: new RetryPolicy(2)),
        ]);

    private static ExecutionRequest MakeRequest(ExecutionId executionId, StepId stepId) => new(
        executionId,
        new WorkflowId("wf-1"),
        stepId,
        "worker",
        Inputs: [],
        Outputs: [],
        Timeout: TimeSpan.FromMinutes(10),
        Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    private static void AssertFlowStateEqual(FlowState expected, FlowState actual)
    {
        Assert.Equal(expected.WorkflowDefinitionSnapshotId, actual.WorkflowDefinitionSnapshotId);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Steps.Count, actual.Steps.Count);
        for (int i = 0; i < expected.Steps.Count; i++)
        {
            AssertStepStateEqual(expected.Steps[i], actual.Steps[i]);
        }
        Assert.Equal(expected.StepLessExecutions.Count, actual.StepLessExecutions.Count);
        for (int i = 0; i < expected.StepLessExecutions.Count; i++)
        {
            Assert.Equal(expected.StepLessExecutions[i], actual.StepLessExecutions[i]);
        }
        Assert.Equal(expected.CancellationRequestedExecutionIds.Count, actual.CancellationRequestedExecutionIds.Count);
        for (int i = 0; i < expected.CancellationRequestedExecutionIds.Count; i++)
        {
            Assert.Equal(expected.CancellationRequestedExecutionIds[i], actual.CancellationRequestedExecutionIds[i]);
        }
    }

    private static void AssertStepStateEqual(StepState expected, StepState actual)
    {
        Assert.Equal(expected.StepId, actual.StepId);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.LatestExecutionId, actual.LatestExecutionId);
        Assert.Equal(expected.ConsecutiveFailureCount, actual.ConsecutiveFailureCount);
        Assert.Equal(expected.LatestFailureClassification, actual.LatestFailureClassification);
        Assert.Equal(expected.LatestFailureReason, actual.LatestFailureReason);
        Assert.Equal(expected.PauseRecordedForLatestExecution, actual.PauseRecordedForLatestExecution);
        Assert.Equal(expected.PausedOutcome, actual.PausedOutcome);
        Assert.Equal(expected.PendingSupplementaryExecutionId, actual.PendingSupplementaryExecutionId);
        Assert.Equal(expected.IsPendingSupersedeTarget, actual.IsPendingSupersedeTarget);
        Assert.Equal(expected.RetryNotBefore, actual.RetryNotBefore);
        Assert.Equal(expected.RetryDelayMs, actual.RetryDelayMs);
        Assert.Equal(expected.RetryScheduledForExecutionId, actual.RetryScheduledForExecutionId);
        Assert.Equal(expected.LatestExecutionFailedRetryNotBefore, actual.LatestExecutionFailedRetryNotBefore);
        Assert.Equal(expected.UpstreamExecutionIds.Count, actual.UpstreamExecutionIds.Count);
        foreach (var (k, v) in expected.UpstreamExecutionIds)
        {
            Assert.True(actual.UpstreamExecutionIds.TryGetValue(k, out var actualVal));
            Assert.Equal(v, actualVal);
        }
    }

    [Fact]
    public void Equivalence_checkpoint_plus_tail_equals_full_replay()
    {
        var snapshot = TestSnapshot();
        var exec1 = new ExecutionId("exec-1");
        var exec2 = new ExecutionId("exec-2");

        var midwayEvents = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, DateTimeOffset.UtcNow),
            new FlowEvent.ExecutionSucceeded(exec1),
        };

        var (midwayState, checkpointMidway) = StateProjector.ProjectAndCheckpoint(midwayEvents, snapshot);
        Assert.Equal(2, checkpointMidway.EventOffset);
        Assert.Equal(StepStatus.Succeeded, Assert.Single(midwayState.Steps, s => s.StepId == Step1).Status);

        var allEvents = new List<FlowEvent>(midwayEvents)
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec2, Step2), 101, DateTimeOffset.UtcNow),
            new FlowEvent.ExecutionSucceeded(exec2),
        };

        // Projected via checkpoint + tail replay
        var stateFromCheckpoint = StateProjector.Project(allEvents, snapshot, checkpointMidway);

        // Projected via full replay
        var stateFromFullReplay = StateProjector.Project(allEvents, snapshot, checkpoint: null);

        // Deep structural equality assertion
        AssertFlowStateEqual(stateFromFullReplay, stateFromCheckpoint);
        Assert.Equal(WorkflowStatus.Terminal, stateFromCheckpoint.Status);
        Assert.Equal(StepStatus.Succeeded, Assert.Single(stateFromCheckpoint.Steps, s => s.StepId == Step2).Status);
    }

    [Fact]
    public void Polarity_corrupt_checkpoint_file_falls_back_to_full_replay_loudly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "aer_checkpoint_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var aerDir = Path.Combine(tempDir, ".aer");
            Directory.CreateDirectory(aerDir);
            var checkpointFile = Path.Combine(aerDir, "checkpoint.json");
            File.WriteAllText(checkpointFile, "{ corrupt json ... }}}");

            using var sw = new StringWriter();
            var originalErr = Console.Error;
            Console.SetError(sw);

            ProjectionCheckpoint? checkpoint = null;
            try
            {
                checkpoint = ProjectionCheckpointStore.Load(tempDir);
            }
            finally
            {
                Console.SetError(originalErr);
            }

            Assert.Null(checkpoint);
            var errOutput = sw.ToString();
            Assert.Contains("Fallback to full replay LOUDLY", errOutput);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public void Polarity_checkpoint_offset_exceeds_log_length_falls_back_to_full_replay_loudly()
    {
        var snapshot = TestSnapshot();
        var exec1 = new ExecutionId("exec-1");
        var events = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, DateTimeOffset.UtcNow),
            new FlowEvent.ExecutionSucceeded(exec1),
        };

        var (_, validCheckpoint) = StateProjector.ProjectAndCheckpoint(events, snapshot);
        var invalidCheckpoint = new ProjectionCheckpoint(EventOffset: 999, validCheckpoint.State);

        using var sw = new StringWriter();
        var originalErr = Console.Error;
        Console.SetError(sw);

        FlowState state;
        try
        {
            state = StateProjector.Project(events, snapshot, invalidCheckpoint);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        var errOutput = sw.ToString();
        Assert.Contains("Fallback to full replay LOUDLY", errOutput);

        var fullReplayState = StateProjector.Project(events, snapshot, checkpoint: null);
        AssertFlowStateEqual(fullReplayState, state);
    }

    [Fact]
    public void Stale_checkpoint_arm_replays_tail_events()
    {
        var snapshot = TestSnapshot();
        var exec1 = new ExecutionId("exec-1");
        var exec2 = new ExecutionId("exec-2");

        var eventsAtCheckpoint = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, DateTimeOffset.UtcNow),
            new FlowEvent.ExecutionSucceeded(exec1),
        };

        var (stateAtCheckpoint, checkpoint) = StateProjector.ProjectAndCheckpoint(eventsAtCheckpoint, snapshot);
        Assert.Equal(StepStatus.Pending, Assert.Single(stateAtCheckpoint.Steps, s => s.StepId == Step2).Status);

        var updatedEvents = new List<FlowEvent>(eventsAtCheckpoint)
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec2, Step2), 101, DateTimeOffset.UtcNow),
            new FlowEvent.ExecutionSucceeded(exec2),
        };

        var reopenedState = StateProjector.Project(updatedEvents, snapshot, checkpoint);

        // Tail events must be reflected in reopened state
        Assert.Equal(StepStatus.Succeeded, Assert.Single(reopenedState.Steps, s => s.StepId == Step2).Status);
        Assert.Equal(WorkflowStatus.Terminal, reopenedState.Status);
    }

    [Fact]
    public void Determinism_deleting_checkpoint_changes_nothing_except_open_cost()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "aer_checkpoint_det_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var snapshot = TestSnapshot();
            var exec1 = new ExecutionId("exec-1");
            var events = new List<FlowEvent>
            {
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, DateTimeOffset.UtcNow),
                new FlowEvent.ExecutionSucceeded(exec1),
            };

            var (_, checkpoint) = StateProjector.ProjectAndCheckpoint(events, snapshot);
            ProjectionCheckpointStore.Save(tempDir, checkpoint);

            var loadedCheckpoint = ProjectionCheckpointStore.Load(tempDir);
            var stateWithCheckpoint = StateProjector.Project(events, snapshot, loadedCheckpoint);

            ProjectionCheckpointStore.Delete(tempDir);
            var checkpointAfterDelete = ProjectionCheckpointStore.Load(tempDir);
            Assert.Null(checkpointAfterDelete);

            var stateWithoutCheckpoint = StateProjector.Project(events, snapshot, checkpoint: null);

            AssertFlowStateEqual(stateWithoutCheckpoint, stateWithCheckpoint);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public void Red_first_proof_skipping_tail_replay_fails_equivalence()
    {
        var snapshot = TestSnapshot();
        var exec1 = new ExecutionId("exec-1");
        var exec2 = new ExecutionId("exec-2");

        var midwayEvents = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec1, Step1), 100, DateTimeOffset.UtcNow),
            new FlowEvent.ExecutionSucceeded(exec1),
        };

        var (_, checkpoint) = StateProjector.ProjectAndCheckpoint(midwayEvents, snapshot);

        var allEvents = new List<FlowEvent>(midwayEvents)
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(exec2, Step2), 101, DateTimeOffset.UtcNow),
            new FlowEvent.ExecutionSucceeded(exec2),
        };

        // Full replay reflects all events
        var fullReplayState = StateProjector.Project(allEvents, snapshot, checkpoint: null);

        // Simulated broken checkpoint load (skipping tail replay by building state strictly from checkpoint without processing tail events)
        var brokenStateFromCheckpointOnly = StateProjector.Project(midwayEvents, snapshot, checkpoint);

        // Proves that the equivalence test discriminates: broken state is NOT equal to full replay state
        Assert.NotEqual(fullReplayState.Status, brokenStateFromCheckpointOnly.Status);
        Assert.Equal(StepStatus.Pending, Assert.Single(brokenStateFromCheckpointOnly.Steps, s => s.StepId == Step2).Status);
        Assert.Equal(StepStatus.Succeeded, Assert.Single(fullReplayState.Steps, s => s.StepId == Step2).Status);
    }
}
