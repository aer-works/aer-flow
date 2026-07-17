using Aer.Flow.Domain;
using Aer.Flow.Templates;

namespace Aer.Ui.Core;

/// <summary>
/// Opens a raw, not-yet-instantiated <see cref="WorkflowDefinition"/> template file directly —
/// the DAG view's other input alongside <see cref="TaskProjectionLoader"/>'s bound-task path (UI
/// spec §5, §10; issue #120). Reuses <see cref="WorkflowDefinitionParser.LoadFromFileAsync"/>
/// exactly as Flow's own write path (template loading ahead of <c>SnapshotBinder.Bind</c>) does —
/// never a second parser — the same "consume the read model as a library" seam
/// <see cref="TaskProjectionLoader"/> established for snapshots (M14 Phase 1, issue #118).
/// </summary>
public static class TemplateProjectionLoader
{
    /// <exception cref="WorkflowDefinitionValidationException">
    /// <paramref name="templateFilePath"/> does not exist, or its contents fail to parse or
    /// structurally validate as a <see cref="WorkflowDefinition"/>.
    /// </exception>
    public static async Task<WorkflowDefinition> LoadAsync(
        string templateFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(templateFilePath);

        if (!File.Exists(templateFilePath))
        {
            throw new WorkflowDefinitionValidationException([$"No such template file: '{templateFilePath}'"]);
        }

        return await WorkflowDefinitionParser.LoadFromFileAsync(templateFilePath, cancellationToken)
            .ConfigureAwait(false);
    }
}
