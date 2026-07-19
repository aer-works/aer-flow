using Aer.Adapters;
using Aer.Cli;
using Aer.Flow;
using Aer.Flow.Artifacts;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Templates;
using Aer.Ui.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using Avalonia.Threading;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Win32;



[assembly: InternalsVisibleTo("Aer.Ui.Tests")]

namespace Aer.Ui;

public partial class MainWindow : Window
{
    private const string ArtifactsDirectoryName = "artifacts";
    private const int MaxArtifactPreviewLength = 8000;

    /// <summary>
    /// This window's presentation-agnostic half (M19 Phase 2, issue #187): projection loading, pump
    /// hosting, and every mutation-interface call live on the session in <c>Aer.Ui.Core</c> — this
    /// code-behind renders the session's outcomes and raises its intents, nothing more. The
    /// constructor delegates wire the presentation half back in: the bindings box as the
    /// ask-don't-infer path source, the 2-second poller as the mutation-progress renderer, and
    /// <see cref="OpenAsync"/> as the settle-time re-open.
    /// </summary>
    private readonly TaskSession _session;

    private readonly DispatcherTimer _liveRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>
    /// Drives <see cref="RemoteViewModel.TickPairingCodeCountdown"/> once a second while the Remote
    /// section is open — the label was previously set once from the fetch response and never
    /// updated, so "Expires in 60s" stayed frozen even long after the daemon's own 60s expiry
    /// (<c>PairingCodeManager.ValidateAndConsume</c>) had made the code genuinely dead. Auto-fetches
    /// a fresh code on reaching 0 rather than leaving a visibly-expired one on screen.
    /// </summary>
    private readonly DispatcherTimer _pairingCountdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>
    /// Which execution's conversation is currently shown (M18 Phase 2, issue #178) — local UI
    /// selection state like <see cref="TaskDirectoryPathBox"/>'s text (UI spec §4), never a
    /// projected fact: every <see cref="LoadAsync"/> re-renders the conversation from the durable
    /// transcript this directory holds *now*, which is how load-on-refresh follows a still-running
    /// exchange without any push/streaming channel.
    /// </summary>
    private string? _conversationOutputDirectory;
    private string? _conversationLabel;

    /// <summary>Set once <see cref="OnClosing"/> has already stopped an in-flight pump and is closing the window for real — prevents re-entering the stop sequence from the follow-up programmatic <see cref="Window.Close()"/>.</summary>
    private bool _closeConfirmed;

    /// <summary>Test-only observation of the live-refresh polling state (see <see cref="UpdateLiveRefreshTimer"/>) — never consulted by production code.</summary>
    internal bool IsLiveRefreshTimerEnabled => _liveRefreshTimer.IsEnabled;

    /// <summary>
    /// This window's ViewModel (M15 Phase 2, issue #138) — set as <see cref="Window.DataContext"/> so
    /// <see cref="MainWindow.axaml"/> can bind the paused-step decision surface and the shared
    /// mutation-in-flight flag directly. See <see cref="MainWindowViewModel"/>'s own remarks for why
    /// this is scoped to that surface only, not the rest of the window's rendering.
    /// </summary>
    internal MainWindowViewModel ViewModel { get; } = new();

    // ── The re-home facade (M19 Phase 2, #187) ─────────────────────────────────────────────────
    // Every pre-shell control, reachable under its original name: the shell moved the controls
    // into Home/Task/Author views (their new homes per docs/ux/information-architecture.md), and
    // these internal properties are how this window's rendering code and the headless round trips
    // keep addressing them — one migration per surface, no behavioral change. Phases 3–4 retire
    // entries as they rebuild each surface properly.
    internal TextBox TaskDirectoryPathBox => HomeViewControl.TaskDirectoryPathBox;
    internal Button OpenButton => HomeViewControl.OpenButton;
    internal Button RefreshButton => HomeViewControl.RefreshButton;

    internal TextBox WorkflowTemplatePathBox => TaskViewControl.WorkflowTemplatePathBox;
    internal TextBox BindingsFilePathBox => TaskViewControl.BindingsFilePathBox;
    internal Button RunButton => TaskViewControl.RunButton;
    internal Button StopButton => TaskViewControl.StopButton;
    internal TextBlock RunStatusText => TaskViewControl.RunStatusText;
    internal TextBlock StatusText => TaskViewControl.StatusText;
    internal StackPanel StepsPanel => TaskViewControl.StepsPanel;
    internal TextBlock CancelStatusText => TaskViewControl.CancelStatusText;
    internal TextBlock DecisionStatusText => TaskViewControl.DecisionStatusText;
    internal Canvas DagCanvas => TaskViewControl.DagCanvas;
    internal StackPanel HistoryPanel => TaskViewControl.HistoryPanel;
    internal StackPanel ConversationExecutionsPanel => TaskViewControl.ConversationExecutionsPanel;
    internal StackPanel ConversationPanel => TaskViewControl.ConversationPanel;
    internal StackPanel DecisionsPanel => TaskViewControl.DecisionsPanel;
    internal StackPanel SupplementaryPanel => TaskViewControl.SupplementaryPanel;
    internal StackPanel LineagePanel => TaskViewControl.LineagePanel;
    internal TextBox ArtifactPreviewBox => TaskViewControl.ArtifactPreviewBox;
    internal TextBox TemplateComparePathBox => TaskViewControl.TemplateComparePathBox;
    internal Button CompareButton => TaskViewControl.CompareButton;
    internal StackPanel DiffPanel => TaskViewControl.DiffPanel;

    internal TextBox TemplateEditorPathBox => AuthorViewControl.TemplateEditorPathBox;
    internal Button NewTemplateButton => AuthorViewControl.NewTemplateButton;
    internal Button EditTemplateButton => AuthorViewControl.EditTemplateButton;
    internal Button SaveTemplateButton => AuthorViewControl.SaveTemplateButton;
    internal Button AddStepButton => AuthorViewControl.AddStepButton;
    internal Canvas TemplateEditorDagCanvas => AuthorViewControl.TemplateEditorDagCanvas;
    internal TextBox BindingsEditorPathBox => AuthorViewControl.BindingsEditorPathBox;
    internal Button NewBindingsButton => AuthorViewControl.NewBindingsButton;
    internal Button EditBindingsButton => AuthorViewControl.EditBindingsButton;
    internal Button SaveBindingsButton => AuthorViewControl.SaveBindingsButton;
    internal Button AddBindingEntryButton => AuthorViewControl.AddBindingEntryButton;
    internal Button CheckBindingsAgainstTemplateButton => AuthorViewControl.CheckBindingsAgainstTemplateButton;

    internal Button RemoteToggleButton => RemoteViewControl.RemoteToggleButton;
    internal Button RemoteRefreshCodeButton => RemoteViewControl.RemoteRefreshCodeButton;
    internal Button RemoteOpenSidecarAuthButton => RemoteViewControl.RemoteOpenSidecarAuthButton;
    internal Button RemoteForgetSidecarButton => RemoteViewControl.RemoteForgetSidecarButton;
    internal Button SaveTailscaleAuthKeyButton => RemoteViewControl.SaveTailscaleAuthKeyButton;

    /// <summary>
    /// The re-homed counterpart of <c>Window.FindControl</c> for the headless round trips: controls
    /// now live in the views' name scopes, so the window's own scope no longer resolves them — this
    /// searches Home, Task, then Author, preserving every test's by-name lookup unchanged.
    /// </summary>
    internal T? FindViewControl<T>(string name) where T : Control
        => HomeViewControl.FindControl<T>(name)
           ?? TaskViewControl.FindControl<T>(name)
           ?? AuthorViewControl.FindControl<T>(name);

    private static readonly bool IsUnderTest = AppDomain.CurrentDomain.GetAssemblies()
        .Any(a => a.FullName != null && (a.FullName.Contains("xunit") || a.FullName.Contains("Test") || a.FullName.Contains("test")));

    public MainWindow()
        : this(LocalUiConfigurationStore.CreateDefault(), WorkerAdapterRegistry.Default, IsUnderTest ? null : "http://localhost:5000")
    {
    }

