using Aer.Flow.Artifacts;
using Aer.Flow.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui.Core;

/// <summary>
/// M19 Phase 3 (issue #188): the plain-language vocabulary map (docs/ux/ux-principles.md) applied
/// to the task view's primary text. Total for primary labels — a spec term reaching a primary
/// label is a defect (Phase 1 decision of record); the precise engine vocabulary survives one
/// disclosure away (ids as handles, the Details section, tooltips).
/// </summary>
public static class PlainLanguage
{
    public static string ForStep(StepStatus status) => status switch
    {
        StepStatus.Pending => "Not started yet",
        StepStatus.Running => "Working",
        StepStatus.Succeeded => "Done",
        StepStatus.Failed => "Failed",
        StepStatus.Cancelled => "Stopped",
        StepStatus.Paused => "Waiting for your review",
        StepStatus.Rejected => "Rejected",
        _ => status.ToString(),
    };

    public static string ForDecision(DecisionType decisionType) => decisionType switch
    {
        DecisionType.Resume => "Approved",
        DecisionType.Reject => "Rejected",
        DecisionType.RetryWithRevision => "Retry requested",
        DecisionType.Supersede => "Sent back",
        _ => decisionType.ToString(),
    };

    /// <summary>The task-level headline — the same mapping Home's cards use, shared so the two surfaces can never drift.</summary>
    public static string ForWorkflow(TaskProjection projection) => projection.State.Status switch
    {
        WorkflowStatus.Paused => "Waiting for your review",
        WorkflowStatus.Running when projection.State.Steps.FirstOrDefault(s => s.Status == StepStatus.Running) is { } running
            => $"Working — {running.StepId.Value}",
        WorkflowStatus.Running => "Working",
        _ when projection.State.Steps.Any(s => s.Status is StepStatus.Failed or StepStatus.Rejected) => "Failed",
        _ => "Finished",
    };
}

/// <summary>
/// One step of the open task as the drill-in's read model (M19 Phase 3, issue #188;
/// docs/ux/information-architecture.md's Task view): the plain status up front, and everything
/// that used to sprawl as separate stacked sections — attempts, output files, conversations,
/// recorded decisions — sliced per step. Rebuilt wholesale on every refresh like every other
/// projection surface; selection is re-anchored by <see cref="StepId"/> across rebuilds.
/// </summary>
public sealed partial class StepItemViewModel : ObservableObject
{
    private readonly Action<StepItemViewModel> _select;

    public StepItemViewModel(
        string stepId,
        string worker,
        StepStatus status,
        IReadOnlyList<string> attemptLines,
        IReadOnlyList<ArtifactFileViewModel> outputFiles,
        IReadOnlyList<ConversationRefViewModel> conversations,
        IReadOnlyList<string> decisionLines,
        PausedStepViewModel? pausedStep,
        Action<StepItemViewModel> select)
    {
        StepId = stepId;
        Worker = worker;
        Status = status;
        AttemptLines = attemptLines;
        OutputFiles = outputFiles;
        Conversations = conversations;
        DecisionLines = decisionLines;
        PausedStep = pausedStep;
        _select = select;
    }

    public string StepId { get; }
    public string Worker { get; }
    public StepStatus Status { get; }
    public string PlainStatusText => PlainLanguage.ForStep(Status);
    public IReadOnlyList<string> AttemptLines { get; }
    public IReadOnlyList<ArtifactFileViewModel> OutputFiles { get; }
    public IReadOnlyList<ConversationRefViewModel> Conversations { get; }
    public IReadOnlyList<string> DecisionLines { get; }

    /// <summary>Non-null exactly while this step waits at its review gate — the inline decision actions (§17 via M15's <see cref="PausedStepViewModel"/>, unchanged semantics, plain words on the buttons).</summary>
    public PausedStepViewModel? PausedStep { get; }
    public bool IsPaused => PausedStep is not null;

    public bool HasOutputFiles => OutputFiles.Count > 0;
    public bool HasConversations => Conversations.Count > 0;
    public bool HasDecisions => DecisionLines.Count > 0;

    [ObservableProperty]
    private bool isSelected;

    [RelayCommand]
    private void Select() => _select(this);
}

/// <summary>One durable output file of one execution, previewable in place — the same file-listing + plain-text-preview ceiling as M14 (issue #121), re-sliced per step.</summary>
public sealed partial class ArtifactFileViewModel(string label, string filePath, Func<string, Task> previewAsync)
{
    public string Label { get; } = label;
    public string FilePath { get; } = filePath;

