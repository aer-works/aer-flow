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
        var entries = await reader.ReadAllEntriesWithTimestampsAsync(cancellationToken).ConfigureAwait(false);
        var state = StateProjector.Project(events, snapshot);

        PrintState(output, state, logPath, events, entries);

        if (options.Follow)
        {
            var artifactsDir = Path.Combine(options.TaskDirectoryPath, Aer.Flow.Artifacts.ArtifactManager.ArtifactsDirectoryName);
            TailStreams(output, artifactsDir, new Dictionary<string, long>(StringComparer.Ordinal));
        }

        if (!options.Follow || state.Status == WorkflowStatus.Terminal)
        {
            return;
        }

        await FollowAsync(output, reader, snapshot, events.Count, logPath, options.TaskDirectoryPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls <paramref name="logPath"/>'s length for growth, printing every event newer than
    /// <paramref name="printedEventCount"/> as it appears, until re-projecting reaches
    /// <see cref="WorkflowStatus.Terminal"/> or <paramref name="cancellationToken"/> is cancelled.
    /// Tails stdout/stderr streams of running executions interleaved with event lines.
    /// </summary>
    private static async Task FollowAsync(
        TextWriter output,
        FlowEventLogReader reader,
        WorkflowDefinitionSnapshot snapshot,
        int printedEventCount,
        string logPath,
        string taskDirectoryPath,
        CancellationToken cancellationToken)
    {
        var lastObservedLength = -1L;
        var artifactsDir = Path.Combine(taskDirectoryPath, Aer.Flow.Artifacts.ArtifactManager.ArtifactsDirectoryName);
        var streamOffsets = new Dictionary<string, long>(StringComparer.Ordinal);

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

            var logFile = new FileInfo(logPath);
            var currentLength = logFile.Exists ? logFile.Length : 0;

            if (currentLength != lastObservedLength)
            {
                lastObservedLength = currentLength;

                var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
                for (var i = printedEventCount; i < events.Count; i++)
                {
                    output.WriteLine(events[i]);
                }

                printedEventCount = events.Count;

                var state = StateProjector.Project(events, snapshot);
                TailStreams(output, artifactsDir, streamOffsets);

                if (state.Status == WorkflowStatus.Terminal)
                {
                    output.WriteLine($"Workflow status: {state.Status}");
                    return;
                }
            }
            else
            {
                TailStreams(output, artifactsDir, streamOffsets);
            }
        }
    }

    // Public as a test seam, matching FormatStepStatus and EscapeNonPrintable: the reader-side
    // rollover behavior is asserted directly (the lane review's medium finding).
    public static void TailStreams(TextWriter output, string artifactsDir, Dictionary<string, long> streamOffsets)
    {
        if (!Directory.Exists(artifactsDir))
        {
            return;
        }

        foreach (var execDir in Directory.GetDirectories(artifactsDir, "execution_*"))
        {
            TailStreamFile(
                output,
                Path.Combine(execDir, Aer.Flow.Dispatch.ExecutionStreamLogger.StdoutLogFileName),
                Path.Combine(execDir, Aer.Flow.Dispatch.ExecutionStreamLogger.StdoutRolloverFileName),
                streamOffsets);

            TailStreamFile(
                output,
                Path.Combine(execDir, Aer.Flow.Dispatch.ExecutionStreamLogger.StderrLogFileName),
                Path.Combine(execDir, Aer.Flow.Dispatch.ExecutionStreamLogger.StderrRolloverFileName),
                streamOffsets);
        }
    }

    private static void TailStreamFile(TextWriter output, string logPath, string rolloverPath, Dictionary<string, long> streamOffsets)
    {
        if (!File.Exists(logPath))
        {
            return;
        }

        streamOffsets.TryGetValue(logPath, out var offset);

        // Rollover detection keys on the rollover FILE'S identity (its mtime advances every time
        // the writer rolls), never on a length comparison: a fresh file whose length equals the
        // stored offset made `length < offset` miss the rollover entirely and silently drop the
        // new content -- found by the reader-side test the lane review demanded. The rollover
        // path doubles as its own dict key; log and rollover paths are distinct strings.
        if (File.Exists(rolloverPath))
        {
            streamOffsets.TryGetValue(rolloverPath, out var seenRolloverTicks);
            var rolloverFi = new FileInfo(rolloverPath);
            var ticks = rolloverFi.LastWriteTimeUtc.Ticks;
            if (ticks != seenRolloverTicks)
            {
                // The rolled file IS the previous current file: emit its unseen tail, then the
                // fresh file reads from the start.
                if (rolloverFi.Length > offset)
                {
                    ReadAndOutputBytes(output, rolloverPath, offset, rolloverFi.Length - offset);
                }

                offset = 0;
                streamOffsets[rolloverPath] = ticks;
            }
        }

        var fi = new FileInfo(logPath);
        if (fi.Length > offset)
        {
            var bytesRead = ReadAndOutputBytes(output, logPath, offset, fi.Length - offset);
            offset += bytesRead;
        }

        streamOffsets[logPath] = offset;
    }

    private static long ReadAndOutputBytes(TextWriter output, string path, long offset, long count)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[count];
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = fs.Read(buffer, totalRead, (int)(count - totalRead));
                if (read <= 0) break;
                totalRead += read;
            }

            if (totalRead > 0)
            {
                var escaped = EscapeNonPrintable(buffer.AsSpan(0, totalRead));
                output.Write(escaped);
            }

            return totalRead;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    public static string EscapeNonPrintable(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(bytes.Length);
        var decoder = System.Text.Encoding.UTF8.GetDecoder();
        var chars = new char[2];

        for (int i = 0; i < bytes.Length;)
        {
            int bytesUsed, charsUsed;
            bool completed;
            decoder.Convert(bytes.Slice(i, 1).ToArray(), 0, 1, chars, 0, 2, false, out bytesUsed, out charsUsed, out completed);

            if (charsUsed > 0)
            {
                for (int c = 0; c < charsUsed; c++)
                {
                    var ch = chars[c];
                    if (ch is '\n' or '\t' || IsPrintable(ch))
                    {
                        sb.Append(ch);
                    }
                    else
                    {
                        var code = (ushort)ch;
                        if (code <= 0xFF)
                        {
                            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"\\x{code:x2}");
                        }
                        else
                        {
                            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"\\x{code:x4}");
                        }
                    }
                }

                i += bytesUsed;
            }
            else
            {
                // charsUsed == 0 with the byte consumed means the decoder BUFFERED a valid-so-far
                // lead/continuation byte of a multi-byte sequence -- not an invalid byte. Emitting
                // an escape here duplicated every non-ASCII character as \xNN + the decoded char
                // (the lane review's high finding). Advance silently; the decoder produces the
                // character when the sequence completes, and the flush below drains a sequence
                // truncated at end-of-input as U+FFFD (genuinely invalid bytes already surface as
                // U+FFFD through the decoder's replacement fallback).
                i++;
            }
        }

        var flushed = new char[2];
        decoder.Convert([], 0, 0, flushed, 0, 2, flush: true, out _, out var flushedChars, out _);
        for (int c = 0; c < flushedChars; c++)
        {
            sb.Append(flushed[c]);
        }

        return sb.ToString();
    }

    private static bool IsPrintable(char ch)
    {
        if (ch == ' ') return true;
        var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
        return cat is not (System.Globalization.UnicodeCategory.Control
            or System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.Surrogate
            or System.Globalization.UnicodeCategory.PrivateUse
            or System.Globalization.UnicodeCategory.OtherNotAssigned
            or System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator
            or System.Globalization.UnicodeCategory.SpaceSeparator);
    }

    private static void PrintState(
        TextWriter output, FlowState state, string logPath, IReadOnlyList<FlowEvent> events, IReadOnlyList<LogEntry> entries)
    {
        output.WriteLine($"Workflow status: {state.Status}");
        output.WriteLine($"Log last updated: {ResolveLogUpdatedAt(logPath)}");

        var eventTimestamps = ExtractEventTimestamps(entries);

        foreach (var step in state.Steps)
        {
            var executionText = step.LatestExecutionId?.ToString() ?? "none";
            var statusText = FormatStepStatus(step, events);
            var timeText = step.LatestExecutionId is not null && eventTimestamps.TryGetValue(step.LatestExecutionId.Value.Value, out var time)
                ? $" @ {time:O}"
                : string.Empty;
            output.WriteLine($"  {step.StepId}: {statusText} (execution={executionText}{timeText})");
        }

        foreach (var stepLess in state.StepLessExecutions)
        {
            output.WriteLine($"  (supplementary) {stepLess.Worker}: execution={stepLess.ExecutionId} pending");
        }
    }

    private static Dictionary<string, DateTime> ExtractEventTimestamps(IReadOnlyList<LogEntry> entries)
    {
        var timestamps = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            string? execId = null;
            DateTime? timestamp = null;

            switch (entry)
            {
                case LogEntry.FlowLogEntry flowEntry:
                    timestamp = flowEntry.WriterUtcTimestamp;
                    if (flowEntry.Event is FlowEvent.ExecutionRequestAccepted accepted)
                    {
                        execId = accepted.Request.ExecutionId.Value;
                    }
                    break;
                case LogEntry.CoreLogEntry coreEntry:
                    timestamp = coreEntry.WriterUtcTimestamp;
                    if (coreEntry.Event is CoreEvent.ExecutionStarted started)
                    {
                        execId = started.ExecutionId.Value;
                    }
                    break;
            }

            if (execId is not null && timestamp.HasValue)
            {
                timestamps[execId] = timestamp.Value;
            }
        }

        return timestamps;
    }

    public static string FormatStepStatus(StepState step, IReadOnlyList<FlowEvent> events)
    {
        // Probe ONLY steps claiming a live engine. Paused is a mask over an already-terminal
        // outcome (StateProjector) -- its engine has legitimately exited, and probing it stamped
        // every healthy paused step "crash recovery will classify" (the lane review's high
        // finding). Pending has no execution yet, so no liveness claim applies there either.
        if (step.Status is not StepStatus.Running)
        {
            return step.Status.ToString();
        }

        if (step.LatestExecutionId is null)
        {
            return step.Status.ToString();
        }

        var accepted = events.OfType<FlowEvent.ExecutionRequestAccepted>()
            .FirstOrDefault(e => e.Request.ExecutionId == step.LatestExecutionId);

        var probeResult = EngineLivenessProbe.Probe(accepted?.EnginePid, accepted?.EngineStartTime);

        return probeResult.Status switch
        {
            EngineLivenessStatus.Alive => step.Status.ToString(),
            EngineLivenessStatus.Dead => $"{step.Status} — engine not alive; crash recovery will classify on next pump",
            EngineLivenessStatus.Unknown => $"liveness unknown ({probeResult.Why})",
            _ => $"liveness unknown ({probeResult.Why})",
        };
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
