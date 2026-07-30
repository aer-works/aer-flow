using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Aer.Flow.Templates;

namespace Aer.Cli;

/// <summary>
/// <c>aer status</c> (#730): a read-only projection of a task directory's recorded events —
/// "this session's workaround was hand-rolled monitors polling PIDs and tailing <c>flow.jsonl</c>
/// by path", which this replaces with the product's own register. Every field printed comes from
/// <see cref="StateProjector.Project"/> — the same projection <see cref="RunCommand"/>,
/// <see cref="CancelCommand"/> and <see cref="Aer.Ui.Core.TaskProjectionLoader"/> already call — so
/// there is exactly one place "what does this event log mean" is computed, never a second reader of
/// the format here.
/// <para>
/// Deliberately never takes <see cref="Aer.Flow.Concurrency.ConcurrencyGuard"/>'s lock and never
/// constructs a <see cref="FlowEventLogWriter"/>: this is the one command in <c>Aer.Cli</c> that can
/// run concurrently with a live <c>aer run</c> pump on the same task directory, which is the whole
/// point of a status/watch command. It also never resolves a worker binding (no <c>--bindings</c>
/// option exists on <see cref="StatusOptions"/> at all) — nothing here dispatches, so there is
/// nothing to bind.
/// </para>
/// </summary>
public static class StatusCommand
{
    private const string SnapshotFileName = "snapshot.json";
    private const string LogFileName = "flow.jsonl";

    /// <summary>
    /// How often <c>--follow</c> re-checks <c>flow.jsonl</c>'s length for growth. A modest,
    /// fixed interval rather than a <see cref="FileSystemWatcher"/> — file-system change
    /// notifications are unreliable across platforms (missed events on some network/CI
    /// filesystems, duplicate events on others), where a length poll on a plain
    /// <see cref="FileInfo"/> always tells the truth.
    /// </summary>
    private const int PollIntervalMs = 500;

