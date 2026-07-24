using System.Text.Json;

namespace Aer.Adapters;

/// <summary>
/// What a migration run did. <see cref="Ran"/> is false when there was nothing to do -- either the
/// completion marker was already present or no legacy root existed -- which is the steady state
/// after the first run.
/// </summary>
public sealed record LegacyStorageMigrationResult(
    bool Ran,
    int RecordsMigrated,
    IReadOnlyList<string> Destinations);

/// <summary>
/// The <c>~/.aer/tasks</c> -> <c>~/.aer/sessions</c> fold (#333). Decision 0001 deletes "task" from
/// the product and defines <b>session</b> as the running instance of a workflow, so the two parallel
/// roots become one: a DAG run is a session whose workflow is an authored pipeline, exactly as a chat
/// is a session whose workflow is the conversation shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>Copy, never move.</b> Nothing under the legacy root is deleted or modified, ever. A failed or
/// half-finished migration therefore cannot lose data, and reversal is simply pointing back at
/// <c>tasks/</c>. The cost is transient duplication of a directory tree that is kilobytes in
/// practice -- a trade that is not close, given the alternative destroys the user's real history.
/// </para>
/// <para>
/// <b>Why a plan file rather than deciding destinations as we go.</b> A legacy task and an existing
/// session may share a folder name, so some records need a suffixed destination. Recomputing that
/// choice on every run is not idempotent: after a partially-completed run the destination now
/// exists, so a naive "is the name free?" test would pick a *different*, doubly-suffixed name the
/// second time and scatter half-copies. Worse, a re-run that decided to overwrite the colliding name
/// could destroy a genuine pre-existing session. So the source -> destination mapping is computed
/// once against the original state, persisted, and reused verbatim by every later run. Overwriting
/// within a planned destination is then always safe: it can only ever be this migration's own
/// partial output.
/// </para>
/// <para>
/// <b>Ordering.</b> Plan, then copy, then verify, then mark. The completion marker is written only
/// after every source file has been confirmed present at its destination, so an interruption at any
/// point leaves the marker absent and the next run resumes. The operation is idempotent by
/// construction rather than by a flag.
/// </para>
/// <para>
/// <b>#322's <c>created</c> timestamp.</b> A DAG run's creation time is derived from the
/// <c>snapshot.json</c> file mtime, which is correct only because that file is written once and never
/// mutated (spec §11.2). A plain copy would stamp every migrated record with the migration's own
/// wall-clock time and silently rewrite user-visible history. Last-write times are therefore restored
/// explicitly on every copied file -- not left to <c>File.Copy</c>'s platform-dependent behaviour.
/// </para>
/// </remarks>
public static class LegacyStorageMigration
{
    /// <summary>
    /// Written under <see cref="AerPaths.Root"/> once a migration has fully completed and verified.
    /// Its presence is the fast path: later runs return without touching the filesystem.
    /// </summary>
    public const string CompletionMarkerFileName = ".sessions-unified";

    /// <summary>
    /// The persisted source -> destination mapping, written before the first byte is copied and
    /// reused by every subsequent run. See the type remarks for why this cannot be recomputed.
    /// </summary>
    public const string PlanFileName = ".sessions-unified.plan.json";

    /// <summary>
    /// Suffix appended when a legacy task's folder name is already taken by a session. Applied with
    /// an ordinal counter if even the suffixed name collides.
    /// </summary>
    private const string CollisionSuffix = "-workflow";

    private static readonly JsonSerializerOptions PlanJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Folds the legacy <c>tasks</c> root into <c>sessions</c>, resuming any interrupted previous
    /// run. Safe to call unconditionally on every startup.
    /// </summary>
    public static Task<LegacyStorageMigrationResult> RunAsync(CancellationToken cancellationToken = default) =>
        RunAsync(AerPaths.Root, cancellationToken);

