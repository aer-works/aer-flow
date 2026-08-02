using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aer.Adapters;

/// <summary>
/// A phase within a <see cref="WorkflowTemplate"/> — names a worker role to run, along with phase-specific
/// instruction prose and an optional approval gate toggle.
/// </summary>
/// <param name="RoleId">The worker role to run for this phase (must resolve against <see cref="WorkerRoleCatalog"/>).</param>
/// <param name="Instruction">The prose body / instruction for this phase.</param>
/// <param name="AskFirst">Per-step gate toggle (decision 0025) — whether to prompt the operator before executing.</param>
public sealed record WorkflowTemplatePhase(string RoleId, string Instruction, bool AskFirst);

/// <summary>
/// A reusable workflow template definition composed as data over the existing worker-role catalog.
/// </summary>
/// <param name="Id">The unique identifier of the workflow template.</param>
/// <param name="Phases">The ordered list of phases that make up the workflow template.</param>
/// <param name="Inputs">The list of inputs required by the workflow template (must be from the closed set).</param>
public sealed record WorkflowTemplate(string Id, IReadOnlyList<WorkflowTemplatePhase> Phases, IReadOnlyList<string> Inputs);

/// <summary>
/// The runtime-resolved catalog of workflow templates.
/// </summary>
public static class WorkflowTemplateCatalog
{
    public const string TemplatesPathEnvironmentVariable = "AER_WORKFLOW_TEMPLATES_PATH";

    private const string TemplatesDefaultFileName = "WorkflowTemplates.json";
    private const string TemplatesOverrideFileName = "workflow-templates.json";

    // Plain JSON only — no comments, no trailing commas.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // The engine-defined, closed set of valid inputs (decision 0047). New members are added here by the engine,
    // never supplied by a template author.
    private static readonly HashSet<string> ClosedInputs = new(StringComparer.Ordinal)
    {
        "diff-of-work-so-far",
    };

    /// <summary>Every workflow template in the catalog, in file order.</summary>
    public static IReadOnlyList<WorkflowTemplate> All => Load();

    /// <summary>The workflow template with <paramref name="id"/>, or throws if the catalog has no such template.</summary>
    public static WorkflowTemplate For(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException(
                $"No workflow template '{id}' in the catalog. Known templates: {string.Join(", ", All.Select(t => t.Id))}.");
    }

    private static IReadOnlyList<WorkflowTemplate> Load()
    {
        var rawTemplates = ReadJson<List<RawTemplate>>(
            ResolvePath(TemplatesPathEnvironmentVariable, TemplatesOverrideFileName, TemplatesDefaultFileName), "template list");

        if (rawTemplates.Count == 0)
        {
            throw new InvalidOperationException("The workflow-template catalog is empty.");
        }

        var knownRoleIds = WorkerRoleCatalog.All.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var templates = new List<WorkflowTemplate>(rawTemplates.Count);

        foreach (var raw in rawTemplates)
        {
            if (!seen.Add(raw.Id))
            {
                throw new InvalidOperationException($"Duplicate workflow template id '{raw.Id}' in the catalog.");
            }

            if (raw.Phases is null || raw.Phases.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Workflow template '{raw.Id}' declares no phases. Every template must contain at least one phase.");
            }

            var phases = new List<WorkflowTemplatePhase>(raw.Phases.Count);
            foreach (var rawPhase in raw.Phases)
            {
                if (!knownRoleIds.Contains(rawPhase.RoleId))
                {
                    throw new InvalidOperationException(
                        $"Workflow template '{raw.Id}' phase names role '{rawPhase.RoleId}', which is not defined in the worker-role catalog. " +
                        $"Known roles: {string.Join(", ", WorkerRoleCatalog.All.Select(r => r.Id))}.");
                }

                phases.Add(new WorkflowTemplatePhase(rawPhase.RoleId, rawPhase.Instruction, rawPhase.AskFirst));
            }

            if (raw.Inputs is null)
            {
                throw new InvalidOperationException($"Workflow template '{raw.Id}' declares null inputs.");
            }

            foreach (var input in raw.Inputs)
            {
                if (!ClosedInputs.Contains(input))
                {
                    throw new InvalidOperationException(
                        $"Workflow template '{raw.Id}' declares unknown input '{input}'. " +
                        $"Known inputs: {string.Join(", ", ClosedInputs)}.");
                }
            }

            templates.Add(new WorkflowTemplate(raw.Id, phases, raw.Inputs));
        }

        return templates;
    }

    private static string ResolvePath(string envVar, string overrideFileName, string defaultFileName)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        var configOverride = Path.Combine(AerPaths.Root, overrideFileName);
        return File.Exists(configOverride)
            ? configOverride
            : Path.Combine(AppContext.BaseDirectory, defaultFileName);
    }

    private static T ReadJson<T>(string path, string what)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The workflow-template catalog's {what} was not found at '{path}'. The default ships next to " +
                "the engine; an override lives under AER_HOME or the AER_WORKFLOW_TEMPLATES_PATH env var.", path);
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"The workflow-template catalog's {what} at '{path}' parsed to null.");
    }

    // Every field is [JsonRequired]: a missing member would otherwise deserialize to its default
    // (false / null) and silently ship a template nobody authored — a dropped phase or missing input constraint.
    // The catalog's contract is to fail loudly at load, so a typo'd or omitted key throws here rather than
    // surfacing at runtime.
    private sealed record RawTemplate(
        [property: JsonRequired] string Id,
        [property: JsonRequired] IReadOnlyList<RawPhase> Phases,
        [property: JsonRequired] IReadOnlyList<string> Inputs);

    private sealed record RawPhase(
        [property: JsonRequired] string RoleId,
        [property: JsonRequired] string Instruction,
        [property: JsonRequired] bool AskFirst);
}
