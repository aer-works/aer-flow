using Aer.Flow.CrashTestHost;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;

// M10 Phase 4 (issue #72): a small, test-only pump host standing in for Aer.Cli, which is still a
// stub. Aer.Flow.Tests spawns this as a real OS process, waits for a specific durable fact to
// appear in the log, then kills it — exercising MutationInterface.StartWorkflowAsync against real
// Core dispatch, then reconciling from a real, killed-mid-run log via a second, in-process run.
//
// args: <pausePoint> <taskDirectory> <artifactsRoot> <logPath> <pauseSignalPath> <cancelSignalPath>
//   pausePoint: "none" | "before-dispatch" | "after-dispatch" (see DispatchPausePoint).
if (args.Length != 6)
{
    await Console.Error.WriteLineAsync(
        "usage: <pausePoint> <taskDirectory> <artifactsRoot> <logPath> <pauseSignalPath> <cancelSignalPath>");
    return 1;
}

var pausePoint = args[0] switch
{
    "none" => DispatchPausePoint.None,
    "before-dispatch" => DispatchPausePoint.BeforeDispatch,
    "after-dispatch" => DispatchPausePoint.AfterDispatch,
    _ => throw new ArgumentException($"Unknown pausePoint '{args[0]}'."),
};
var taskDirectory = args[1];
var artifactsRoot = args[2];
var logPath = args[3];
var pauseSignalPath = args[4];
var cancelSignalPath = args[5];

// The worker only needs to be genuinely long-running for the no-pause (orphan) scenario, where
// this run's own real timing — not a decorator pause — is what leaves it still executing when
// killed. Both paused scenarios never let a real dispatch reach the worker at all (before-dispatch)
// or let it run to a real, fast, natural exit before pausing (after-dispatch).
var workerKind = pausePoint == DispatchPausePoint.None ? ScenarioWorker.LongSleep : ScenarioWorker.QuickSuccess;
var (snapshot, bindings) = Scenarios.Build(workerKind);

await using var writer = new FlowEventLogWriter(logPath);
var reader = new FlowEventLogReader(logPath);
var dispatcher = new PausableCoreDispatcher(new CoreDispatcher(writer), pausePoint, pauseSignalPath);
var inFlightExecutions = new InFlightExecutionRegistry();

// Fire-and-forget: harmless if this process is killed before cancelSignalPath ever appears (the
// common case for every scenario except the unfulfilled-cancellation one), since nothing here has
// any effect until that file exists.
_ = WatchForCancelSignalAsync(cancelSignalPath, reader, inFlightExecutions);

await MutationInterface.StartWorkflowAsync(
    Scenarios.WorkflowId, taskDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
    inFlightExecutions: inFlightExecutions);

return 0;

static async Task WatchForCancelSignalAsync(
    string cancelSignalPath, IEventLogReader reader, InFlightExecutionRegistry inFlightExecutions)
{
    while (!File.Exists(cancelSignalPath))
    {
        await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
    }

    // Resolves from this process's own log rather than a passed-in argument: the ExecutionId is
    // minted fresh by MutationInterface on every run and unknowable to the test harness in advance.
    ExecutionId? executionId = null;
    while (executionId is null)
    {
        var events = await reader.ReadAllAsync().ConfigureAwait(false);
        var accepted = events.OfType<FlowEvent.ExecutionRequestAccepted>().FirstOrDefault(e => e.Request.StepId == Scenarios.StepA);
        executionId = accepted?.Request.ExecutionId;
        if (executionId is null)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }
    }

    await inFlightExecutions.RequestCancellationAsync(executionId.Value).ConfigureAwait(false);
}
