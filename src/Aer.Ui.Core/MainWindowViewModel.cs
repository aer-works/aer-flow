using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aer.Ui.Core;

/// <summary>
/// <see cref="MainWindow"/>'s ViewModel layer (M15 Phase 2, issue #138) — introduced for exactly the
/// surface M14 Phase 1 named as the potential second concrete need: the paused-step decision buttons,
/// whose enabled state is tied jointly to projected state (<see cref="PausedSteps"/>) and an
/// in-flight mutation (<see cref="IsMutationInFlight"/>). The rest of the window's read-only
/// rendering (DAG, history, lineage, diff) is untouched, still direct code-behind control
/// manipulation — this type does not attempt to own that.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public ObservableCollection<PausedStepViewModel> PausedSteps { get; } = [];

    /// <summary>Home's cards + decision inbox (M19 Phase 2, #187) — see <see cref="HomeViewModel"/>.</summary>
    public HomeViewModel Home { get; } = new();

    /// <summary>
    /// Which shell section is active (M19 Phase 2, #187) — pure presentation state (like a text
    /// box's contents, UI spec §4), never a projected fact. Opening a task navigates to
    /// <see cref="ShellSection.Task"/>; everything else is the user's own navigation.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeVisible))]
    [NotifyPropertyChangedFor(nameof(IsTaskVisible))]
    [NotifyPropertyChangedFor(nameof(IsAuthorVisible))]
    [NotifyPropertyChangedFor(nameof(IsRemoteVisible))]
    private ShellSection currentSection = ShellSection.Home;

    public bool IsHomeVisible => CurrentSection == ShellSection.Home;
    public bool IsTaskVisible => CurrentSection == ShellSection.Task;
    public bool IsAuthorVisible => CurrentSection == ShellSection.Author;
    public bool IsRemoteVisible => CurrentSection == ShellSection.Remote;

    /// <summary>The Enable Remote Access view's state (M21 Phase 3, issue #234) — see <see cref="RemoteViewModel"/>.</summary>
    public RemoteViewModel Remote { get; } = new();

    /// <summary>
    /// The template editor's state (M16 Phase 1, issue #150) — the authoring surface, deliberately
    /// its own child ViewModel rather than more fields here: authoring is a separate concern from
    /// the mutation/decision surface this type was introduced for, and it is the first surface
    /// whose fields are two-way bound (see <see cref="TemplateEditorViewModel"/>'s own remarks).
    /// </summary>
    public TemplateEditorViewModel TemplateEditor { get; } = new();

    /// <summary>
    /// The worker-bindings editor's state (M16 Phase 4, issue #153) — the second authoring surface,
    /// alongside <see cref="TemplateEditor"/>, riding the same MVVM shape for the same reason: it is
    /// two-way bound. Bindings are a separate concern from template editing (UI spec §4, §9) — never
    /// persisted in a task directory, never touching a bound snapshot.
    /// </summary>
    public BindingsEditorViewModel BindingsEditor { get; } = new();

    /// <summary>The guided New Workflow flow (M19 Phase 4, #189) — the Author view's primary surface; the file editors above are its advanced disclosure.</summary>
    public NewWorkflowViewModel NewWorkflow { get; } = new();

    /// <summary>
    /// One entry per currently-running or cancellation-pending execution (M15 Phase 4, issue #140) —
    /// the targeted-Cancel surface, alongside <see cref="PausedSteps"/>' decision surface.
    /// </summary>
    public ObservableCollection<RunningExecutionViewModel> RunningExecutions { get; } = [];

    /// <summary>
    /// Owner feedback: the "Working right now" section rendered its heading even with nothing
    /// running underneath, reading as a blank/broken panel rather than an honest empty state.
    /// <see cref="RunningExecutions"/> is cleared and refilled in place on the same long-lived
    /// <c>MainWindowViewModel</c> instance (<c>TaskSession.RebuildRunningExecutions</c>), so this
    /// needs its own change notification rather than the "new instance per rebuild" pattern
    /// <see cref="TaskStepsViewModel.HasOutputFiles"/> and its siblings rely on.
    /// </summary>
    public bool HasRunningExecutions => RunningExecutions.Count > 0;

    public MainWindowViewModel()
    {
        RunningExecutions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRunningExecutions));
    }

    /// <summary>
    /// True for the duration of any mutation call this UI process itself is driving — a Run or a
    /// decision — the window's own pump holding the task's §15 lock for that call's entire duration.
    /// Every <see cref="PausedSteps"/> entry's <see cref="PausedStepViewModel.IsEnabled"/> mirrors
    /// this, so a second mutation can never be started from this same process while one is already in
    /// flight (a competing *external* process's lock hold instead surfaces as a
    /// <see cref="Aer.Flow.Concurrency.WorkflowLockedException"/> in-window message, per Phase 1's
    /// precedent — this flag does not, and cannot, prevent that one). <see cref="RunningExecutions"/>
    /// entries are the one deliberate exception: a locally-hosted execution's Cancel stays enabled
    /// exactly while this flag is true (Phase 4) — see <see cref="RunningExecutionViewModel.UpdateEnabled"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool isMutationInFlight;

    /// <summary>
    /// Owner feedback: "does Run make sense on a finished task? or is it a re-run?" — it's neither:
    /// <c>MutationInterface.StartWorkflowAsync</c>'s pump finds nothing ready and nothing in flight
    /// for an already-<see cref="Aer.Flow.Domain.WorkflowStatus.Terminal"/> task and returns the
    /// same state unchanged, a safe but silent no-op. Rather than leave that ambiguous, Run is
    /// disabled once the open task has actually finished — set by <c>MainWindow.LoadAsync</c> from
    /// the loaded projection's <c>State.Status</c>, alongside every other read-only render there.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyPropertyChangedFor(nameof(RunButtonToolTipText))]
    private bool isTaskFinished;

    public bool CanRun => !IsMutationInFlight;

    public string RunButtonToolTipText => IsMutationInFlight
        ? "Execution is currently in flight."
        : "Start a fresh task from a workflow file, or resume/re-run the task open above.";

    /// <summary>In-window message surface for a Run's progress ("Running…") or failure — moved here from a directly-set TextBlock when the orchestration moved to <see cref="TaskSession"/> (M19 Phase 2, #187).</summary>
    [ObservableProperty]
    private string runStatusText = string.Empty;

    /// <summary>In-window message surface for a decision's outcome or failure — the same precedent <see cref="RunStatusText"/> established (Phase 1).</summary>
    [ObservableProperty]
    private string decisionStatusText = string.Empty;

    /// <summary>In-window message surface for a targeted Cancel's outcome or failure (Phase 4) — the same precedent as <see cref="DecisionStatusText"/>.</summary>
    [ObservableProperty]
    private string cancelStatusText = string.Empty;

    /// <summary>The open task's steps as the drill-in surface (M19 Phase 3, #188) — rebuilt wholesale on every load/refresh by <see cref="RebuildTaskSteps"/>.</summary>
    public ObservableCollection<StepItemViewModel> TaskSteps { get; } = [];

    /// <summary>The step whose drill-in is open. Re-anchored by step id across rebuilds; defaults needs-you-first (paused, else running, else the first step).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedStep))]
    private StepItemViewModel? selectedStep;

    public bool HasSelectedStep => SelectedStep is not null;

    /// <summary>The task-level plain-language headline (the vocabulary map's primary text) — the precise <c>Workflow status:</c> line lives in the Details disclosure.</summary>
    [ObservableProperty]
    private string taskHeadlineText = "No task open.";

    partial void OnSelectedStepChanged(StepItemViewModel? value)
    {
        foreach (var step in TaskSteps)
        {
            step.IsSelected = ReferenceEquals(step, value);
        }
    }

    /// <summary>
    /// Rebuilds <see cref="TaskSteps"/> from a fresh projection (M19 Phase 3, #188). The preview
    /// and conversation delegates are the skin's render targets — the same inversion
    /// <see cref="TaskSession"/> uses, keeping this assembly Avalonia-free.
    /// </summary>
    public void RebuildTaskSteps(
        TaskProjection projection,
        string taskDirectoryPath,
        Func<string, Task> previewFileAsync,
        Action<string, string> showConversation,
        IReadOnlyDictionary<string, string>? workerAdapters = null)
    {
        var previousSelectedStepId = SelectedStep?.StepId;

        TaskSteps.Clear();
        foreach (var item in StepItemProjector.Build(
            projection, taskDirectoryPath, PausedSteps, previewFileAsync, showConversation,
            select: item => SelectedStep = item,
            workerAdapters: workerAdapters))
        {
            TaskSteps.Add(item);
        }

        TaskHeadlineText = PlainLanguage.ForWorkflow(projection);
        SelectedStep =
            TaskSteps.FirstOrDefault(step => step.StepId == previousSelectedStepId) ??
            TaskSteps.FirstOrDefault(step => step.IsPaused) ??
            TaskSteps.FirstOrDefault(step => step.Status == Aer.Flow.Domain.StepStatus.Running) ??
            TaskSteps.FirstOrDefault();
    }

    /// <summary>Clears the drill-in surface — the error-path counterpart of <see cref="RebuildTaskSteps"/>.</summary>
    public void ClearTaskSteps()
    {
        TaskSteps.Clear();
        SelectedStep = null;
        TaskHeadlineText = "No task open.";
    }

    /// <summary>Selects a step by id — the DAG canvas's node-click entry point (the canvas stays code-behind until Phase 5 makes it a custom control).</summary>
    public void SelectStepById(string stepId)
        => SelectedStep = TaskSteps.FirstOrDefault(step => step.StepId == stepId) ?? SelectedStep;

    partial void OnCurrentSectionChanged(ShellSection value) => SectionChanged?.Invoke(value);

    /// <summary>Raised on navigation so the shell can refresh the newly-activated section (Home rebuilds its cards/inbox on activation — its decision of record).</summary>
    public event Action<ShellSection>? SectionChanged;

    partial void OnIsMutationInFlightChanged(bool value)
    {
        foreach (var step in PausedSteps)
        {
            step.IsEnabled = !value;
        }

        foreach (var execution in RunningExecutions)
        {
            execution.UpdateEnabled(value);
        }
    }
}
