using Aer.Flow.Domain;

namespace Aer.Adapters;

/// <summary>
/// Materializes a single worker <see cref="WorkerRole"/> from the catalog into the
/// <see cref="WorkflowDefinition"/> + <see cref="WorkerBindingConfigEntry"/> the engine runs — the
/// shared primitive behind <c>aer dispatch &lt;role&gt;</c> (#900, front-door rung 2). It is the one
/// place that turns "what a role produces" (its <see cref="WorkerRole.Outputs"/>) into a
/// <see cref="WorkerContract"/> the engine's <c>ContractValidator</c> enforces, so a role that writes
/// nothing fails loudly without the caller restating the contract.
/// </summary>
/// <remarks>
/// Deliberately surface-agnostic — it takes catalog and domain types only, never a CLI or UI type —
/// so the built-in templates and the desktop's authoring both adopt it in place of their own
/// hand-rolled bindings (#901), retiring the parallel <c>BuiltInWorkflowTemplates</c> source of truth.
/// Until then this is the catalog's only consumer.
/// </remarks>
public static class RoleDispatch
{
    /// <summary>
    /// The reusable core: a resolved role plus a task spec become one worker binding whose contract's
    /// <c>ProducedOutputs</c> are exactly the role's declared outputs, whose grant/timeout/model/effort
    /// come from the role, and whose prompt is the spec with the role's output instructions appended —
    /// single-sourced from the catalog so a spec prompt stays just the task.
    /// </summary>
    /// <param name="role">The resolved catalog role (see <see cref="WorkerRoleCatalog.For"/>).</param>
    /// <param name="spec">The task prompt for this dispatch — what the worker is asked to do.</param>
    /// <param name="adapterOverride">
    /// A vendor adapter to run this role on instead of its tier's default (<see cref="WorkerRole.Adapter"/>) —
    /// the <c>--adapter</c> escape hatch. A role never names a vendor, so this is the only place a
    /// caller picks one, and it does not change the role's capability.
    /// </param>
    public static WorkerBindingConfigEntry ToBinding(WorkerRole role, string spec, string? adapterOverride = null)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(spec);

        var contract = new WorkerContract(
            WorkerName: role.Id,
            RequiredInputs: [],
            ProducedOutputs: role.Outputs.Select(o => new ProducedOutput(o.Name, Schema: o.Schema)).ToList(),
            OptionalMetadata: []);

        // Normalize whichever adapter wins, not just the CLI override: role.Adapter comes from the
        // operator-editable, rebuild-free WorkerTiers.json, so a tier authored as "Claude" must resolve
        // the same as the override path does — otherwise the binding fails with UnknownWorkerAdapterException
        // for an adapter that plainly exists.
        var adapter = (string.IsNullOrWhiteSpace(adapterOverride) ? role.Adapter : adapterOverride)
            .Trim().ToLowerInvariant();

        return new WorkerBindingConfigEntry(
            Adapter: adapter,
            Contract: contract,
            PromptTemplate: BuildPrompt(role, spec),
            Timeout: role.Timeout,
            Model: role.Model,
            PermissionGrant: role.Grant,
            Effort: role.Effort);
    }

    /// <summary>
    /// Wraps <see cref="ToBinding"/> in a single-step workflow — the shape <c>aer dispatch</c> hands to
    /// the same pump <c>aer run</c> drives. The step's <see cref="WorkflowStepDefinition.Outputs"/>
    /// mirror the contract's, so the reporter prints each produced file's path on success.
    /// </summary>
    public static (WorkflowDefinition Definition, IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings) Materialize(
        WorkerRole role, string spec, string? adapterOverride = null)
    {
        ArgumentNullException.ThrowIfNull(role);

        var binding = ToBinding(role, spec, adapterOverride);

        var definition = new WorkflowDefinition(
            WorkflowTemplateId: new WorkflowTemplateId($"dispatch-{role.Id}"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(
                    StepId: new StepId(role.Id),
                    Worker: role.Id,
                    Inputs: [],
                    Outputs: role.Outputs.Select(o => o.Name).ToList(),
                    DependsOn: [],
                    RetryPolicy: new RetryPolicy(3),
                    PausePoint: null)
            ]);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry> { [role.Id] = binding };
        return (definition, bindings);
    }

    /// <summary>
    /// The spec, then the role's output instructions verbatim — so the worker is told to produce
    /// exactly the files the contract asserts. A role always declares at least one output (the catalog
    /// enforces it at load), so the header is never emitted without lines under it.
    /// </summary>
    private static string BuildPrompt(WorkerRole role, string spec)
    {
        var instructions = string.Join("\n", role.Outputs.Select(o => $"- {o.Instruction}"));
        return $"{spec.TrimEnd()}\n\nRequired outputs:\n{instructions}\n";
    }
}