    /// <summary>
    /// Migrates a specific root. Takes the root explicitly rather than reading
    /// <see cref="AerPaths.Root"/> so this is exercisable without mutating the process-wide
    /// <c>AER_HOME</c>: that variable is global state, and tests that swap it cannot run alongside
    /// anything else reading a path. Every test here gets its own throwaway root instead.
    /// </summary>
    public static async Task<LegacyStorageMigrationResult> RunAsync(string root, CancellationToken cancellationToken = default)
    {
        var markerPath = Path.Combine(root, CompletionMarkerFileName);
        if (File.Exists(markerPath))
        {
            return new LegacyStorageMigrationResult(Ran: false, RecordsMigrated: 0, Destinations: []);
        }

        var legacyRoot = Path.Combine(root, AerPaths.LegacyTasksDirectoryName);
        var sessionsRoot = Path.Combine(root, AerPaths.SessionsDirectoryName);

        // Nothing to fold: record that so later startups take the fast path above rather than
        // re-scanning a directory that will never exist.
        if (!Directory.Exists(legacyRoot))
        {
            await WriteMarkerAsync(markerPath, migrated: 0, cancellationToken).ConfigureAwait(false);
            return new LegacyStorageMigrationResult(Ran: false, RecordsMigrated: 0, Destinations: []);
        }

        var plan = await LoadOrCreatePlanAsync(root, legacyRoot, sessionsRoot, cancellationToken).ConfigureAwait(false);
        if (plan.Count == 0)
        {
            await WriteMarkerAsync(markerPath, migrated: 0, cancellationToken).ConfigureAwait(false);
            return new LegacyStorageMigrationResult(Ran: false, RecordsMigrated: 0, Destinations: []);
        }

        Directory.CreateDirectory(sessionsRoot);

        foreach (var (source, destination) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyDirectory(source, destination, cancellationToken);
        }

        // Verify before marking. An unverified marker would permanently skip a migration that
        // silently dropped files -- the stale-and-unchecked failure this milestone exists to kill.
        foreach (var (source, destination) in plan)
        {
            VerifyCopied(source, destination);
        }

        await WriteMarkerAsync(markerPath, plan.Count, cancellationToken).ConfigureAwait(false);

        // The plan has served its purpose; the marker now short-circuits every future run. Removing
        // it keeps the root tidy, and its absence can no longer cause a recompute.
        var planPath = Path.Combine(root, PlanFileName);
        if (File.Exists(planPath))
        {
            File.Delete(planPath);
        }

        return new LegacyStorageMigrationResult(
            Ran: true,
            RecordsMigrated: plan.Count,
            Destinations: plan.Select(entry => entry.Value).ToArray());
    }

    /// <summary>
    /// Reads the persisted plan, or computes and persists one against the current state. Reuse is
    /// what makes an interrupted run resumable rather than destructive -- see the type remarks.
    /// </summary>
    private static async Task<Dictionary<string, string>> LoadOrCreatePlanAsync(
        string root, string legacyRoot, string sessionsRoot, CancellationToken cancellationToken)
    {
        var planPath = Path.Combine(root, PlanFileName);
        if (File.Exists(planPath))
        {
            var existingJson = await File.ReadAllTextAsync(planPath, cancellationToken).ConfigureAwait(false);
            var existing = JsonSerializer.Deserialize<Dictionary<string, string>>(existingJson);
            if (existing is { Count: > 0 })
            {
                return existing;
            }
        }

        var plan = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(sessionsRoot))
        {
            foreach (var existingSession in Directory.GetDirectories(sessionsRoot))
            {
                taken.Add(Path.GetFileName(existingSession));
            }
        }

        foreach (var source in Directory.GetDirectories(legacyRoot).OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(source);
            var candidate = name;
            if (taken.Contains(candidate))
            {
                candidate = name + CollisionSuffix;
                var ordinal = 2;
                while (taken.Contains(candidate))
                {
                    candidate = $"{name}{CollisionSuffix}-{ordinal}";
                    ordinal++;
                }
            }

            taken.Add(candidate);
            plan[source] = Path.Combine(sessionsRoot, candidate);
        }

        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            planPath, JsonSerializer.Serialize(plan, PlanJsonOptions), cancellationToken).ConfigureAwait(false);
        return plan;
    }

    /// <summary>
    /// Recursive copy that preserves each file's last-write time. Overwrites at the destination,
    /// which is safe because destinations come from the persisted plan and so can only be this
    /// migration's own partial output (type remarks).
    /// </summary>
    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);

            // Explicit, not incidental: #322 reads a DAG run's `created` off snapshot.json's mtime,
            // so letting the copy restamp it would rewrite user-visible history.
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), cancellationToken);
        }
    }

    /// <summary>
    /// Confirms every source file reached the destination at the same length. Throws rather than
    /// returning a bool: a verification failure must stop the marker being written, and swallowing it
    /// would make the next startup skip a migration that lost data.
    /// </summary>
    private static void VerifyCopied(string source, string destination)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            if (!File.Exists(target))
            {
                throw new InvalidOperationException(
                    $"Migration verification failed: '{relative}' from '{source}' is missing at '{destination}'. " +
                    "The legacy directory is untouched; no completion marker was written, so the next start retries.");
            }

            var sourceLength = new FileInfo(file).Length;
            var targetLength = new FileInfo(target).Length;
            if (sourceLength != targetLength)
            {
                throw new InvalidOperationException(
                    $"Migration verification failed: '{relative}' is {targetLength} bytes at '{destination}' but " +
                    $"{sourceLength} bytes at '{source}'. The legacy directory is untouched; no completion marker " +
                    "was written, so the next start retries.");
            }
        }
    }

    private static async Task WriteMarkerAsync(string markerPath, int migrated, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        await File.WriteAllTextAsync(
            markerPath,
            $"Unified sessions/tasks storage (#333) at {DateTimeOffset.UtcNow:O}; {migrated} record(s) migrated. " +
            "The legacy 'tasks' directory was copied, never moved, and can be removed by hand once you are satisfied.",
            cancellationToken).ConfigureAwait(false);
    }
}
