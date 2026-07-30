using System.Text.Json;
using Aer.Flow.Domain;
using Aer.Flow.Store;

namespace Aer.Flow.Templates;

/// <summary>
/// Freezes a validated <see cref="WorkflowDefinition"/> template into an immutable
/// <see cref="WorkflowDefinitionSnapshot"/> at task creation (spec §11.2), and persists it
/// alongside the task's log directory. Once bound and persisted, later edits to the source
/// template file — or even the file being deleted — have no effect on the snapshot: binding
/// copies every field the snapshot needs out of the in-memory <see cref="WorkflowDefinition"/>
/// and never re-reads the source afterward.
/// </summary>
public static class SnapshotBinder
{
    /// <exception cref="WorkflowDefinitionValidationException">
    /// <paramref name="definition"/> fails structural validation. Re-validated here (in addition
    /// to whatever validation <see cref="WorkflowDefinitionParser"/> already performed) because
    /// <see cref="Bind"/> is a public entry point on its own and must not freeze an invalid
    /// definition just because it was constructed in-memory rather than parsed from a file.
    /// </exception>
    public static WorkflowDefinitionSnapshot Bind(WorkflowDefinition definition)
    {
        WorkflowDefinitionValidator.Validate(definition);

        return new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId(Guid.NewGuid().ToString("n")),
            definition.WorkflowTemplateId,
            definition.WorkflowTemplateVersion,
            definition.Steps);
    }

    /// <summary>Bounded retry budget for a rename that loses a transient sharing violation (see remarks).</summary>
    private const int MaxRenameAttempts = 5;

    /// <summary>
    /// Persists <paramref name="snapshot"/> as JSON at <paramref name="snapshotFilePath"/>, creating
    /// parent directories as needed.
    /// </summary>
    /// <remarks>
    /// Writes to a <c>{snapshotFilePath}.{guid}.tmp</c> sibling in the same directory, flushes it,
    /// then <see cref="File.Move(string, string, bool)"/>s it onto <paramref name="snapshotFilePath"/>
    /// -- a same-volume rename, atomic on both Windows and POSIX (same shape as
    /// <c>Aer.Adapters.AtomicLaunchConfigWriter</c>, applied there to worker launch configs).
    /// Without this, a concurrent reader can observe <c>File.Exists == true</c> with partial JSON on
    /// disk while a direct write is still in flight (#818).
    /// <para>
    /// <b>The rename is retried, unlike that writer's retry which guards concurrent writers:</b> this
    /// method has no concurrent-writer race (a snapshot is bound and persisted exactly once per task)
    /// but does have a writer-vs-reader one, and measurement while building this fix's own race test
    /// showed Windows' overwrite-rename needs the destination briefly exclusive: a reader with
    /// <paramref name="snapshotFilePath"/> open at that instant (e.g. <see cref="LoadFromFileAsync"/>'s
    /// <see cref="File.ReadAllTextAsync(string, CancellationToken)"/>, default <c>FileShare.Read</c>)
    /// makes <see cref="File.Move(string, string, bool)"/> throw <see cref="UnauthorizedAccessException"/>
    /// -- a transient sharing violation, not a real failure, so it is retried with a short backoff
    /// rather than surfaced.
    /// </para>
    /// </remarks>
    public static async Task PersistAsync(
        WorkflowDefinitionSnapshot snapshot,
        string snapshotFilePath,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(snapshotFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(snapshot, SnapshotJson.Options);
        var tempPath = $"{snapshotFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, snapshotFilePath, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= MaxRenameAttempts)
                    {
                        throw;
                    }

                    await Task.Delay(10 * attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Best-effort: a leftover .tmp file is invisible to every reader (none of them glob the
            // directory, all look up the exact snapshot file name) and far smaller a problem than
            // masking the real exception -- same choice AtomicLaunchConfigWriter makes.
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
            }

            throw;
        }
    }

    /// <summary>
    /// Reads back a snapshot persisted by <see cref="PersistAsync"/> — how a resumed <c>aer run</c>
    /// (§21) re-derives the exact frozen template a task was created from, rather than re-parsing
    /// and re-binding the source workflow file a second time (which would mint a new, unrelated
    /// <see cref="WorkflowDefinitionSnapshotId"/> and, per this type's own remarks, be unaffected by
    /// what binding already froze anyway).
    /// </summary>
    /// <exception cref="SnapshotLoadException">The file is malformed or empty.</exception>
    public static async Task<WorkflowDefinitionSnapshot> LoadFromFileAsync(
        string snapshotFilePath, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(snapshotFilePath, cancellationToken).ConfigureAwait(false);

        WorkflowDefinitionSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<WorkflowDefinitionSnapshot>(json, SnapshotJson.Options);
        }
        catch (JsonException ex)
        {
            throw new SnapshotLoadException($"Malformed snapshot JSON at '{snapshotFilePath}': {ex.Message}", ex);
        }

        if (snapshot is null)
        {
            throw new SnapshotLoadException($"Snapshot file '{snapshotFilePath}' did not contain a WorkflowDefinitionSnapshot object.");
        }

        return snapshot;
    }
}
