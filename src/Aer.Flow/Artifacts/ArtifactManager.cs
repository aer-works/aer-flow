using Aer.Flow.Domain;

namespace Aer.Flow.Artifacts;

/// <summary>
/// Pre-allocates artifact directories and computes the paths a worker is invoked with (spec §16).
/// Workers are blind to versioning, lineage, and path topology — they receive only an input set of
/// paths and one output directory, and Flow computes and assigns all of it before dispatch.
/// M7 Phase 6 resolves the open question of how paths are passed: as environment variables,
/// <c>AER_INPUT_&lt;n&gt;</c> and <c>AER_OUTPUT_DIR</c>, per the spec's own example.
/// </summary>
public static class ArtifactManager
{
    /// <summary>
    /// Creates (if needed) and returns <c>{artifactsRootPath}/execution_{executionId}</c> — the
    /// immutable directory this execution's outputs will be written into (§16). Addressing the
    /// directory by <see cref="ExecutionId"/> rather than a separately tracked sequence number is
    /// what makes every artifact's provenance derivable from the Event Store alone.
    /// </summary>
    public static string AllocateOutputDirectory(string artifactsRootPath, ExecutionId executionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        var directory = OutputDirectoryPath(artifactsRootPath, executionId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// The supplementary artifact a <see cref="Domain.DecisionType.RetryWithRevision"/> or
    /// <see cref="Domain.DecisionType.Supersede"/> decision attaches to its consequence's dispatch
    /// (§17.2, §17.5): <paramref name="supplementaryExecutionId"/>'s already-completed output
    /// directory, addressed the same way as any other execution's — no new path convention needed.
    /// </summary>
    public static string ResolveSupplementaryInputPath(string artifactsRootPath, ExecutionId supplementaryExecutionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        return OutputDirectoryPath(artifactsRootPath, supplementaryExecutionId);
    }

    /// <summary>
    /// The same addressing <see cref="AllocateOutputDirectory"/> uses, without creating anything —
    /// for a caller that only needs to read an execution's already-allocated output directory (e.g.
    /// <see cref="Outcomes.NonProcessCompletionDetector"/> checking contract satisfaction, §17.3).
    /// </summary>
    public static string ResolveOutputDirectory(string artifactsRootPath, ExecutionId executionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        return OutputDirectoryPath(artifactsRootPath, executionId);
    }

    private static string OutputDirectoryPath(string artifactsRootPath, ExecutionId executionId) =>
        Path.Combine(artifactsRootPath, $"execution_{executionId}");

    /// <summary>
    /// Resolves <paramref name="step"/>'s declared <c>Inputs</c> to concrete file paths, in
    /// declaration order, by locating — among <paramref name="step"/>'s direct
    /// <c>DependsOn</c> — the one dependency whose declared <c>Outputs</c> contains that input's
    /// name, then combining that dependency's most recent successful execution's output directory
    /// (§16) with the name itself. Requires every dependency to already have a successful
    /// execution recorded in <paramref name="state"/> — true for any step the Dependency Resolver
    /// (§11.3 condition 1) has already deemed ready.
    /// </summary>
    public static IReadOnlyList<string> ResolveInputPaths(
        WorkflowStepDefinition step,
        WorkflowDefinitionSnapshot snapshot,
        FlowState state,
        string artifactsRootPath)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        if (step.Inputs.Count == 0)
        {
            return [];
        }

        var stepDefinitionById = snapshot.Steps.ToDictionary(s => s.StepId);
        var stepStateById = state.Steps.ToDictionary(s => s.StepId);

        var paths = new List<string>(step.Inputs.Count);
        foreach (var inputName in step.Inputs)
        {
            var producer = FindProducer(step, inputName, stepDefinitionById);
            var producerExecutionId = stepStateById[producer.StepId].LatestExecutionId
                ?? throw new ArtifactResolutionException(
                    $"Dependency '{producer.StepId}' has no successful execution yet; cannot resolve " +
                    $"input '{inputName}' for step '{step.StepId}'.");

            paths.Add(Path.Combine(artifactsRootPath, $"execution_{producerExecutionId}", inputName));
        }

        return paths;
    }

    /// <summary>
    /// Builds the AER-computed environment variables (§3, §16) a worker is invoked with:
    /// <c>AER_INPUT_0</c>.. for each resolved input path, in order, <c>AER_OUTPUT_DIR</c> for
    /// the pre-allocated output directory, and — only when this dispatch is a
    /// <see cref="Domain.DecisionType.RetryWithRevision"/> or <see cref="Domain.DecisionType.Supersede"/>
    /// consequence carrying a supplement (§17.2, §17.5) — <c>AER_SUPPLEMENTARY_INPUT</c> for
    /// <paramref name="supplementaryInputPath"/>. A dedicated variable, not a declared input name, so
    /// it can never collide with a step's own declared <c>Inputs</c>. Pass-through variables
    /// (secrets, vendor settings) are not this method's concern — they carry no derived value and
    /// are resolved separately, immediately before dispatch (§3).
    /// </summary>
    public static IReadOnlyList<EnvironmentVariable.AerComputed> BuildEnvironment(
        IReadOnlyList<string> inputPaths,
        string outputDirectory,
        string? supplementaryInputPath = null)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        var variables = new List<EnvironmentVariable.AerComputed>(inputPaths.Count + 2);
        for (var i = 0; i < inputPaths.Count; i++)
        {
            variables.Add(new EnvironmentVariable.AerComputed($"AER_INPUT_{i}", inputPaths[i]));
        }

        variables.Add(new EnvironmentVariable.AerComputed("AER_OUTPUT_DIR", outputDirectory));

        if (supplementaryInputPath is not null)
        {
            variables.Add(new EnvironmentVariable.AerComputed("AER_SUPPLEMENTARY_INPUT", supplementaryInputPath));
        }

        return variables;
    }

    private static WorkflowStepDefinition FindProducer(
        WorkflowStepDefinition step,
        string inputName,
        Dictionary<StepId, WorkflowStepDefinition> stepDefinitionById)
    {
        foreach (var dependencyStepId in step.DependsOn)
        {
            if (stepDefinitionById[dependencyStepId].Outputs.Contains(inputName))
            {
                return stepDefinitionById[dependencyStepId];
            }
        }

        throw new ArtifactResolutionException(
            $"No direct dependency of step '{step.StepId}' declares output '{inputName}'.");
    }
}