    [RelayCommand]
    private Task Preview() => previewAsync(FilePath);
}

/// <summary>One execution of the step that recorded a durable transcript — opens M18's conversation view unchanged in behavior (discovery by transcript presence alone, §10.1).</summary>
public sealed partial class ConversationRefViewModel(string label, string outputDirectory, Action<string, string> show)
{
    public string Label { get; } = label;
    public string OutputDirectory { get; } = outputDirectory;

    [RelayCommand]
    private void Show() => show(OutputDirectory, Label);
}

/// <summary>
/// Builds the per-step drill-in items from one <see cref="TaskProjection"/> — pure projection
/// re-slicing (§11): every fact here already renders in the Details section's task-level panels;
/// this groups it by step, in plain language, nothing new asserted.
/// </summary>
public static class StepItemProjector
{
    public static IReadOnlyList<StepItemViewModel> Build(
        TaskProjection projection,
        string taskDirectoryPath,
        IReadOnlyList<PausedStepViewModel> pausedSteps,
        Func<string, Task> previewFileAsync,
        Action<string, string> showConversation,
        Action<StepItemViewModel> select)
    {
        var artifactsRootPath = Path.Combine(taskDirectoryPath, "artifacts");
        var pausedByStepId = pausedSteps.ToDictionary(paused => paused.StepId);
        var executionsByStepId = projection.Lineage.Executions
            .Where(execution => execution.StepId is not null)
            .ToLookup(execution => execution.StepId!);
        var stepIdByExecutionId = projection.Lineage.Executions
            .Where(execution => execution.StepId is not null)
            .ToDictionary(execution => execution.ExecutionId, execution => execution.StepId!);

        var items = new List<StepItemViewModel>(projection.State.Steps.Count);
        foreach (var stepState in projection.State.Steps)
        {
            var attempts = projection.History.AttemptsByStepId.GetValueOrDefault(
                stepState.StepId, (IReadOnlyList<ExecutionAttempt>)[]);
            var attemptLines = new List<string>(attempts.Count);
            for (var index = 0; index < attempts.Count; index++)
            {
                var attempt = attempts[index];
                var classificationSuffix = attempt.FailureClassification switch
                {
                    FailureClassification.Retryable => " — can be retried",
                    FailureClassification.Permanent => " — not retryable",
                    _ => string.Empty,
                };
                attemptLines.Add(
                    $"Attempt {index + 1} of {attempts.Count}: " +
                    $"{PlainLanguage.ForStep(attempt.Status)}{classificationSuffix} ({attempt.ExecutionId})");
            }

            var outputFiles = new List<ArtifactFileViewModel>();
            var conversations = new List<ConversationRefViewModel>();
            foreach (var execution in executionsByStepId[stepState.StepId])
            {
                var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, execution.ExecutionId);
                foreach (var fileName in execution.OutputFiles)
                {
                    outputFiles.Add(new ArtifactFileViewModel(
                        $"{fileName} ({execution.ExecutionId})",
                        Path.Combine(outputDirectory, fileName),
                        previewFileAsync));
                }

                if (TranscriptProjectionLoader.HasTranscript(outputDirectory))
                {
                    conversations.Add(new ConversationRefViewModel(
                        $"{stepState.StepId} — {execution.ExecutionId} ({execution.Worker})",
                        outputDirectory,
                        showConversation));
                }
            }

            var decisionLines = projection.History.Decisions
                .Where(decision =>
                    (stepIdByExecutionId.TryGetValue(decision.ReferencedExecutionId, out var decidedStepId) &&
                     decidedStepId == stepState.StepId) ||
                    decision.TargetStepId == stepState.StepId)
                .Select(decision =>
                {
                    var target = decision.TargetStepId is { } targetStepId ? $" to {targetStepId}" : string.Empty;
                    var pending = decision.Resolved ? string.Empty : " — not carried out yet";
                    return $"{PlainLanguage.ForDecision(decision.DecisionType)}{target} " +
                           $"({decision.DecisionId} on {decision.ReferencedExecutionId}){pending}";
                })
                .ToList();

            var stepDefinition = projection.Snapshot.Steps.First(step => step.StepId == stepState.StepId);
            items.Add(new StepItemViewModel(
                stepState.StepId.Value,
                stepDefinition.Worker,
                stepState.Status,
                attemptLines,
                outputFiles,
                conversations,
                decisionLines,
                pausedByStepId.GetValueOrDefault(stepState.StepId),
                select));
        }

        return items;
    }
}