    /// <summary>
    /// Takes the recents store as a constructor argument, never constructing
    /// <see cref="LocalUiConfigurationStore.CreateDefault"/> unconditionally, so a test can point it
    /// at a temp file instead of the real per-user config directory — the same "production wiring
    /// is the caller's decision" seam <c>Aer.Cli</c>'s <c>RunCommand</c> established for the adapter
    /// registry (M11 Phase 3).
    /// </summary>
    public MainWindow(LocalUiConfigurationStore configurationStore)
        : this(configurationStore, WorkerAdapterRegistry.Default, IsUnderTest ? null : "http://localhost:5000")
    {
    }

    /// <summary>
    /// Takes the worker-adapter registry as a constructor argument too (M15 Phase 1, issue #137) —
    /// the same production-wiring-is-the-caller's-decision seam as <paramref name="configurationStore"/>
    /// and, before this window existed, <c>RunCommand.ExecuteAsync</c>'s own adapter-registry
    /// parameter (M11 Phase 3). Defaults to <see cref="WorkerAdapterRegistry.Default"/> via the
    /// other two constructors so production callers never have to name it explicitly, while
    /// <c>Aer.Ui.Tests</c> can substitute a deterministic shell-stub registry instead of resolving a
    /// live vendor CLI.
    /// </summary>
    public MainWindow(LocalUiConfigurationStore configurationStore, IReadOnlyDictionary<string, IWorkerAdapter> adapters, string? daemonUrl = null)
    {
        InitializeComponent();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Win32Properties.AddWndProcHookCallback(this, WndProcHook);
        }
        DataContext = ViewModel;
        _session = new TaskSession(
            configurationStore,
            adapters,
            ViewModel,
            bindingsFilePathProvider: () => BindingsFilePathBox.Text,
            mutationStarted: _liveRefreshTimer.Start,
            mutationFailed: _liveRefreshTimer.Stop,
            reopenTaskAsync: (taskDirectoryPath, cancellationToken) => OpenAsync(taskDirectoryPath, cancellationToken),
            daemonUrl: daemonUrl);

        // M16 Phase 4 (issue #153): adapter names are offered from the registry this window was
        // constructed with — reflect, don't invent — carried per-row on WorkerBindingEntryViewModel
        // rather than bound from a shared ancestor, since ItemsControl.ItemTemplate's DataContext is
        // the entry itself.
        ViewModel.BindingsEditor.SetAdapterRegistry(adapters);
        ViewModel.NewWorkflow.SetAdapterRegistry(adapters);