    /// <exception cref="SnapshotLoadException">
    /// The task directory has no persisted snapshot — a nonexistent directory and an existing one
    /// that was never started via <c>aer run</c> fail identically here (both are just "no
    /// <c>snapshot.json</c> at this path"), or the persisted snapshot is malformed.
    /// </exception>
    public static async Task ExecuteAsync(
        StatusOptions options, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        var snapshotPath = Path.Combine(options.TaskDirectoryPath, SnapshotFileName);
        var logPath = Path.Combine(options.TaskDirectoryPath, LogFileName);

        // Never Directory.CreateDirectory here (unlike RunCommand): a status probe against a task
        // that was never started must report the same typed failure, not conjure the directory
        // into existence as a side effect of looking at it.
        if (!File.Exists(snapshotPath))
        {
            throw new SnapshotLoadException(
                $"Task directory '{options.TaskDirectoryPath}' has no bound snapshot — 'aer status' " +
                "projects a task 'aer run' has already started, and never binds one fresh.");
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        var reader = new FlowEventLogReader(logPath);
        var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var state = StateProjector.Project(events, snapshot);

        PrintState(output, state, logPath);

        // A task directory whose snapshot is bound but has recorded zero events yet -- never
        // started via `aer run` -- projects as WorkflowStatus.Terminal by StateProjector's own
        // deliberate, already-tested rule (StateProjectorTests.An_all_pending_workflow_projects_WorkflowStatus_Terminal):
        // "nothing running, nothing paused" is trivially true before anything has ever dispatched.
        // So `aer status --follow` against a not-yet-started task prints once and exits immediately
        // rather than waiting for it to begin -- the same fact any other reader of this projection
        // already lives with, not something particular to --follow.
        if (!options.Follow || state.Status == WorkflowStatus.Terminal)
        {
            return;
        }

        await FollowAsync(output, reader, snapshot, events.Count, logPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls <paramref name="logPath"/>'s length for growth, printing every event newer than
    /// <paramref name="printedEventCount"/> as it appears, until re-projecting reaches
    /// <see cref="WorkflowStatus.Terminal"/> or <paramref name="cancellationToken"/> is cancelled.
    /// A cancellation (Ctrl+C, or a host-initiated stop) ends the loop the same way the issue's own
    /// acceptance criteria describe it — a normal way for <c>--follow</c> to end, not a fault — so
    /// it is caught here rather than left to escape into <c>Program</c>'s generic exception mapping.
    /// </summary>
    private static async Task FollowAsync(
        TextWriter output,
        FlowEventLogReader reader,
        WorkflowDefinitionSnapshot snapshot,
        int printedEventCount,
        string logPath,
        CancellationToken cancellationToken)
    {
        // Deliberately not seeded from a fresh FileInfo read here: ExecuteAsync already decided
        // "not terminal yet" from an earlier read of the log, and PrintState's writes to `output`
        // run in between -- when `output` is a piped/redirected Console.Out, a slow downstream
        // reader applies real backpressure there, real wall-clock time, not a nanosecond gap. If
        // the workflow finishes in that window, a baseline captured *now* would already include
        // the final bytes, `currentLength` would never differ from it again, and the loop would
        // poll forever against an already-finished workflow -- the exact hang this command must not
        // produce (regression-tested in StatusCommandEndToEndTests via a TextWriter that blocks
        // there deliberately). A sentinel that can never equal a real length forces the very first
        // poll to always re-read and re-project, bounding that race to one PollIntervalMs rather
        // than leaving it unbounded.
        var lastObservedLength = -1L;

        while (true)
        {
            try
            {
                await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // A fresh FileInfo per poll: an existing instance caches Length at construction time
            // and never observes growth on its own, which would silently turn every poll after the
            // first into a no-op — the loop would never see the file change again.
            var logFile = new FileInfo(logPath);
            var currentLength = logFile.Exists ? logFile.Length : 0;
            if (currentLength == lastObservedLength)
            {
                continue;
            }

            lastObservedLength = currentLength;

            // Re-read the whole log through the one parser for this format rather than hand-rolling
            // an incremental line read here — ReadAllAsync already handles a dangling, not-yet-
            // newline-terminated write in flight (§5.3) correctly, and a second reader of the same
            // format is exactly what CLAUDE.md's record-once gate forbids.
            var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            for (var i = printedEventCount; i < events.Count; i++)
            {
                output.WriteLine(events[i]);
            }

            printedEventCount = events.Count;

            var state = StateProjector.Project(events, snapshot);
            if (state.Status == WorkflowStatus.Terminal)
            {
                output.WriteLine($"Workflow status: {state.Status}");
                return;
            }
        }
    }

    private static void PrintState(TextWriter output, FlowState state, string logPath)
    {
        output.WriteLine($"Workflow status: {state.Status}");
        output.WriteLine($"Log last updated: {ResolveLogUpdatedAt(logPath)}");

        foreach (var step in state.Steps)
        {
            var executionText = step.LatestExecutionId?.ToString() ?? "none";
            output.WriteLine($"  {step.StepId}: {step.Status} (execution={executionText})");
        }

        foreach (var stepLess in state.StepLessExecutions)
        {
            output.WriteLine($"  (supplementary) {stepLess.Worker}: execution={stepLess.ExecutionId} pending");
        }
    }

    /// <summary>
    /// <c>flow.jsonl</c>'s own last-write time (UTC), append-only so this is exactly "when the
    /// last event landed" — the closest honest answer available. Not a per-step value: as
    /// <c>TaskProjectionLoader.ResolveTimestampsAsync</c> already records, "a DAG task carries no
    /// serialized timestamp anywhere ... neither the <c>flow.jsonl</c> line envelope nor any
    /// <see cref="FlowEvent"/> records one", so there is no finer-grained fact to report per step
    /// without adding one to the event schema — tracked as #745 rather than done silently here,
    /// since whether that schema change is even worth making is its own decision. Printed once, at
    /// the whole-log grain it actually has, rather than repeated per step as if it meant something
    /// narrower.
    /// </summary>
    private static string ResolveLogUpdatedAt(string logPath) => File.Exists(logPath)
        ? File.GetLastWriteTimeUtc(logPath).ToString("O")
        : "never (no flow.jsonl yet)";
}
