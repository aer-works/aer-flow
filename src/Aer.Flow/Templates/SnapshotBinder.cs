using System.Text.Json;
using Aer.Flow.Domain;

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

    /// <summary>Persists <paramref name="snapshot"/> as JSON at <paramref name="snapshotFilePath"/>, creating parent directories as needed.</summary>
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

        var json = JsonSerializer.Serialize(snapshot);
        await File.WriteAllTextAsync(snapshotFilePath, json, cancellationToken).ConfigureAwait(false);
    }
}
