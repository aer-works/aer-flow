using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;

namespace Aer.Flow.CrashTestHost;

/// <summary>The single Core dispatch a crash-window run under test uses (see <see cref="Scenarios.Build"/>).</summary>
public enum ScenarioWorker
{
    /// <summary>Writes its declared output and exits 0 almost immediately.</summary>
    QuickSuccess,

    /// <summary>Never exits on its own within any test's timeout — genuinely still running until killed.</summary>
    LongSleep,
}

/// <summary>
/// Builds the one-step <see cref="WorkflowDefinitionSnapshot"/>/<see cref="WorkerBinding"/> pair
/// every M10 Phase 4 crash-window test uses, shared between the killed run
/// (<see cref="Program"/>, a separate OS process) and the in-process recovery run
/// (<c>Aer.Flow.Tests</c>) so both bind against the identical definition — the real-process
/// analogue of <c>MutationInterfaceCrashRecoveryTests</c>' shared fixture helpers.
/// </summary>
public static class Scenarios
{
    public static readonly StepId StepA = new("a");
    public static readonly WorkflowId WorkflowId = new("wf-crash-test");

    public static (WorkflowDefinitionSnapshot Snapshot, IReadOnlyDictionary<string, WorkerBinding> Bindings) Build(
        ScenarioWorker worker)
    {
        var target = worker switch
        {
            ScenarioWorker.QuickSuccess => OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", "echo done>%AER_OUTPUT_DIR%\\result"])
                : new CoreDispatchTarget("sh", ["-c", "echo done > \"$AER_OUTPUT_DIR/result\""]),
            ScenarioWorker.LongSleep => OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", "timeout /t 120 /nobreak >nul"])
                : new CoreDispatchTarget("sh", ["-c", "sleep 120"]),
            _ => throw new ArgumentOutOfRangeException(nameof(worker), worker, "Unknown ScenarioWorker."),
        };

        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snapshot-crash-test"),
            new WorkflowTemplateId("crash-test-host"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(StepA, "worker", [], ["result"], DependsOn: [], new RetryPolicy(2))]);

        var bindings = new Dictionary<string, WorkerBinding>
        {
            ["worker"] = new WorkerBinding.Process(
                new WorkerContract("worker", [], [new ProducedOutput("result")], []),
                target,
                TimeSpan.FromMinutes(5)),
        };

        return (snapshot, bindings);
    }
}