        _liveRefreshTimer.Tick += (_, _) => _ = RefreshAsync();
        OpenButton.Click += (_, _) => _ = OpenAsync(TaskDirectoryPathBox.Text ?? string.Empty);
        RefreshButton.Click += (_, _) => _ = RefreshAsync();
        CompareButton.Click += (_, _) => _ = CompareToTemplateAsync(TemplateComparePathBox.Text ?? string.Empty);
        RunButton.Click += (_, _) => _ = RunAsync(
            TaskDirectoryPathBox.Text ?? string.Empty, WorkflowTemplatePathBox.Text, BindingsFilePathBox.Text ?? string.Empty);
        StopButton.Click += (_, _) => _ = StopAsync();
        NewTemplateButton.Click += (_, _) => NewTemplate();
        EditTemplateButton.Click += (_, _) => _ = OpenTemplateInEditorAsync(TemplateEditorPathBox.Text ?? string.Empty);
        SaveTemplateButton.Click += (_, _) => _ = SaveTemplateAsync(TemplateEditorPathBox.Text ?? string.Empty);
        AddStepButton.Click += (_, _) => ViewModel.TemplateEditor.AddStep();
        ViewModel.TemplateEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TemplateEditorViewModel.PreviewLayout))
            {
                RenderTemplateEditorDag();
            }
        };
        NewBindingsButton.Click += (_, _) => NewBindings();
        EditBindingsButton.Click += (_, _) => _ = OpenBindingsInEditorAsync(BindingsEditorPathBox.Text ?? string.Empty);
        SaveBindingsButton.Click += (_, _) => _ = SaveBindingsAsync(BindingsEditorPathBox.Text ?? string.Empty);
        AddBindingEntryButton.Click += (_, _) =>
        {
            ViewModel.BindingsEditor.AddEntry();
            RefreshBindingsTemplateCrossCheck();
        };
        CheckBindingsAgainstTemplateButton.Click += (_, _) => RefreshBindingsTemplateCrossCheck();
        NavHomeButton.Click += (_, _) => ViewModel.CurrentSection = ShellSection.Home;
        NavTaskButton.Click += (_, _) => ViewModel.CurrentSection = ShellSection.Task;
        NavAuthorButton.Click += (_, _) => ViewModel.CurrentSection = ShellSection.Author;
        NavRemoteButton.Click += (_, _) => ViewModel.CurrentSection = ShellSection.Remote;
        RemoteToggleButton.Click += (_, _) => _ = ViewModel.Remote.ToggleRemoteAsync(_session);
        RemoteRefreshCodeButton.Click += (_, _) => _ = ViewModel.Remote.GeneratePairingCodeAsync(_session);
        // M21 Phase 5 (#242): the one-time interactive Tailscale sign-in the tsnet sidecar needs on
        // first enrollment. UseShellExecute=true is the standard cross-platform "hand this URL to
        // whatever the OS's default browser is" — Aer.Ui.Core has no process-launching capability
        // of its own (kept Avalonia/OS-free), so this stays here with the rest of the button wiring.
        RemoteOpenSidecarAuthButton.Click += (_, _) =>
        {
            if (ViewModel.Remote.SidecarAuthUrl is { } url)
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { /* best-effort */ }
            }
        };
        // The only prior way to disconnect the sidecar's tsnet node was deleting it from the
        // Tailscale admin console and restarting Aer.Ui — found live via direct user feedback.
        RemoteForgetSidecarButton.Click += (_, _) => _ = ViewModel.Remote.ForgetSidecarAsync(_session);
        SaveTailscaleAuthKeyButton.Click += (_, _) => _ = ViewModel.Remote.SaveTailscaleAuthKeyAsync(_session);
        // Home rebuilds its cards/inbox on activation (HomeViewModel's scan-scope decision of
        // record) — fire-and-forget like every other event-handler entry point here.
        ViewModel.SectionChanged += section =>
        {
            if (section == ShellSection.Home)
            {
                _ = RefreshHomeAsync(CancellationToken.None);
            }

            // M19 Phase 4 (#189): the read-only vendor-readiness line refreshes on Author
            // activation — presence can change while the app is open (a CLI just installed).
            if (section == ShellSection.Author)
            {
                ViewModel.NewWorkflow.RefreshVendorReadiness();
            }

            // M21 Phase 3 (#234): remote status + a fresh pairing code on every activation — a
            // code expires in 60s, so a re-visit should never show a stale/dead one.
            if (section == ShellSection.Remote)
            {
                _ = ViewModel.Remote.RefreshAsync(_session);
                _pairingCountdownTimer.Start();
            }
            else
            {
                _pairingCountdownTimer.Stop();
            }
        };
        _pairingCountdownTimer.Tick += (_, _) =>
        {
            ViewModel.Remote.TickPairingCodeCountdown();
            if (ViewModel.Remote.PairingCodeExpiresInSeconds <= 0)
            {
                _ = ViewModel.Remote.GeneratePairingCodeAsync(_session);
            }

            // M21 Phase 5 (#242): reuses this same 1s tick to poll the tsnet sidecar's status
            // rather than a second timer — stops once Ready (the tailnet IP shouldn't change while
            // the process runs), so this doesn't poll forever after enrollment completes.
            if (ViewModel.Remote.ShouldPollSidecarStatus)
            {
                _ = ViewModel.Remote.RefreshSidecarStatusAsync(_session);
            }

            // Phase 6 (#243) follow-up: a phone pairing while this page is already open used to
            // only show up in "Paired devices" after navigating away and back (RefreshPairedClientsAsync
            // was only ever called from RefreshAsync's own activation/toggle path) — found live. Only
            // polls while remote access is actually on, since a new pairing can't happen otherwise.
            if (ViewModel.Remote.IsRemoteEnabled)
            {
                _ = ViewModel.Remote.RefreshPairedClientsAsync(_session);
            }
        };
        // M19 Phase 4 (#189): Save & Run without leaving the flow — each run gets a fresh task
        // directory beside the authored files (one workspace per workflow, tasks inside it), then
        // the shell navigates to the Task view and drives the same RunAsync as the Run button.
        ViewModel.NewWorkflow.RunRequested += async (workflowFilePath, bindingsFilePath) =>
        {
            var taskDirectoryPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(workflowFilePath)!,
                $"task-{DateTime.Now:yyyyMMdd-HHmmss}");
            ViewModel.CurrentSection = ShellSection.Task;
            await RunAsync(taskDirectoryPath, workflowFilePath, bindingsFilePath);
        };
        // #211: the Outputs preview box is imperative control state, not bound — nothing cleared
        // or refreshed it when the drill-in moved to a different step, so it kept showing the
        // previously-selected step's last-previewed file. Clear on every change, then auto-load
        // the new step's first output (if it has one) so a freshly-opened step's Outputs tab isn't
        // just an unexplained blank box either.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SelectedStep))
            {
                _ = ShowSelectedStepFirstOutputAsync();
            }
        };
        Closed += (_, _) =>
        {
            _liveRefreshTimer.Stop();
            _pairingCountdownTimer.Stop();
        };
        Closing += OnClosing;
    }

    /// <summary>
    /// #217: keeps the custom title bar's maximize/restore glyph in sync with <see cref="Window.WindowState"/>
    /// regardless of what changed it — this window's own two maximize entry points, but also Aero
    /// Snap, the taskbar, and Win+Up/Down, none of which route through this window's own click handlers.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            UpdateMaximizeRestoreIcon((WindowState)change.NewValue!);
        }
    }

    /// <summary>The #211 hook above's body — its own method so a test can await it deterministically instead of racing the fire-and-forget subscription.</summary>
    private Task ShowSelectedStepFirstOutputAsync()
    {
        ArtifactPreviewBox.Text = string.Empty;
        var firstOutput = ViewModel.SelectedStep?.OutputFiles.FirstOrDefault();
        return firstOutput is null ? Task.CompletedTask : firstOutput.PreviewCommand.ExecuteAsync(null);
    }

    // ── #217: the custom title bar's own chrome. MainWindow.axaml marks the bar and its three
    // buttons with chrome:WindowDecorationProperties.ElementRole (TitleBar/MinimizeButton/
    // MaximizeButton/CloseButton), which is what gives native drag-to-move, double-click-to-
    // maximize, and Aero-Snap/taskbar integration on platforms that honor it. The handlers below
    // are a second, always-active path to the same four actions — belt-and-suspenders for
    // whichever platform or input device doesn't route through the non-client role. ─────────────

    /// <summary>Drag-to-move: <see cref="Window.BeginMoveDrag"/> on any left-press over the bar's empty space (the icon/title label is IsHitTestVisible="False"; the three buttons handle their own clicks and never bubble a press up to this handler).</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>Double-click-to-maximize: the one title-bar convention <see cref="OnTitleBarPointerPressed"/>'s drag doesn't already cover for free.</summary>
    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximizeRestore();

    [StructLayout(LayoutKind.Sequential)]
    private struct STYLESTRUCT
    {
        public int styleOld;
        public int styleNew;
    }

    private const uint WM_STYLECHANGING = 0x007C;
    private const int GWL_STYLE = -16;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_SYSMENU = 0x00080000;

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_STYLECHANGING && (int)wParam == GWL_STYLE)
        {
            var styleStruct = Marshal.PtrToStructure<STYLESTRUCT>(lParam);
            styleStruct.styleNew |= WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
            Marshal.StructureToPtr(styleStruct, lParam, false);
        }
        return IntPtr.Zero;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object? sender, RoutedEventArgs e) => ToggleMaximizeRestore();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximizeRestore()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>Swaps the maximize button between a square (maximize) and an overlapping-squares
    /// glyph (restore) — the same convention every platform's own caption buttons use, so a
    /// maximized window doesn't offer the same "maximize" affordance twice.</summary>
    private void UpdateMaximizeRestoreIcon(WindowState state)
    {
        var isMaximized = state == WindowState.Maximized;
        MaximizeButtonIcon.Data = Geometry.Parse(isMaximized
            ? "M 3,5 L 9,5 L 9,11 L 3,11 Z M 5,5 L 5,3 L 11,3 L 11,9 L 9,9"
            : "M 3,3 L 11,3 L 11,11 L 3,11 Z");
        MaximizeButton.SetValue(ToolTip.TipProperty, isMaximized ? "Restore" : "Maximize");
    }

    /// <summary>
    /// Populates Home's cards + inbox from Local UI Configuration (UI spec §3.1), plus (M15
    /// Phase 1) pre-fills the Run action's bindings/template inputs from whatever was last
    /// remembered — call once at startup.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _ = _session.EnsureDaemonConnectedAsync(cancellationToken);

        await RefreshHomeAsync(cancellationToken);

        BindingsFilePathBox.Text = await _session.LoadLastBindingsFilePathAsync(cancellationToken);
        WorkflowTemplatePathBox.Text = await _session.LoadLastWorkflowTemplateFilePathAsync(cancellationToken);
    }

    /// <summary>
    /// The full "open a task directory" action (UI spec §3.1): loads and renders it via
    /// <see cref="LoadAsync"/>, then — only on success — records it as the most recently opened
    /// directory and starts/stops live re-projection (M14 Phase 2, issue #119) depending on whether
    /// the projected workflow has reached a terminal state. This is what <see cref="OpenButton"/>
    /// and a Home task card's Open both call; <see cref="App"/>'s CLI-argument
    /// launch path calls it too, so a directory opened that way is remembered exactly like one
    /// opened by hand.
    /// <para>
    /// If <paramref name="taskDirectoryPath"/> names a file rather than a directory, it is opened as
    /// a raw <c>WorkflowDefinition</c> template instead (M14 Phase 3, issue #120: the DAG view
    /// renders both bound tasks and not-yet-instantiated templates). A template is not a task —
    /// there is no execution state to remember a re-projection cadence for, so it is neither
    /// recorded to <see cref="LocalUiConfigurationStore"/> (that store is task-directory recents
    /// specifically, per its Phase 2 decision of record) nor live-refreshed.
    /// </para>
    /// </summary>
    public async Task OpenAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        TaskDirectoryPathBox.Text = taskDirectoryPath;
        // Opening a task is the one navigation the shell performs itself: whatever this open
        // renders (a projection or its honest error message) renders in the Task view.
        ViewModel.CurrentSection = ShellSection.Task;

        if (File.Exists(taskDirectoryPath) && !Directory.Exists(taskDirectoryPath))
        {
            _session.SetCurrentTaskDirectory(null);
            await LoadTemplateAsync(taskDirectoryPath, cancellationToken);
            _liveRefreshTimer.Stop();
            return;
        }

        _session.SetCurrentTaskDirectory(taskDirectoryPath);

        await LoadAsync(taskDirectoryPath, cancellationToken);

        if (_session.LastLoadSucceeded)
        {
            await _session.RecordOpenedAsync(taskDirectoryPath, cancellationToken);
            await RefreshHomeAsync(cancellationToken);
        }

        UpdateLiveRefreshTimer();
    }

    /// <summary>
    /// The mutation seam this phase exists to prove (issue #137): a Run action that either starts a
    /// fresh task from <paramref name="workflowTemplateFilePath"/> + <paramref name="bindingsFilePath"/>,
    /// or resumes an already-bound <paramref name="taskDirectoryPath"/> after a pause or stop — the
    /// same <c>RunCommand.ExecuteAsync</c> call <c>aer run</c> makes, reused in-process rather than
    /// spawning the installed binary (the seam decision this phase resolves). Bindings are never
    /// persisted in a task directory (M14 Phase 2's decision of record) and the template is only
    /// ever read on a fresh start (<see cref="RunOptions.WorkflowFilePath"/>'s own remarks), so both
    /// are asked for here rather than inferred — "ask, don't infer," the same discipline the recents
    /// list already follows for task-directory discovery (UI spec §3.1).
    /// <para>
    /// The pump itself runs on a background thread (<see cref="Task.Run(Func{Task})"/>): a live
    /// execution can take however long a real worker takes, and the UI thread must never await that
    /// directly. This method starts <see cref="MainWindow"/>'s existing 2-second poller
    /// (<see cref="_liveRefreshTimer"/>) immediately, before the pump even begins, so it is what
    /// renders progress for the run's entire duration — this method itself only touches projection
    /// controls once more, via <see cref="OpenAsync"/>, after the pump has already reached its
    /// fixed point.
    /// </para>
    /// </summary>
    public async Task RunAsync(
        string taskDirectoryPath, string? workflowTemplateFilePath, string bindingsFilePath, CancellationToken cancellationToken = default)
    {
        TaskDirectoryPathBox.Text = taskDirectoryPath;
        // Kept in sync here, not just read from at Run-button-click time, so a later decision —
        // whose bindings path the session asks this same box for at call time ("ask, don't infer",
        // M14 Phase 2's decision of record) — has a value even when RunAsync was invoked directly
        // (a test, or a future non-button caller) rather than through the click handler.
        BindingsFilePathBox.Text = bindingsFilePath;

        await _session.RunAsync(taskDirectoryPath, workflowTemplateFilePath, bindingsFilePath, cancellationToken);
    }

    /// <summary>
    /// Starts a fresh template-editing session over a blank in-memory <see cref="WorkflowDefinition"/>
    /// (M16 Phase 1, issue #150) — nothing touches disk until <see cref="SaveTemplateAsync"/>.
    /// Deliberately synchronous: there is no file to read.
    /// </summary>
    public void NewTemplate() => ViewModel.TemplateEditor.StartNewFile();

    /// <summary>
    /// Opens <paramref name="templateFilePath"/> into the template editor (M16 Phase 1, issue #150)
    /// via the engine's own <see cref="TemplateProjectionLoader"/>/<c>WorkflowDefinitionParser</c> —
    /// never a second parser. This is a separate surface from <see cref="OpenAsync"/>'s read-only
    /// template projection (M14 Phase 3), which stays untouched: the read-only view is how a
    /// template (or a bound snapshot's diff against one) is *inspected*; this editor is how a
    /// template *file* is changed — and only ever a file, never a bound snapshot (UI spec §2, §4,
    /// §5). Phase 1 exposes exactly the metadata fields (<c>WorkflowTemplateId</c>,
    /// <c>WorkflowTemplateVersion</c>); the loaded steps ride through every save untouched until
    /// Phase 2's structural editing.
    /// </summary>
    public async Task OpenTemplateInEditorAsync(string templateFilePath, CancellationToken cancellationToken = default)
    {
        TemplateEditorPathBox.Text = templateFilePath;
        await ViewModel.TemplateEditor.OpenFromFileAsync(templateFilePath, cancellationToken);
    }

    /// <summary>
    /// Saves the editor's current state to <paramref name="templateFilePath"/> through
    /// <c>WorkflowDefinitionWriter</c>, so the saved file round-trips through the exact
    /// parser/validator every other consumer uses (M16 Phase 1, issue #150). Implements Flow spec
    /// §11.1's version-increment rule directly (settled ahead of this phase): a save whose content
    /// differs from the loaded baseline increments <c>WorkflowTemplateVersion</c> — unless the user
    /// explicitly set a different version themselves, which is respected as-is (a hand-editor may
    /// legitimately do the same) — a no-op save writes nothing and increments nothing, and a
    /// brand-new template's first save has no predecessor to distinguish from, so it saves the
    /// version as entered. Deliberately not gated on <see cref="MainWindowViewModel.IsMutationInFlight"/>:
    /// a template file is not durable task state, no §15 task lock is involved, and an edit is
    /// visible only to future instantiations regardless (UI spec §5).
    /// </summary>
    public async Task SaveTemplateAsync(string templateFilePath, CancellationToken cancellationToken = default)
        => await ViewModel.TemplateEditor.SaveToFileAsync(templateFilePath, cancellationToken);

    /// <summary>
    /// Starts a fresh worker-bindings editing session over an empty config (M16 Phase 4, issue #153)
    /// — nothing touches disk until <see cref="SaveBindingsAsync"/>. Deliberately synchronous: there
    /// is no file to read.
    /// </summary>
    public void NewBindings() => ViewModel.BindingsEditor.StartNewFile();

    /// <summary>
    /// Opens <paramref name="bindingsFilePath"/> into the bindings editor (M16 Phase 4, issue #153)
    /// via <see cref="BindingsProjectionLoader"/> — never a second parser. Bindings are a UI/CLI
    /// input, never durable task state (UI spec §4, §9; M14 Phase 2's decision of record), so unlike
    /// <see cref="OpenAsync"/> there is no read-only counterpart this editor has to stay separate
    /// from: authoring is the only surface a bindings file has in this UI.
    /// </summary>
    public async Task OpenBindingsInEditorAsync(string bindingsFilePath, CancellationToken cancellationToken = default)
    {
        BindingsEditorPathBox.Text = bindingsFilePath;
        await ViewModel.BindingsEditor.OpenFromFileAsync(bindingsFilePath, cancellationToken);
        RefreshBindingsTemplateCrossCheck();
    }

    /// <summary>
    /// Saves the bindings editor's current rows to <paramref name="bindingsFilePath"/> through
    /// <c>WorkerBindingConfigWriter</c>, so the saved file round-trips through the exact
    /// <c>WorkerBindingConfigParser.Parse</c> every other consumer uses (M16 Phase 4, issue #153,
    /// the same round-trip bar as Phase 1's template writer). Unlike <see cref="SaveTemplateAsync"/>,
    /// there is no version field to increment — a bindings file has no §11.1 counterpart.
    /// </summary>
    public async Task SaveBindingsAsync(string bindingsFilePath, CancellationToken cancellationToken = default)
    {
        await ViewModel.BindingsEditor.SaveToFileAsync(bindingsFilePath, cancellationToken);
        RefreshBindingsTemplateCrossCheck();
    }

    /// <summary>
    /// Recomputes <see cref="MainWindowViewModel.BindingsEditor"/>'s
    /// <see cref="BindingsEditorViewModel.MissingTemplateWorkerNames"/> (UI spec §9's advisory
    /// cross-check, M16 Phase 4's named open question) — which <c>Worker</c> names the template
    /// currently open <em>in the template editor</em> (<see cref="TemplateEditorViewModel.Baseline"/>)
    /// declares that have no entry in the bindings editor's own <see cref="BindingsEditorViewModel.Entries"/>.
    /// <para>
    /// <b>Source decision of record:</b> reads <c>ViewModel.TemplateEditor.Baseline</c> — the
    /// template-editing surface's own in-memory state — rather than the read-only DAG view's
    /// transient <c>LoadTemplateAsync</c> result, which is never retained as a field. This is a
    /// read-only consultation of already-computed state, not a change to template-editing code
    /// (Phases 1-3 own that; this phase excludes touching it) — nothing here writes to, or is called
    /// from, <see cref="TemplateEditorViewModel"/> or <see cref="OpenTemplateInEditorAsync"/>.
    /// </para>
    /// <para>
    /// Advisory display only, never a save gate (§9): bindings are deliberately not template data
    /// and never persisted in a task directory, so <see cref="SaveBindingsAsync"/> never consults
    /// this. Called explicitly — after New/Open/Save bindings and after adding a row — rather than
    /// wired to any template-editor change notification, since this phase does not touch that
    /// surface's events either.
    /// </para>
    /// </summary>
    private void RefreshBindingsTemplateCrossCheck()
        => ViewModel.BindingsEditor.RefreshTemplateCrossCheck(ViewModel.TemplateEditor.Baseline);

    /// <summary>
    /// Re-projects the currently open task directory in place (M14 Phase 2's change-observation
    /// requirement, issue #119) — a no-op if nothing has been opened yet. Public and directly
    /// awaitable for the same reason <see cref="LoadAsync"/> is (issue #118): a test can drive
    /// exactly one re-projection deterministically, rather than pumping the dispatcher and waiting
    /// on <see cref="_liveRefreshTimer"/>'s real elapsed-time tick, which is what actually calls this
    /// in production.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentTaskDirectoryPath is not { } currentTaskDirectoryPath)
        {
            return;
        }

        await LoadAsync(currentTaskDirectoryPath, cancellationToken);

        // While the poller is observing an open task, a visible Home stays live too — the cards
        // and inbox ride the same tick rather than owning a second timer (HomeViewModel's
        // scan-scope decision of record).
        if (ViewModel.IsHomeVisible)
        {
            await RefreshHomeAsync(cancellationToken);
        }

        UpdateLiveRefreshTimer();
    }

    /// <summary>
    /// The seam this phase exists to prove (issue #118), reaching the screen: loads
    /// <paramref name="taskDirectoryPath"/> through <see cref="TaskProjectionLoader"/> and renders
    /// its per-step statuses as plain <see cref="TextBlock"/> rows — deliberately minimal, per
    /// Phase 1's exclusion of "any styling worth defending". Public and directly awaitable (rather
    /// than fired from the constructor or a <c>Loaded</c> event) so a test can drive it
    /// deterministically without pumping the dispatcher on a timer. Extended in Phase 2 (issue #119)
    /// to also render the fuller <see cref="TaskProjection.History"/> surface, but
    /// <see cref="StatusText"/>/<see cref="StepsPanel"/>'s own rendering is untouched from Phase 1.
    /// </summary>
    public async Task LoadAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        var outcome = await _session.LoadAsync(taskDirectoryPath, cancellationToken);

        if (outcome.Projection is not { } projection)
        {
            // A real GUI has no stderr/exit-code convention to fail into (Aer.Cli's Program.cs
            // boundary) — an invalid task directory or a malformed snapshot/event log renders as an
            // in-window message instead. The session has already cleared the mutation surfaces.
            StatusText.Text = outcome.ErrorMessage;
            ClearProjectionPanels();
            ViewModel.IsTaskFinished = false;
            return;
        }

        ViewModel.IsTaskFinished = projection.State.Status == WorkflowStatus.Terminal;
        StatusText.Text = $"Workflow status: {projection.State.Status}";
        StepsPanel.Children.Clear();
        foreach (var step in projection.State.Steps)
        {
            StepsPanel.Children.Add(new TextBlock { Text = $"{step.StepId}: {step.Status}" });
        }

        var statusByStepId = projection.State.Steps.ToDictionary(step => step.StepId, step => step.Status);
        RenderDag(projection.Snapshot.Steps, statusByStepId);

        RenderExecutionHistory(projection);
        RenderDecisions(projection);
        RenderSupplementaryExecutions(projection);
        RenderArtifactLineage(projection, taskDirectoryPath);
        RenderConversationExecutions(projection, taskDirectoryPath);
        RenderConversation();

        // M19 Phase 3 (#188): the per-step drill-in — built after the session has rebuilt
        // PausedSteps, so each paused step's inline decision card is the same live VM instance.
        ViewModel.RebuildTaskSteps(
            projection, taskDirectoryPath,
            previewFileAsync: filePath => ShowArtifactPreviewAsync(filePath),
            showConversation: ShowConversation);
    }

    /// <summary>Clears every read-only projection panel — the error-path counterpart of a successful render, shared by task and template loads.</summary>
    private void ClearProjectionPanels()
    {
        StepsPanel.Children.Clear();
        DagCanvas.Children.Clear();
        HistoryPanel.Children.Clear();
        DecisionsPanel.Children.Clear();
        SupplementaryPanel.Children.Clear();
        LineagePanel.Children.Clear();
        ConversationExecutionsPanel.Children.Clear();
        ClearConversation();
        ArtifactPreviewBox.Text = string.Empty;
        DiffPanel.Children.Clear();
        ViewModel.ClearTaskSteps();
    }

    /// <summary>
    /// Renders a raw, not-yet-instantiated <see cref="WorkflowDefinition"/> template's DAG
    /// (M14 Phase 3, issue #120; UI spec §5, §10) — the counterpart to <see cref="LoadAsync"/> for
    /// paths that name a file rather than a task directory. There is no <see cref="FlowState"/> to
    /// overlay (a template is not bound to any task, so it has never executed) and no execution
    /// history/decisions/supplementary-execution surface either — those are all per-task facts;
    /// only the graph itself is meaningful for a template.
    /// </summary>
    private async Task LoadTemplateAsync(string templateFilePath, CancellationToken cancellationToken)
    {
        var outcome = await _session.LoadTemplateAsync(templateFilePath, cancellationToken);

        ViewModel.IsTaskFinished = false;

        if (outcome.Definition is not { } definition)
        {
            StatusText.Text = outcome.ErrorMessage;
            ClearProjectionPanels();
            return;
        }

        StatusText.Text =
            $"Template: {definition.WorkflowTemplateId} v{definition.WorkflowTemplateVersion} " +
            $"({definition.Steps.Count} step(s)) — not a task, no execution state.";
        ClearProjectionPanels();
        RenderDag(definition.Steps, statusByStepId: null);
        ViewModel.TaskHeadlineText = "A workflow file — not a running task.";
    }

    /// <summary>The one status system's token keys (M19 Phase 5, #190) — line color and area tint per <see cref="StepStatus"/>, resolved from the active theme at render time so the DAG follows light/dark like every other surface.</summary>
    private static readonly IReadOnlyDictionary<StepStatus, (string Border, string Background)> StatusTokenKeys =
        new Dictionary<StepStatus, (string, string)>
        {
            [StepStatus.Pending] = ("Status.Idle", "Status.IdleBg"),
            [StepStatus.Running] = ("Status.Running", "Status.RunningBg"),
            [StepStatus.Succeeded] = ("Status.Succeeded", "Status.SucceededBg"),
            [StepStatus.Failed] = ("Status.Failed", "Status.FailedBg"),
            [StepStatus.Cancelled] = ("Status.Idle", "Status.IdleBg"),
            [StepStatus.Paused] = ("Status.NeedsYou", "Status.NeedsYouBg"),
            [StepStatus.Rejected] = ("Status.Failed", "Status.FailedBg"),
        };

    // this.FindResource(key) (no theme argument) resolves against ThemeVariant.Default, never this
    // window's ActualThemeVariant -- it silently matches Tokens.axaml's literal x:Key="Default"
    // dictionary (the light palette) regardless of which variant the window is actually rendering
    // in, unlike XAML DynamicResource bindings, which are ActualThemeVariant-aware. Every DAG node
    // border/fill went through this before the fix, producing light-palette color against the
    // dark-palette inherited text -- the washed-out boxes.
    private IBrush Token(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Transparent;

    private const double DagCellWidth = 170;
    private const double DagCellHeight = 150;
    private const double DagNodeWidth = 150;
    // Tall enough for the icon plus up to 4 label lines (step id, worker, status, optional
    // pause line) at Type.Caption.FontSize — 56 fit the old text-only, 2-line label but let a
    // 3-4 line label with the #206 status icon on top spill past the border.
    private const double DagNodeHeight = 96;

    /// <summary>
    /// Renders <see cref="DagLayoutEngine.Layout"/>'s result over <paramref name="steps"/> as boxes
    /// (one per step, positioned by <see cref="DagNode.Rank"/>/<see cref="DagNode.Column"/>) joined
    /// by lines (one per <see cref="DagEdge"/>): solid for an ordinary <c>DependsOn</c> dependency,
    /// dashed for a declared <c>PausePoint.SupersedeTargets</c> entry (UI spec §10; issue #120).
    /// <paramref name="statusByStepId"/> is <c>null</c> for a raw template — nothing to overlay — or
    /// populated from the bound task's <see cref="FlowState"/> for a real task directory; either way
    /// every node still renders, just without a status-derived background in the template case.
    /// </summary>
    private void RenderDag(IReadOnlyList<WorkflowStepDefinition> steps, IReadOnlyDictionary<StepId, StepStatus>? statusByStepId)
        => RenderDag(
            DagLayoutEngine.Layout(steps), DagCanvas, statusByStepId,
            // M19 Phase 3 (#188): a node click opens that step's drill-in — task canvas only; the
            // template editor's preview has no task state to drill into.
            onNodeSelect: stepId => ViewModel.SelectStepById(stepId.Value));

    /// <summary>
    /// Re-layouts and renders <see cref="TemplateEditorViewModel.PreviewLayout"/> into
    /// <see cref="TemplateEditorDagCanvas"/> (M16 Phase 2, issue #151) — a dedicated canvas, not the
    /// read-only <see cref="DagCanvas"/>, so the editor's live preview can never collide with an
    /// independently-opened task or template's read-only rendering (Phase 1's separate-surfaces
    /// decision, extended to the graph view). <see langword="null"/> (an invalid or empty in-progress
    /// graph) clears the canvas rather than rendering a stale layout.
    /// </summary>
    private void RenderTemplateEditorDag()
    {
        if (ViewModel.TemplateEditor.PreviewLayout is not { } layout)
        {
            TemplateEditorDagCanvas.Children.Clear();
            TemplateEditorDagCanvas.Width = 0;
            TemplateEditorDagCanvas.Height = 0;
            return;
        }

        RenderDag(layout, TemplateEditorDagCanvas, statusByStepId: null);
    }

    private void RenderDag(
        DagLayout layout, Canvas canvas, IReadOnlyDictionary<StepId, StepStatus>? statusByStepId,
        Action<StepId>? onNodeSelect = null)
    {
        canvas.Children.Clear();

        if (layout.Nodes.Count == 0)
        {
            canvas.Width = 0;
            canvas.Height = 0;
            return;
        }

        var nodeByStepId = layout.Nodes.ToDictionary(node => node.StepId);

        foreach (var edge in layout.Edges)
        {
            var from = nodeByStepId[edge.From];
            var to = nodeByStepId[edge.To];

            var line = new Line
            {
                StartPoint = new Point(
                    from.Column * DagCellWidth + DagNodeWidth / 2,
                    from.Rank * DagCellHeight + DagNodeHeight),
                EndPoint = new Point(
                    to.Column * DagCellWidth + DagNodeWidth / 2,
                    to.Rank * DagCellHeight),
                Stroke = edge.IsSupersede ? Token("Status.Stale") : Token("Color.Border"),
                StrokeThickness = 1.5,
            };

            if (edge.IsSupersede)
            {
                line.StrokeDashArray = [4, 2];
            }

            canvas.Children.Add(line);
        }

        foreach (var node in layout.Nodes)
        {
            var status = statusByStepId?.GetValueOrDefault(node.StepId);
            // A bound task's node carries its status as border + tint (the one status system);
            // a raw template's node is a plain surface — nothing has executed, nothing to say.
            var (borderBrush, background) = status is { } knownStatus && StatusTokenKeys.TryGetValue(knownStatus, out var keys)
                ? (Token(keys.Border), Token(keys.Background))
                : (Token("Color.Border"), Token("Color.Surface"));

            var label = status is { } renderedStatus
                ? $"{node.StepId}\n{node.Worker}\n{renderedStatus}"
                : $"{node.StepId}\n{node.Worker}";

            if (node.HasPausePoint)
            {
                label += node.SupersedeTargets.Count > 0
                    ? $"\n[pause -> {string.Join(", ", node.SupersedeTargets)}]"
                    : "\n[pause]";
            }

            var textBlock = new TextBlock
            {
                Text = label,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                FontSize = this.FindResource("Type.Caption.FontSize") as double? ?? 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            // Post-M19 design review (#206/#209): status icon per node, same glyph set and mapping
            // as every other status-bearing surface — a raw template's node has no status, so no
            // icon (nothing has executed, nothing to say, same as its plain-surface color).
            var content = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Spacing = 4,
            };
            if (status is { } iconStatus)
            {
                content.Children.Add(new ShapePath
                {
                    Data = this.FindResource(Converters.StatusIconMap.GeometryKeyFor(iconStatus)) as Geometry,
                    Stroke = borderBrush,
                    StrokeThickness = 1.6,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                    Width = 14,
                    Height = 14,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                });
            }

            content.Children.Add(textBlock);

            var border = new Border
            {
                Width = DagNodeWidth,
                Height = DagNodeHeight,
                Background = background,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1.5),
                CornerRadius = this.FindResource("Radius.Medium") is CornerRadius radius ? radius : default,
                Child = content,
            };

            if (onNodeSelect is { } select)
            {
                var stepId = node.StepId;
                border.PointerPressed += (_, _) => select(stepId);
            }

            Canvas.SetLeft(border, node.Column * DagCellWidth);
            Canvas.SetTop(border, node.Rank * DagCellHeight);
            canvas.Children.Add(border);
        }

        var maxColumn = layout.Nodes.Max(node => node.Column);
        var maxRank = layout.Nodes.Max(node => node.Rank);
        canvas.Width = (maxColumn + 1) * DagCellWidth;
        canvas.Height = (maxRank + 1) * DagCellHeight;
    }

    /// <summary>
    /// Renders every step's full attempt history (not just the latest attempt
    /// <see cref="StepsPanel"/> already shows), plus its retry count, pause state, and declared
    /// <c>SupersedeTargets</c> — the read-model surface <see cref="Aer.Flow.Domain.FlowState"/>
    /// alone does not carry (issue #119).
    /// </summary>
    private void RenderExecutionHistory(TaskProjection projection)
    {
        HistoryPanel.Children.Clear();
        var stepDefinitionByStepId = projection.Snapshot.Steps.ToDictionary(step => step.StepId);

        foreach (var stepState in projection.State.Steps)
        {
            var attempts = projection.History.AttemptsByStepId.GetValueOrDefault(
                stepState.StepId, (IReadOnlyList<ExecutionAttempt>)[]);

            for (var index = 0; index < attempts.Count; index++)
            {
                var attempt = attempts[index];
                var classificationSuffix = attempt.FailureClassification is { } classification
                    ? $" ({classification})"
                    : string.Empty;
                var nonProcessSuffix = attempt.IsNonProcess ? " [non-process]" : string.Empty;

                HistoryPanel.Children.Add(new TextBlock
                {
                    Text = $"{stepState.StepId} attempt {index + 1}/{attempts.Count}: " +
                           $"{attempt.ExecutionId} -> {attempt.Status}{classificationSuffix}{nonProcessSuffix}",
                });
            }

            var summary = $"{stepState.StepId}: consecutive failures={stepState.ConsecutiveFailureCount}";
            if (stepState.Status == StepStatus.Paused)
            {
                var pausePoint = stepDefinitionByStepId[stepState.StepId].PausePoint;
                var supersedeTargets = pausePoint?.SupersedeTargets is { Count: > 0 } targets
                    ? string.Join(", ", targets)
                    : "none";
                summary += $", paused (underlying outcome={stepState.PausedOutcome}), supersede targets=[{supersedeTargets}]";
            }

            if (stepState.PendingSupplementaryExecutionId is { } pendingSupplement)
            {
                summary += $", pending supplementary execution={pendingSupplement}";
            }

            if (stepState.IsPendingSupersedeTarget)
            {
                summary += ", pending supersede dispatch";
            }

            HistoryPanel.Children.Add(new TextBlock { Text = summary });
        }
    }

    /// <summary>
    /// The Ctrl+C equivalent (§9's host-initiated stop; M15 Phase 4, issue #140): cancels whichever
    /// pump this window's own Run/Decide action currently has in flight — a no-op when nothing is.
    /// Fire-and-forget by design, mirroring <c>Aer.Cli.Program.cs</c>'s <c>Console.CancelKeyPress</c>
    /// handler: signalling <see cref="TaskSession.RequestHostStop"/> is only the signal.
    /// <see cref="RunAsync"/>/<see cref="DecideAsync"/>'s own awaited pump is what actually drives
    /// §9's intent-first record for every execution still in flight, then the durable
    /// <c>ExecutionCancelled</c> §7's second reflection phase needs, and clears
    /// <see cref="MainWindowViewModel.IsMutationInFlight"/> once that pump reaches its fixed point.
    /// </summary>
    public Task StopAsync()
    {
        _session.RequestHostStop();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Window-close semantics with a pump in flight (issue #140): the first <c>Closing</c> is
    /// cancelled and treated as a Stop request instead of a silent abandonment — the CLI's Ctrl+C
    /// equivalent still fires even though there is no terminal to Ctrl+C in. Once the retained pump
    /// task has actually reached its fixed point (<see cref="RunAsync"/>/<see cref="DecideAsync"/>'s
    /// own <c>finally</c> already reflects that in the projection via their trailing
    /// <see cref="OpenAsync"/>), this closes the window for real — a plain, uncancelled close, since
    /// <see cref="_closeConfirmed"/> is now set.
    /// </summary>
    public async void ConfirmCloseAndExit()
    {
        if (_session.IsDaemonConfigured)
        {
            Show();
            Activate();

            var hasRunningTasks = ViewModel.RunningExecutions.Count > 0;
            var result = await ExitConfirmationWindow.ShowPromptAsync(this, hasRunningTasks);
            if (result == null)
            {
                return;
            }

            _closeConfirmed = true;
            if (result == true)
            {
                _session.RequestHostStop();
                _ = _session.ShutdownDaemonAsync();
            }
            Close();
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
        else
        {
            _closeConfirmed = true;
            Close();
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_session.IsDaemonConfigured)
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        if (_session.IsDaemonConfigured)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        if (_session.CurrentPumpTask is not { IsCompleted: false } pumpTask)
        {
            return;
        }

        e.Cancel = true;
        _session.RequestHostStop();
        _ = CloseOncePumpSettlesAsync(pumpTask);
    }

    private async Task CloseOncePumpSettlesAsync(Task pumpTask)
    {
        try
        {
            await pumpTask.ConfigureAwait(true);
        }
        catch
        {
            // RunAsync/DecideAsync's own try/catch already renders any AerFlowException as an
            // in-window message on their own await of this same task; this second await exists only
            // to learn that the pump has reached a fixed point, not to re-observe its outcome.
        }

        _closeConfirmed = true;
        Close();
    }



    private void RenderDecisions(TaskProjection projection)
    {
        DecisionsPanel.Children.Clear();
        foreach (var decision in projection.History.Decisions)
        {
            var target = decision.TargetStepId is { } targetStepId ? $", target={targetStepId}" : string.Empty;
            var supplement = decision.SupplementaryExecutionId is { } supplementaryExecutionId
                ? $", supplement={supplementaryExecutionId}"
                : string.Empty;
            var resolution = decision.Resolved ? "resolved" : "unresolved";

            DecisionsPanel.Children.Add(new TextBlock
            {
                Text = $"{decision.DecisionId}: {decision.DecisionType} on {decision.ReferencedExecutionId}" +
                       $"{target}{supplement} ({resolution})",
            });
        }
    }

    private void RenderSupplementaryExecutions(TaskProjection projection)
    {
        SupplementaryPanel.Children.Clear();
        foreach (var execution in projection.History.StepLessExecutions)
        {
            var nonProcessSuffix = execution.IsNonProcess ? " [non-process]" : string.Empty;
            SupplementaryPanel.Children.Add(new TextBlock
            {
                Text = $"{execution.ExecutionId} ({execution.Worker}): {execution.Status}{nonProcessSuffix}",
            });
        }
    }

    /// <summary>
    /// Renders <see cref="ArtifactLineage"/> (M14 Phase 4, issue #121): one block per execution,
    /// naming its declared inputs' resolved producers, then a row of buttons — one per file actually
    /// present in its output directory — each wired to <see cref="ShowArtifactPreviewAsync"/>.
    /// <paramref name="taskDirectoryPath"/> is <see cref="LoadAsync"/>'s own parameter, not
    /// <see cref="TaskSession.CurrentTaskDirectoryPath"/>: <c>LoadAsync</c> is a supported, directly-callable
    /// entry point in its own right (issue #118) that a caller may invoke without ever going through
    /// <see cref="OpenAsync"/> (which is the only place that field is set) — the rendered buttons must
    /// resolve against the directory this exact call just loaded, not a field that might still be
    /// null or, worse, stale from a previously opened task.
    /// </summary>
    private void RenderArtifactLineage(TaskProjection projection, string taskDirectoryPath)
    {
        LineagePanel.Children.Clear();

        var artifactsRootPath = System.IO.Path.Combine(taskDirectoryPath, ArtifactsDirectoryName);

        foreach (var execution in projection.Lineage.Executions)
        {
            var header = execution.StepId is { } stepId
                ? $"{stepId} — {execution.ExecutionId} ({execution.Worker})"
                : $"(supplementary) — {execution.ExecutionId} ({execution.Worker})";
            LineagePanel.Children.Add(new TextBlock { Text = header, FontWeight = FontWeight.SemiBold });

            foreach (var link in execution.Inputs)
            {
                LineagePanel.Children.Add(new TextBlock
                {
                    Text = $"    input '{link.InputName}' <- {link.ProducerStepId} ({link.ProducerExecutionId})",
                });
            }

            if (execution.OutputFiles.Count == 0)
            {
                LineagePanel.Children.Add(new TextBlock { Text = "    (no output files)" });
                continue;
            }

            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, execution.ExecutionId);
            var filesPanel = new WrapPanel { Margin = new Thickness(16, 0, 0, 0) };
            foreach (var fileName in execution.OutputFiles)
            {
                var filePath = System.IO.Path.Combine(outputDirectory, fileName);
                var button = new Button { Content = fileName, Margin = new Thickness(0, 0, 4, 4) };
                button.Click += (_, _) => _ = ShowArtifactPreviewAsync(filePath);
                filesPanel.Children.Add(button);
            }

            LineagePanel.Children.Add(filesPanel);
        }
    }

    /// <summary>
    /// Reads one artifact file's content into <see cref="ArtifactPreviewBox"/> — "a file listing +
    /// plain-text preview," this phase's stated ceiling (issue #121), not content rendering of any
    /// kind beyond that. Public and directly awaitable, the same reason every other load-driving
    /// entry point on this window is (issue #118): a test can trigger exactly one preview
    /// deterministically instead of raising a real button-click event. Truncated defensively at
    /// <see cref="MaxArtifactPreviewLength"/> — an artifact is not guaranteed to be small or textual,
    /// and this preview is deliberately the cheapest thing that could show it, not a text-viewer.
    /// </summary>
    public async Task ShowArtifactPreviewAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var content = await File.ReadAllTextAsync(filePath, cancellationToken);
            ArtifactPreviewBox.Text = content.Length > MaxArtifactPreviewLength
                ? content[..MaxArtifactPreviewLength] + "\n… (truncated)"
                : content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ArtifactPreviewBox.Text = $"Cannot preview '{System.IO.Path.GetFileName(filePath)}': {ex.Message}";
        }
    }

    /// <summary>
    /// Rebuilds the conversation entry rows (M18 Phase 2, issue #178; UI spec §10.1): one row per
    /// execution whose durable output directory contains a transcript — discovery by content
    /// alone, never by which worker or binding produced the execution, so the row set is a pure
    /// projection of the artifact directories (§11), exactly like
    /// <see cref="RenderArtifactLineage"/>'s file buttons. Strictly per-execution: a retried or
    /// superseded step lists one row per attempt that recorded a transcript, each opening its own
    /// conversation.
    /// </summary>
    private void RenderConversationExecutions(TaskProjection projection, string taskDirectoryPath)
    {
        ConversationExecutionsPanel.Children.Clear();

        var artifactsRootPath = System.IO.Path.Combine(taskDirectoryPath, ArtifactsDirectoryName);

        foreach (var execution in projection.Lineage.Executions)
        {
            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, execution.ExecutionId);
            if (!TranscriptProjectionLoader.HasTranscript(outputDirectory))
            {
                continue;
            }

            var label = execution.StepId is { } stepId
                ? $"{stepId} — {execution.ExecutionId} ({execution.Worker})"
                : $"(supplementary) — {execution.ExecutionId} ({execution.Worker})";

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });

            var viewButton = new Button { Content = "View conversation" };
            viewButton.Click += (_, _) => ShowConversation(outputDirectory, label);
            row.Children.Add(viewButton);

            ConversationExecutionsPanel.Children.Add(row);
        }
    }

    /// <summary>
    /// Renders one execution's transcript as the conversation view (M18 Phase 2, issue #178) and
    /// remembers the selection so every subsequent <see cref="LoadAsync"/> re-renders it from the
    /// durable file — load-on-refresh (riding the same refresh/live-timer path as every other
    /// projection surface) is how the view follows a still-running exchange; there is deliberately
    /// no push/streaming channel (UI spec §10, live streaming assigned to no milestone). Public and
    /// directly callable for the same testability reason as <see cref="ShowArtifactPreviewAsync"/>.
    /// </summary>
    public void ShowConversation(string executionOutputDirectory, string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionOutputDirectory);
        ArgumentException.ThrowIfNullOrEmpty(label);

        _conversationOutputDirectory = executionOutputDirectory;
        _conversationLabel = label;
        RenderConversation();
    }

    private void ClearConversation()
    {
        _conversationOutputDirectory = null;
        _conversationLabel = null;
        ConversationPanel.Children.Clear();
    }

    private void RenderConversation()
    {
        ConversationPanel.Children.Clear();

        if (_conversationOutputDirectory is null)
        {
            return;
        }

        // A selection can legitimately point at nothing durable by the next refresh (the task
        // directory was deleted or recreated) — clear rather than render a guess (§12).
        if (TranscriptProjectionLoader.Load(_conversationOutputDirectory) is not { } transcript)
        {
            ClearConversation();
            return;
        }

        ConversationPanel.Children.Add(new TextBlock { Text = _conversationLabel, FontWeight = FontWeight.SemiBold });

        if (transcript.Lines.Count == 0)
        {
            ConversationPanel.Children.Add(new TextBlock { Text = "(transcript exists but records no turns yet)" });
            return;
        }

        foreach (var line in transcript.Lines)
        {
            ConversationPanel.Children.Add(line switch
            {
                TranscriptLine.Turn turn => RenderTurn(turn),
                TranscriptLine.Malformed malformed => new TextBlock
                {
                    Text = $"line {malformed.LineNumber}: not a schema-valid turn — left as recorded in {TranscriptProjectionLoader.TranscriptFileName}",
                    Foreground = Token("Status.Failed"),
                    TextWrapping = TextWrapping.Wrap,
                },
                _ => throw new InvalidOperationException($"Unknown transcript line kind: {line.GetType().Name}"),
            });
        }
    }

    private Border RenderTurn(TranscriptLine.Turn turn)
    {
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(new TextBlock
        {
            Text = $"{turn.Sequence} · {turn.Role} ({turn.Vendor})",
            FontWeight = FontWeight.SemiBold,
        });
        content.Children.Add(new TextBlock { Text = turn.Text, TextWrapping = TextWrapping.Wrap });

        // Prompt on demand only (the phase plan's default): durable and §12-traceable, but each
        // prompt embeds the entire prior transcript (M17's full-transcript threading), so
        // expanded-by-default would drown the conversation in its own repetition.
        content.Children.Add(new Expander
        {
            Header = "Prompt",
            IsExpanded = false,
            Content = new TextBlock { Text = turn.Prompt, TextWrapping = TextWrapping.Wrap },
        });

        return new Border
        {
            Background = Token("Color.Surface"),
            BorderBrush = Token("Color.BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = this.FindResource("Radius.Medium") is CornerRadius radius ? radius : default,
            Padding = new Thickness(8),
            Child = content,
        };
    }

    /// <summary>
    /// The snapshot-vs-template diff surface (UI spec §5; M14 Phase 4, issue #121): loads
    /// <paramref name="templateFilePath"/> via <see cref="TemplateProjectionLoader"/> and compares it
    /// against the currently open task's bound snapshot via <see cref="SnapshotTemplateDiffer"/>.
    /// Requires a task directory to already be open — <see cref="TaskSession.LastSnapshot"/> is only ever set by
    /// <see cref="LoadAsync"/>'s success path, never by opening a raw template on its own, since a
    /// template with nothing bound to it has nothing to diff against.
    /// </summary>
    public async Task CompareToTemplateAsync(string templateFilePath, CancellationToken cancellationToken = default)
    {
        DiffPanel.Children.Clear();

        if (_session.LastSnapshot is not { } snapshot)
        {
            DiffPanel.Children.Add(new TextBlock { Text = "Open a task directory before comparing it to a template." });
            return;
        }

        try
        {
            var template = await TemplateProjectionLoader.LoadAsync(templateFilePath, cancellationToken);
            RenderDiff(SnapshotTemplateDiffer.Diff(snapshot, template));
        }
        catch (AerFlowException ex)
        {
            DiffPanel.Children.Add(new TextBlock { Text = ex.Message });
        }
    }

    private void RenderDiff(SnapshotTemplateDiff diff)
    {
        DiffPanel.Children.Clear();

        if (diff.TemplateIdMismatch)
        {
            DiffPanel.Children.Add(new TextBlock
            {
                Text = "This file is a different template than the one the task is bound to — a " +
                       "mismatch, not a divergence; no diff is shown.",
            });
            return;
        }

        DiffPanel.Children.Add(new TextBlock
        {
            Text = $"Bound snapshot is template version {diff.SnapshotTemplateVersion}; " +
                   $"current template file is version {diff.TemplateVersion}.",
        });

        if (!diff.HasDiverged)
        {
            DiffPanel.Children.Add(new TextBlock { Text = "No divergence: the bound snapshot matches the current template." });
            return;
        }

        foreach (var addedStepId in diff.AddedStepIds)
        {
            DiffPanel.Children.Add(new TextBlock { Text = $"+ {addedStepId} (added in template; not in the bound snapshot)" });
        }

        foreach (var removedStepId in diff.RemovedStepIds)
        {
            DiffPanel.Children.Add(new TextBlock { Text = $"- {removedStepId} (in the bound snapshot; removed from the template)" });
        }

        foreach (var changedStep in diff.ChangedSteps)
        {
            var changedFields = new List<string>();
            if (changedStep.WorkerChanged)
            {
                changedFields.Add("worker");
            }

            if (changedStep.InputsChanged)
            {
                changedFields.Add("inputs");
            }

            if (changedStep.OutputsChanged)
            {
                changedFields.Add("outputs");
            }

            if (changedStep.DependsOnChanged)
            {
                changedFields.Add("dependsOn");
            }

            if (changedStep.RetryPolicyChanged)
            {
                changedFields.Add("retryPolicy");
            }

            if (changedStep.PausePointChanged)
            {
                changedFields.Add("pausePoint");
            }

            DiffPanel.Children.Add(new TextBlock { Text = $"~ {changedStep.StepId} changed: {string.Join(", ", changedFields)}" });
        }
    }

    /// <summary>Rebuilds Home's task cards and decision inbox from Local UI Configuration + durable task contents (M19 Phase 2, #187) — the successor of the M14 recents panel, now HomeViewModel's own read model.</summary>
    private Task RefreshHomeAsync(CancellationToken cancellationToken)
        => ViewModel.Home.RefreshAsync(_session, path => OpenAsync(path), cancellationToken);

    /// <summary>
    /// Polling, not a <see cref="System.IO.FileSystemWatcher"/> (issue #119's named open question):
    /// simplest thing that works identically across the win/linux/mac CI matrix without depending on
    /// a given filesystem's watch semantics inside a container. Runs only while a task is open and
    /// not yet <see cref="WorkflowStatus.Terminal"/> — once nothing further can change (spec §12),
    /// there is nothing left to observe.
    /// </summary>
    private void UpdateLiveRefreshTimer()
    {
        if (_session.ShouldLiveRefresh)
        {
            _liveRefreshTimer.Start();
        }
        else
        {
            _liveRefreshTimer.Stop();
        }
    }
}
