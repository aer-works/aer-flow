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

    /// <summary>
    /// #215: real execution/decision ids are 32-char generated Guids — pure visual noise to a
    /// non-expert user inline in the drill-in's primary text. Truncated to a short prefix, still
    /// enough to distinguish attempts by eye; the untruncated id remains in the Details disclosure
    /// (§12 traceability), which this projection never touches.
    /// </summary>
    public static string ShortId(string id) => id.Length > 8 ? id[..8] : id;
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
        Action<StepItemViewModel> select,
        string? adapter = null)
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
        Adapter = adapter;
    }

    public string StepId { get; }
    public string Worker { get; }
    public string? Adapter { get; }
    public StepStatus Status { get; }
    public string PlainStatusText => PlainLanguage.ForStep(Status);
    public IReadOnlyList<string> AttemptLines { get; }
    public IReadOnlyList<ArtifactFileViewModel> OutputFiles { get; }
    public IReadOnlyList<ConversationRefViewModel> Conversations { get; }
    public IReadOnlyList<string> DecisionLines { get; }

    public string VendorDisplay
    {
        get
        {
            var target = (Adapter ?? Worker).ToLowerInvariant();
            if (target.Contains("claude")) return "claude";
            if (target.Contains("gemini")) return "gemini";
            if (target.Contains("shell") || target.Contains("stub")) return "shell";
            if (target.Contains("codex") || target.Contains("openai")) return "codex";
            return Adapter != null ? $"{Worker} ({Adapter})" : Worker;
        }
    }

    public string VendorIconGeometry => (Adapter ?? Worker).ToLowerInvariant() switch
    {
        var s when s.Contains("gemini") => "M 12 2 C 12 7.5 7.5 12 2 12 C 7.5 12 12 16.5 12 22 C 12 16.5 16.5 12 22 12 C 16.5 12 12 7.5 12 2 Z",
        var s when s.Contains("claude") => "M 12 2 L 13.5 8.5 L 20 6 L 15.5 11 L 22 14 L 15 15 L 17 22 L 12 17 L 7 22 L 9 15 L 2 14 L 8.5 11 L 4 6 L 10.5 8.5 Z",
        _ => "M 4 5 L 11 12 L 4 19 M 13 19 L 20 19"
    };

    public string VendorBrushColor => (Adapter ?? Worker).ToLowerInvariant() switch
    {
        var s when s.Contains("gemini") => "#4285F4",
        var s when s.Contains("claude") => "#D97706",
        _ => "#10B981"
    };

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

/// <summary>
/// One durable output file of one execution, previewable in place — the same file-listing +
/// plain-text-preview ceiling as M14 (issue #121), re-sliced per step. <see cref="IsSelected"/>
/// (#211) tracks which file's content the step's single preview surface currently shows, so the
/// chip that produced it stays visually indicated instead of the previous selection lingering.
/// </summary>
public sealed partial class ArtifactFileViewModel : ObservableObject
{
    private readonly Func<string, Task> _previewAsync;
    private readonly Action<ArtifactFileViewModel> _select;

    public ArtifactFileViewModel(
        string label, string filePath, Func<string, Task> previewAsync, Action<ArtifactFileViewModel> select)
    {
        Label = label;
        FilePath = filePath;
        _previewAsync = previewAsync;
        _select = select;
    }

    public string Label { get; }
    public string FilePath { get; }

    [ObservableProperty]
    private bool isSelected;

    [RelayCommand]
    private Task Preview()
    {
        _select(this);
        return _previewAsync(FilePath);
    }
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
        Action<StepItemViewModel> select,
        IReadOnlyDictionary<string, string>? workerAdapters = null)
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
                    $"{PlainLanguage.ForStep(attempt.Status)}{classificationSuffix} ({PlainLanguage.ShortId(attempt.ExecutionId.ToString())})");
            }

            var outputFiles = new List<ArtifactFileViewModel>();
            var conversations = new List<ConversationRefViewModel>();
            foreach (var execution in executionsByStepId[stepState.StepId])
            {
                var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, execution.ExecutionId);
                foreach (var fileName in execution.OutputFiles)
                {
                    outputFiles.Add(new ArtifactFileViewModel(
                        $"{fileName} ({PlainLanguage.ShortId(execution.ExecutionId.ToString())})",
                        Path.Combine(outputDirectory, fileName),
                        previewFileAsync,
                        select: file => SelectOutputFile(outputFiles, file)));
                }

                if (TranscriptProjectionLoader.HasTranscript(outputDirectory))
                {
                    conversations.Add(new ConversationRefViewModel(
                        $"{stepState.StepId} — {PlainLanguage.ShortId(execution.ExecutionId.ToString())} ({execution.Worker})",
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
                           $"({PlainLanguage.ShortId(decision.DecisionId.ToString())} on {PlainLanguage.ShortId(decision.ReferencedExecutionId.ToString())}){pending}";
                })
                .ToList();

            var stepDefinition = projection.Snapshot.Steps.First(step => step.StepId == stepState.StepId);
            var adapter = workerAdapters?.GetValueOrDefault(stepDefinition.Worker);

            items.Add(new StepItemViewModel(
                stepState.StepId.Value,
                stepDefinition.Worker,
                stepState.Status,
                attemptLines,
                outputFiles,
                conversations,
                decisionLines,
                pausedByStepId.GetValueOrDefault(stepState.StepId),
                select,
                adapter));
        }

        return items;
    }

    /// <summary>#211: marks exactly one output file of the step selected — mirrors <see cref="StepItemViewModel"/>'s own sibling-clearing selection pattern, scoped to one step's file list.</summary>
    private static void SelectOutputFile(IReadOnlyList<ArtifactFileViewModel> files, ArtifactFileViewModel selected)
    {
        foreach (var file in files)
        {
            file.IsSelected = ReferenceEquals(file, selected);
        }
    }
}
