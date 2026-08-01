using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Templates;

namespace Aer.Cli;

/// <summary>
/// <c>aer dispatch &lt;role&gt;</c> (#900, front-door rung 2): the first consumer of the worker-role
/// catalog. It materializes one role plus a task spec into a single-step workflow via
/// <see cref="RoleDispatch"/>, persists the same <c>workflow.json</c>/<c>bindings.json</c> a template
/// run would, and hands them to <see cref="RunCommand.ExecuteAsync"/> — so the outputs the role
/// declares are contract-checked by the very pump <c>aer run</c> drives, and the reporter prints their
/// paths on success or the failure reason on a no-op. No second validation is bolted on: the engine
/// already treats an unsatisfied contract as a failed execution.
/// </summary>
public static class DispatchCommand
{
    private const string WorkflowFileName = "workflow.json";
    private const string BindingsFileName = "bindings.json";

    /// <exception cref="CliArgumentException">
    /// <paramref name="options"/> names a role the catalog does not define, or a spec file that does
    /// not exist, or the catalog itself is unreadable — every catalog-resolution failure is translated
    /// so it exits cleanly through <c>Program</c>'s typed boundary rather than as a raw stack trace.
    /// </exception>
    public static async Task<CommandResult> ExecuteAsync(
        DispatchOptions options,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);

        WorkerRole role;
        try
        {
            role = WorkerRoleCatalog.For(options.RoleId);
        }
        // dispatch is the first CLI verb to touch the catalog, so it is the first to meet its fail-loud
        // set: an unknown role id (KeyNotFoundException), a missing catalog file (FileNotFoundException),
        // malformed JSON (JsonException), or a structural fault — duplicate id, undefined tier, empty or
        // ill-formed outputs (InvalidOperationException). None derive from AerFlowException, so without
        // this they escape Program's boundary as a 127 crash rather than the clean exit this promises.
        catch (Exception ex) when (ex is KeyNotFoundException or FileNotFoundException or JsonException or InvalidOperationException)
        {
            throw new CliArgumentException(ex.Message);
        }

        if (!File.Exists(options.SpecFilePath))
        {
            throw new CliArgumentException($"Spec file '{options.SpecFilePath}' does not exist.");
        }

        var spec = await File.ReadAllTextAsync(options.SpecFilePath, cancellationToken).ConfigureAwait(false);

        var (definition, bindings) = RoleDispatch.Materialize(role, spec, options.Adapter);

        Directory.CreateDirectory(options.TaskDirectoryPath);
        var workflowFilePath = Path.Combine(options.TaskDirectoryPath, WorkflowFileName);
        var bindingsFilePath = Path.Combine(options.TaskDirectoryPath, BindingsFileName);
        await WorkflowDefinitionWriter.SaveToFileAsync(definition, workflowFilePath, cancellationToken).ConfigureAwait(false);
        await WorkerBindingConfigWriter.SaveToFileAsync(bindings, bindingsFilePath, cancellationToken).ConfigureAwait(false);

        var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, options.TaskDirectoryPath, options.WorkflowId);
        return await RunCommand.ExecuteAsync(runOptions, adapters, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
