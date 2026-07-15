# AER Flow — Implementation Plan

The behavioral spec (`spec/aer-flow-behavioral-spec-v1.0.md`) is authoritative for what the system must guarantee. This document is authoritative for how we are getting there: which subsystems exist, how they group into milestones, and — for the current milestone — what the phase breakdown is.

**Session prompt:** The behavioral spec is authoritative. `IMPLEMENTATION_PLAN.md` is authoritative for sequencing. Help implement the current phase only.

---

## Capability Map

What subsystems exist, derived from the spec. Not chronological — this is architecture, not a build order.

| # | Subsystem | Spec reference |
|---|---|---|
| 1 | **Log Manager** | Atomic append to `flow.jsonl`; fsync write-before-dispatch ordering | §5, §7 |
| 2 | **State Projector** | `Project(EventStore, Snapshot) → FlowState`; causal linking by `ExecutionId` | §12, §13 |
| 3 | **Template Parser** | Load and validate `WorkflowDefinition` from file | §11.1 |
| 4 | **Snapshot Binder** | Freeze template into immutable `WorkflowDefinitionSnapshot` at task creation | §11.2 |
| 5 | **Dependency Resolver** | §11.3 readiness check: condition 1 (dependency succeeded) + condition 2 (staleness via `UpstreamExecutionIds`) | §11.3 |
| 6 | **Artifact Manager** | Pre-allocate `artifacts/execution_{N}/`; assign immutable input/output paths before dispatch | §16 |
| 7 | **Core Dispatcher** | Emit `ExecutionRequest` to aer-core M5 binding; receive `AerEvent` callbacks | §3, §12 |
| 8 | **Outcome Classifier** | Map Core exit reason + output existence to `ExecutionSucceeded/Failed/Cancelled` | §8 |
| 9 | **Contract Validator** | Assert all `ProducedOutputs` exist on disk before classifying as succeeded | §8 |
| 10 | **Retry Engine** | On `ExecutionFailed`, generate new `ExecutionRequest` with new `ExecutionId` per `RetryPolicy` | §10 |
| 11 | **Mutation Interface** | Single entry point for all external state changes; no other mutation path exists | §14 |
| 12 | **Concurrency Guard** | At most one writer per task namespace; file lock (not sentinel file) | §15 |
| 13 | **Pause Engine** | `PausePoint` handling; emit `WorkflowPaused`; idle until decision arrives | §17.1 |
| 14 | **External Decision Handler** | `ExternalDecisionRecorded`; `Resume/Reject/RetryWithRevision/Supersede` | §17.2 |
| 15 | **Supersede + Invalidation Cascade** | New execution for superseded step; staleness propagates forward via §11.3 condition 2 automatically | §17.5 |
| 16 | **Human Worker Support** | Non-process `ExecutionRequest`; completion detected by file existence, not Core exit | §17.3 |

**Product layer** — subsystems beyond the v1.0 engine, from §21 (the CLI is the pump), the adapter spike (#21), and the UI spec. These are what turn the engine library into a runnable product; introduced M11 onward.

| # | Subsystem | Reference |
|---|---|---|
| 17 | **Worker Adapter** | Canonical worker-invocation protocol; per-vendor CLI isolation (Claude, then Gemini/`agy`) behind `IWorkerAdapter` → `CoreDispatchTarget` | CLAUDE.md rule #2; §3, §4; #21 |
| 18 | **CLI Pump** | `aer run`: load workflow + bindings, drive project → resolve → dispatch → await to a terminal state | §21 |
| 19 | **CLI Mutation Commands** | `aer decide` / `aer cancel` against a running or paused task | §14, §21; UI spec §7 |
| 20 | **Distribution** | `aer` as an installable `dotnet tool`; native-lib bundling | AER Overview §6 |
| 21 | **UI Projection** | Read model + views: deterministic reconstruction from bound snapshots, event stores, and artifact directories; DAG/timeline/lineage rendering | UI spec §1, §3, §10–§12 |
| 22 | **UI Control Surface** | The §7 user actions (approve/reject/retry-with-revision/send-back/cancel/start) mapped onto Flow's closed `DecisionType` set, exclusively via the mutation interface | UI spec §6, §7 |
| 23 | **UI Authoring** | Template/DAG/worker-binding editing with structural validation (cycles, `SupersedeTargets` ancestry); never touches a bound snapshot | UI spec §5, §8, §9 |

---

## Milestone Roadmap

Which milestone introduces which capabilities.

| Milestone | Capabilities introduced | Blocked by |
|---|---|---|
| **M7: Foundation** | 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12 | aer-core M5 |
| **M8: Reactive Scheduler** | 10 (Retry Engine); full fan-out/fan-in DAG testing; manifest cache if scale demands | M7 |
| **M9: External Decisions** | 13, 14, 15, 16 (all pause/decision/supersede/human machinery) | M8 |
| **M10: Cancellation & Edge Cases** | §9 cancellation flow; crash recovery hardening (§7 full robustness) | M9 |
| **M11: First Real Run** | 17 (Worker Adapter — Claude only), 18 (CLI Pump) | M10; live aer-core M5 |
| **M12: Full Control Surface** | 17 (Gemini/`agy` adapter), 19 (`decide`/`cancel`); canonical protocol generalized across vendors | M11 |
| **M13: Distribution** | 20 | M11 |
| **M14: UI Projection** | 21 | M11 |
| **M15: UI Control Surface** | 22 | M14; M12 (`aer decide`/`cancel`/`supply` — the mutation-interface callers it wraps) |
| **M16: UI Authoring** | 23 | M14 |

M7–M10 complete the **v1.0 engine** (the behavioral spec is authoritative for it, and every §5.1 flow event now has a producer). M11 onward turns that engine into a runnable product: the worker adapters and the CLI pump the specs assume (§21, CLAUDE.md rule #2) but no engine milestone built, then distribution and — separately — the v0.7 UI.

M14–M16 are that UI track, splitting the roadmap's original single "UI" row the same way the engine split into M7–M10: **projection first** (capability 21 — every other UI capability renders on top of the read model), then the **control surface** (22) and **authoring** (23) as independent tracks behind it — M15 and M16 don't depend on each other, only on M14. Conversation-style views and live Observation-Tier turn streaming (UI spec §10) are deliberately assigned to *no* milestone: they depend on Case 2 encapsulated multi-model workers (Flow spec §18.2) that don't exist yet, and Overview §6's rule is to build the second concrete thing before generalizing for it.

---

## M15: UI Control Surface — Phase Plan

**Goal:** the second UI-track milestone (capability #22; UI spec §6, §7): the §7 user actions — start/resume a workflow, approve, reject, retry (with or without a revision), send back, cancel — exposed in `Aer.Ui`, implemented exclusively by invoking Flow's mutation interface (§14), and mapped onto Flow's closed `DecisionType` set: human-facing labels on top ("Approve" renders, `Resume` is recorded), never a UI-invented decision type (§6). No authoring (M16); projection itself shipped in M14 and only gets consumed here.

Three facts shape the plan. First, **the mutation callers already exist and are library-shaped.** M12's command layer — `RunCommand`/`DecideCommand`/`CancelCommand`/`SupplyCommand.ExecuteAsync`, static, adapter-registry-as-argument (M11's decision of record), returning `CommandResult` (M12's) — is already driven in-process by the smoke tests; the roadmap's "M15 depends on M12" names exactly this surface. Second, **a live execution can only be cancelled from inside the pump's own host process.** §15's lock is held for a mutation call's entire duration, so a second process's `aer cancel` throws `WorkflowLockedException` while a pump is in flight, and M10's decision of record makes the caller-retained in-process `InFlightExecutionRegistry` the only targeted-cancel delivery point. §7 requires Cancel on a running execution — so the UI must itself host the pump for runs it starts, which pulls Phase 1's seam decision hard toward in-process command-layer reuse over spawning the installed `aer` binary (the decision is Phase 1's to record, not this plan's to pre-commit — M14's stack-decision convention). One consequence sits on the critical path: the command layer doesn't yet expose the registry (it lets `MutationInterface` default one internally), an additive signature change Phase 4 owns. Third, **every mutation entry point is itself a pump** that runs to a fixed point, dispatching real workers for possibly minutes — so every UI action is invoked off the UI thread and its progress observed through M14 Phase 2's existing polling re-projection; the UI thread never awaits a pump. A smaller fourth: **bindings are a UI input** — never persisted in the task directory (M14 Phase 2's decision of record) — so "which bindings file" gets the established ask-don't-infer answer, with anything remembered being Local UI Configuration (§4).

Like M14, nothing in M15 can ever need live vendor auth: every action drives the same mutation surface M11/M12 already proved live, so the milestone gate is UI-driven round trips over shell-stub workers, wired into default CI.

### Phase dependency table

| Phase | Requires output from |
|---|---|
| 1 — Mutation seam + start/resume a workflow | — |
| 2 — Resolve decisions: Approve / Reject | 1 |
| 3 — Artifact-carrying decisions: Retry-with-revision + Send-back | 2 |
| 4 — Cancel: targeted live-execution cancel + host stop | 1 |
| 5 — UI-driven mutation round trips in default CI | 2 + 3 + 4 |

Phases 2 and 4 are independent once Phase 1 lands — the same fan-out shape as M14.

### Phase 1 — Mutation seam + start/resume a workflow (#137)
Resolves the one decision every other control-surface phase builds on — how the UI invokes the mutation interface — then proves it end to end: a Run action that starts a fresh task from a template + bindings file, or resumes an already-bound task after a pause or stop (§7's "Start a workflow" / "Resume a workflow after a pause"), invoked off the UI thread, with the existing poller rendering progress while the pump runs to its fixed point.

**Produces:** the UI can start or resume a real run and watch it progress live to `Paused`/`Terminal`.
**Excludes:** all decisions (Phases 2–3); cancel (Phase 4); any authoring of templates or bindings (M16).
**Open questions resolved in this phase:**
- **The mutation seam** — in-process command-layer reuse vs. spawning the installed `aer` binary (dominant constraint above: targeted cancel is in-process-only by M10's construction).
- **Where the adapter registry comes from** — expected shape: a `MainWindow` constructor argument, the same production-wiring-is-the-caller's-decision seam as M14's `LocalUiConfigurationStore`; also what lets `Aer.Ui.Tests` substitute stub adapters (Phase 5 depends on this).
- **Where the bindings file (and template, for a fresh start) come from** — ask-don't-infer, with any remembered value as Local UI Configuration (§4).

### Phase 2 — Resolve decisions: Approve / Reject (#138)
The paused-step action surface for §17.2's two artifact-less decisions: a paused execution renders its available actions; "Approve" records `DecisionType: Resume`, "Reject" records `DecisionType: Reject`, each wrapping `DecideCommand`, each pumping to its next fixed point in the background exactly like Phase 1's run. §7's label-mapping discipline is the point of this phase.

**Produces:** a paused run can be approved or rejected from the UI and watched to its next fixed point.
**Excludes:** artifact-carrying decisions (Phase 3); cancel (Phase 4).
**Open questions resolved in this phase:**
- **Whether a ViewModel/MVVM layer enters now** — M14 deliberately deferred it, naming M15's interactive control surface as the potential second concrete need (buttons whose enabled/disabled state is tied jointly to projected state and an in-flight mutation may be it).
- **How mutation-in-flight is reflected** — actions disabled while the UI's own pump holds the task's lock; a `WorkflowLockedException` from a competing external pump renders via the in-window-message precedent (M14 Phase 1), never a crash.

### Phase 3 — Artifact-carrying decisions: Retry-with-revision + Send-back (#139)
The other half of §17.2's decision set, carrying §7's two explicit UI constraints. "Retry" re-runs the paused step (`RetryWithRevision`), optionally with a supplied revision file; "Send back to X" supersedes an earlier step's already-successful output (`Supersede`, `TargetStepId: X`). The supplementary artifact rides M12's `aer supply` semantics: the UI wraps `SupplyCommand` (mint + populate + settle in one call) then `DecideCommand` with the returned `ExecutionId` — the same two-call round trip the CLI established. §7's constraints, enforced in the UI: "send back to X" is offered *only* for `StepId`s in this pause point's declared `SupersedeTargets` (no send-back option at all when the list is empty — never offered-then-failed at the mutation interface), and the supplementary artifact is mandatory for `Supersede`, optional for `RetryWithRevision`.

**Produces:** the full §17.2 decision surface, with the invalidation cascade watched through the existing projection.
**Excludes:** cancel (Phase 4); any rendering of artifact content beyond what M14 Phase 4 already ships.

### Phase 4 — Cancel: targeted live-execution cancel + host stop (#140)
§7's Cancel, on both grains M10/M12 built. **Targeted:** a Cancel action on one specific running execution, delivered through the `InFlightExecutionRegistry` the UI retained before starting its own pump. **Host stop:** the Ctrl+C equivalent — stop everything in flight, `CancellationRequested` recorded for every live execution before any is signalled, including when the window closes with a pump still running. §7's two-phase reflection must be visible: "cancellation requested" as soon as `CancellationRequested` is observed, "stopped" only once `ExecutionCancelled` separately arrives.

**Produces:** running work can be cancelled from the UI — one execution or the whole run — with request and completion reflected as the two distinct facts they are.
**Excludes:** any retry/decision surface changes (Phases 2–3 own those).
**Open questions resolved in this phase:**
- **The additive command-layer signature change** exposing the caller-retained registry (today `RunCommand`/`DecideCommand` let `MutationInterface` default one internally, so no caller can reach an in-flight execution).
- **Window-close semantics** with a pump in flight.
- **What Cancel offers for executions this UI process is *not* hosting** — a targeted cancel can only be a new mutation call that blocks on §15's lock, so the likely answer is the locked-message surface, not a button that pretends to work.

### Phase 5 — UI-driven mutation round trips, wired into default CI (#141)
The M15 completion gate, placed by M14 Phase 5's reasoning: no live vendor auth is needed, so shell-stub workers cover it unattended. Headless-Avalonia tests drive the real `MainWindow` through full round trips on all three CI OSes: run → pause → Approve → terminal; pause → supply + Send-back → invalidation cascade → terminal; running → targeted Cancel → cancelled. `ShellCommandWorkerAdapter` is currently `Aer.Cli.Tests`-internal — whether it moves to a shared test utility or `Aer.Ui.Tests` grows its own stub registry is this phase's choice. M14's golden-projection gate must stay green throughout: the control surface adds mutation callers, never projection semantics.

**Produces:** M15 complete — every §7 action exercised end to end through the real UI, unattended, on win/linux/mac.
**Excludes:** any live-vendor runbook (deliberately — no new vendor-facing machinery exists to prove); authoring (M16).

---

## Current Milestone

**M15: UI Control Surface** — phase plan above. Progress:

- ✅ Phase 1 — Mutation seam + start/resume a workflow (#137)
- ✅ Phase 2 — Resolve decisions: Approve / Reject (#138)
- ⬜ Phase 3 — Artifact-carrying decisions: Retry-with-revision + Send-back (#139)
- ⬜ Phase 4 — Cancel: targeted live-execution cancel + host stop (#140)
- ⬜ Phase 5 — UI-driven mutation round trips in default CI (#141)

Per this document's session prompt: help implement the current phase only.

Decisions of record from M15:

- **The mutation seam is in-process reuse of `Aer.Cli.RunCommand.ExecuteAsync`** — `Aer.Ui` now
  references `Aer.Cli` and `Aer.Adapters` directly (new `ProjectReference`s), the same static,
  adapter-registry-as-argument call `Program.cs` makes for `aer run`, rather than spawning the
  installed `aer` binary. This is the seam every later phase's decision command builds on the same
  way (Phase 1).
- **The worker-adapter registry is a `MainWindow` constructor argument**, defaulting to
  `WorkerAdapterRegistry.Default` through the existing parameterless/one-argument constructors so
  no production caller has to name it — the same "production wiring is the caller's decision" seam
  `LocalUiConfigurationStore` established in M14 Phase 2. `Aer.Ui.Tests` substitutes a deterministic
  shell-stub registry (`MainWindowRunTests`) instead of resolving a live vendor CLI (Phase 1).
- **`RunOptions.WorkflowFilePath` is nullable** — a resume of an already-bound task directory never
  reads it (`RunCommand.ExecuteAsync` only binds a fresh snapshot when none is persisted yet), so
  `MainWindow.RunAsync` never has to ask the user for a template unless the task directory is
  actually starting fresh. A fresh start with no template given is a `CliArgumentException`, not a
  silent no-op (Phase 1).
- **Bindings and template file paths are asked for on every Run, never inferred** — bindings are
  never persisted in a task directory (M14 Phase 2's decision of record) and a template is only
  ever relevant on a fresh start. `LocalUiConfigurationStore` gained `LastBindingsFilePath`/
  `LastWorkflowTemplateFilePath` purely to pre-fill that ask, the same non-authoritative,
  rebuildable-convenience treatment as the existing recents list (Phase 1).
- **The pump runs via `Task.Run` inside `MainWindow.RunAsync`, and the UI thread never awaits it
  directly** — a real dispatch can take however long a worker takes; the existing 2-second
  `DispatcherTimer` poller (M14 Phase 2) is what renders progress while a Run is in flight.
  `RunAsync` itself only touches projection controls once, after the pump has already reached its
  fixed point (Phase 1).
- **`RunCommand`/`MutationInterface` were not given the caller-retained `InFlightExecutionRegistry`
  this phase** — deliberately deferred to Phase 4, which already owns that additive signature
  change per the phase plan above; Phase 1's Run action has nothing yet to target a cancel at
  (Phase 1).
- **MVVM enters now, scoped to the decision surface only** — `CommunityToolkit.Mvvm`
  (source-generator `[ObservableProperty]`/`[RelayCommand]`, no reactive-extensions dependency) is
  the new `Aer.Ui` `PackageReference`. `MainWindowViewModel`/`PausedStepViewModel` own exactly the
  surface M14 Phase 1 named as the potential second concrete need — buttons whose enabled state is
  tied jointly to projected state and an in-flight mutation — set as `MainWindow.DataContext`. The
  rest of the window's read-only rendering (DAG, history, lineage, diff) is untouched, still direct
  code-behind control manipulation; migrating it is a future decision this phase's Approve/Reject
  surface does not need to force (Phase 2).
- **§7's Approve/Reject label mapping**: `PausedStepViewModel.ApproveCommand` records
  `DecisionType.Resume`, `RejectCommand` records `DecisionType.Reject` — never a UI-invented decision
  type (UI spec §6). `MainWindow.RebuildPausedSteps` re-derives one `PausedStepViewModel` per step
  whose latest attempt is `StepStatus.Paused`, from `StepState.LatestExecutionId`, on every load —
  a projected fact, not retained handler state, so a step that resumes simply stops appearing next
  load (Phase 2).
- **One shared `IsMutationInFlight` flag, not a per-action one**, gates every mutation this UI
  process can start — `RunButton`'s bound `IsEnabled` and every `PausedStepViewModel`'s command
  `CanExecute` all read it, since the underlying §15 lock could not support two concurrent
  in-process mutations regardless. A `WorkflowLockedException` from a *competing external* pump
  still renders via the in-window-message precedent (M14 Phase 1) — this flag only ever prevents a
  second mutation from this same process, never claims to reach across processes (Phase 2).
- **The decision's worker-bindings path is read from `BindingsFilePathBox` at decide-time, not
  cached in a field** — the same "ask, don't infer" box `RunAsync` already asks for (Phase 1's
  decision of record); `RunAsync` now also writes its own `bindingsFilePath` argument back into that
  box so a decision has something to read even when `RunAsync` was invoked directly rather than
  through the Run button's click handler (Phase 2).

## Completed Milestones

Completed milestones keep only their phase checklist and any decisions of record later work
still leans on. The full phase plans — goals, boundaries, and the open questions each phase
resolved — live in this file's git history and in the linked issues.

**M14: UI Projection** — the first UI-track milestone: `Aer.Ui`, an Avalonia desktop app
consuming `Aer.Flow`'s read model in-process — task/execution/decision projection with live
polling, the DAG view, artifact lineage, the snapshot-vs-template diff, and a golden-projection
determinism gate in default CI. Read-only throughout: no mutations (M15), no authoring (M16).

- ✅ Phase 1 — Stack decision + walking skeleton (#118)
- ✅ Phase 2 — Task & execution projection + change observation (#119)
- ✅ Phase 3 — DAG view (snapshot topology + status overlay) (#120)
- ✅ Phase 4 — Artifact lineage + snapshot-vs-template diff (#121)
- ✅ Phase 5 — Golden-projection determinism gate, wired into default CI (#122)

Decisions of record from M14:

- **Stack: Avalonia, in this repo/solution, referencing `Aer.Flow` directly.** UI spec §13 treats
  the form factors as behaviorally equivalent, so the criteria were Overview §6 (single-developer
  tool — "run the exe" is the whole deployment story) and §11's determinism, which in-process
  read-model reuse inherits by construction, the same seam `Aer.Cli` proved for the write side.
  Avalonia over WPF/MAUI for genuine cross-platform (the existing three-OS CI matrix) and real
  vector graphics. Nothing needed a cross-language/cross-solution boundary, so Overview §7's
  default held: `Aer.Ui.csproj`/`Aer.Ui.Tests.csproj` are new leaves in `AerFlow.slnx` (Phase 1).
- **Project name is `Aer.Ui`, not `Aer.Flow.Ui`** — the UI is architecturally outside the trusted
  execution stack (UI spec §2) and must never read as part of Flow's namespace; `Aer.Cli` set the
  flat-naming precedent (Phase 1).
- **No ViewModel/data-binding layer** — code-behind against named controls is the simplest thing
  that renders the projection; an MVVM layer waits for a second concrete need, which M15's
  interactive control surface may be (Phase 1).
- **Async entry points are public and directly awaitable** (`LoadAsync`, `RefreshAsync`), never
  fired only from constructors, `Loaded` events, or timer ticks — the only way a test drives them
  deterministically without pumping the dispatcher or racing elapsed time. `OpenAsync` is the
  richer production entry (load + recents + live-refresh timer) the Open button, recents clicks,
  and CLI-argument launch all go through (Phases 1–2).
- **A failed load renders as an in-window message, not a crash** — a GUI has no stderr/exit-code
  convention to fail into; `MainWindow` catches `AerFlowException` itself (Phase 1).
- **UI tests drive the real `App`/`MainWindow` through `Avalonia.Headless`/`Avalonia.Headless.XUnit`**,
  offscreen, no display server — which forced `Aer.Ui.Tests` onto xunit v3, an isolated exception
  to the repo's xunit v2 convention, confined to this one project (Phase 1).
- **`ExecutionHistory`/`ExecutionHistoryProjector` is an `Aer.Ui`-only read-model type, not an
  addition to `FlowState`.** `StateProjector` deliberately collapses each step to its latest
  attempt (§12); full per-execution history is a presentation-layer fact re-derived from the same
  event list, never a dispatch-affecting one. `TaskProjection` carries `Snapshot`/`State`/`History`
  (Phase 2).
- **A non-process/human execution is identified by `ExecutionRequest.Timeout is null`** — the only
  signal already durable on disk once the read side has nothing but the event log and snapshot
  (bindings are never persisted to the task directory) (Phase 2).
- **Task-directory discovery is "ask the user, or pick a remembered one" — never a scanned root**
  (UI spec §3.1's implementation choice). `LocalUiConfigurationStore` is a small explicit JSON
  file store, deliberately non-authoritative per §3.1: missing/corrupt loads as empty, vanished
  paths silently drop, capped at 10 (Phase 2).
- **`MainWindow` takes its `LocalUiConfigurationStore` as a constructor argument** — production
  wiring is the caller's decision, the same seam as `RunCommand`'s adapter-registry argument;
  it's what points tests at a temp config file (Phase 2).
- **Change observation is polling via a 2-second `DispatcherTimer`, not `FileSystemWatcher`** —
  identical behavior across the three-OS matrix, re-read cost known cheap (M8's ~3.8ms finding);
  polling stops once `WorkflowStatus` reaches `Terminal` (Phase 2).
- **A separate release-please package for `Aer.Ui` was tried and reverted** — a same-repo
  `exclude-paths` split can't work while every phase commit also touches this file (a root-level
  path), and upstream `exclude-paths` reliability is poor in manifest mode
  ([release-please#2301](https://github.com/googleapis/release-please/issues/2301), [#2230](https://github.com/googleapis/release-please/issues/2230)).
  The real fix is a separate repo, not worth reopening the placement decision for; `Aer.Ui` stays
  on the shared root version.
- **`DagLayoutEngine.Layout` takes `IReadOnlyList<WorkflowStepDefinition>` directly** — the shape
  both a raw template and a bound snapshot expose, so one graph view covers both and only the
  status overlay branches. Layering is longest-path-from-root, columns in declaration order; all
  output order derives from walking the input lists, never `Dictionary`/`HashSet` enumeration
  order — as deterministic as §11 requires, assertable by the golden gate (Phase 3).
- **`TemplateProjectionLoader` is a separate loader, not a branch inside `TaskProjectionLoader`**
  (different durable-state shapes); `MainWindow.OpenAsync` routes on `File.Exists` vs.
  `Directory.Exists` — a template file and a task directory are never ambiguous on disk. Opening a
  template records no recents and starts no live-refresh timer (Phase 3).
- **`ArtifactLineageProjector` walks each recorded `ExecutionRequest`'s `UpstreamExecutionIds`
  directly — never `ArtifactManager.ResolveInputPaths` or the current `FlowState`.** Which
  execution fed an input is recorded once, at dispatch time; re-deriving against today's state
  would substitute a step's current latest execution for the one actually consumed. Producers are
  found by matching the snapshot's declared `Inputs` names against each `DependsOn` step's
  declared `Outputs` (Phase 4).
- **A `WorkflowTemplateId` mismatch is `TemplateIdMismatch`, never folded into `HasDiverged`** —
  divergence means the *same* template changed, not that the wrong file was compared;
  `WorkflowTemplateVersion` is informational, never part of the predicate (Phase 4).
- **There is no durable link from a bound task back to its template file** (snapshot carries
  id/version only — confirmed against `WorkflowDefinitionSnapshot`, `SnapshotBinder`, and every
  `FlowEvent`), so the diff surface takes the template path from the user — ask, don't infer
  (Phase 4).
- **`GoldenProjectionCanonicalizer` tokenizes runtime-minted IDs by first appearance and sorts
  only the `Dictionary`/`HashSet`-backed fields** — every List-backed field stays in its natural
  walk-derived order, because that order *is* the §11 determinism property the gate exists to
  check; re-sorting would hide real ordering bugs (Phase 5).
- **Golden files are bootstrapped/refreshed only via opt-in `AER_UPDATE_GOLDEN_FILES=1`**, writing
  to the source-tree fixture path so a reviewable diff is the only way a golden changes (Phase 5).
- **Fixture hazard, for anyone authoring pumped fixtures:** steps sharing one `Worker` name but
  declaring different `Outputs` are safe as never-pumped templates but dispatch-unsafe once
  pumped — one shared `WorkerBinding` makes `OutcomeClassifier` check the wrong step's output.
  Give every step a distinct worker name (`paused-run-workflow.json` vs. the older
  `diamond-workflow-with-pause.json`) (Phase 5).

**M13: Distribution** — turned `aer` from a checkout-only build into an installable
`dotnet tool`: single-platform packing, version wiring from `release-please`, multi-RID
native-lib bundling, and an unattended CI round-trip check proving install → run → uninstall
works with no live vendor auth (`pixi run verify-pack`, `scripts/verify-pack-roundtrip.sh`).

- ✅ Phase 1 — Pack `aer` as a `dotnet tool` (single-platform) (#107)
- ✅ Phase 2 — Version wiring (release-please → package `Version`) (#108)
- ✅ Phase 3 — Multi-RID native-lib bundling (Windows/Linux/macOS) (#109)
- ✅ Phase 4 — Installed-tool round-trip check (wired into default CI) (#110)

Decisions of record from M13:

- **The version's single source of truth is a root `Directory.Build.props` `<Version>`**, bumped
  directly by a release-please `extra-files` XML entry on every release PR merge — visible to
  every local build, not just CI. `IncludeSourceRevisionInInformationalVersion` is explicitly
  `false`, so the tool's reported version equals `CHANGELOG.md`'s plain entry (Phase 2).
- **A single fat package, not three RID-qualified packages**: the CI `test` matrix (now including
  a real `macos-latest` job) uploads each OS's own `cargo build` output; the `pack` job gathers
  the other two platforms into `artifacts/native-libs/<rid>/` (gitignored, a hand-off point only).
  The pack items are `Condition="Exists(...)"`, so a plain local `pixi run pack` with no gathered
  artifacts still works (Phases 1, 3).
- **No runtime OS-detection/P/Invoke-resolution code was needed**: `DllImport` references the bare
  name `aer_core` and .NET's default probing appends the host-appropriate prefix/extension, so all
  three platform binaries coexist in the flat `tools/.../any/` directory (Phase 3).
- **No extra MSBuild plumbing for the native lib**: `PackAsTool` packs from a *publish* output,
  which already folds in `Aer.Core.csproj`'s existing `Content` copy of `aer_core` (Phase 1).
- **`PackageId`/`ToolCommandName` are both `aer`** — no public feed exists to collide with
  (Phase 1).
- **The round-trip check is a plain bash script in the CI `pack` job** (not a `dotnet test`, not a
  gated runbook — nothing needs live vendor auth): it drives the literal `README.md` install/run/
  uninstall commands, so the script *is* the documentation, verified. Its "no live vendor" trick
  is stubbing the `claude` binary itself ahead on `PATH` — `WorkerAdapterRegistry.Default` (what
  an installed `aer` actually wires) only resolves `claude`/`gemini`, so the test-only `shell`
  adapter is unreachable from a real installed tool; the stub satisfies the output contract by
  reading `AER_OUTPUT_DIR` directly (Phase 4).

**M12: Full Control Surface** — the milestone that made the runnable library drivable: a second
vendor (Gemini's `agy`) behind M11's unchanged protocol, and the mutation surface M9/M10 built
exposed as `aer decide`/`aer cancel`, proven by a live mixed-vendor paused run decided from the
terminal (`docs/runbooks/live-mixed-vendor-smoke.md`).

- ✅ Phase 1 — Gemini worker adapter (headless `agy` CLI) (#95)
- ✅ Phase 2 — `aer cancel` + Ctrl+C host-stop wiring (#96)
- ✅ Phase 3 — `aer decide` + supplementary artifact recording (#97)
- ✅ Phase 4 — Live mixed-vendor paused run (gated end-to-end) (#98)

Decisions of record from M12:

- **`aer supply` mints, populates, and settles a supplementary execution in one call.**
  `RecordSupplementaryExecutionAsync` deliberately never runs the pump (§17.3: minting alone
  changes no readiness), so `aer supply` calls `StartWorkflowAsync` itself after copying `--file`
  into the assigned output directory — the supply → decide round trip is two CLI invocations, and
  the transient `WorkerContract` a supplementary role needs never has to be reconstructed across
  invocations (Phase 3).
- **The non-process `WorkerBinding` a supplementary execution dispatches under is constructed
  directly from `--worker`/`--output`, never looked up in the bindings file** — worker-binding
  config entries only ever resolve to `WorkerBinding.Process` (M11's decision of record), and this
  phase didn't reopen that. `aer supply` is scoped to a single declared output from a single
  `--file`; a multi-output supplementary execution is a hypothetical it declines to design for
  (Phase 3).
- **`aer run`/`aer cancel`/`aer decide` all return a `CommandResult` (`FlowState` + the bound
  snapshot), not a bare `FlowState`** — pause-aware reporting (a paused step's `SupersedeTargets`)
  is only resolvable against the snapshot; `FlowStateReporter` is the one shared formatter
  (Phase 3).
- **The input-directory grant is one vendor-neutral env var**: `ArtifactManager.BuildEnvironment`
  emits `AER_ARTIFACTS_ROOT` unconditionally (inputs and output are sibling directories under one
  root, §16); `GeminiWorkerAdapter` grants it once via `--add-dir`; `ClaudeWorkerAdapter` simply
  never references it (Phase 1).
- **The registry key is the vendor name, not the binary name** (`"gemini"`, though the binary is
  `agy`); `agy` is shell-wrapped with stdin redirected exactly like Claude (free insurance against
  the same stall class), and its scoped-permission flag is `--mode`, default `"accept-edits"` —
  further confirmation `PermissionScope` stays an opaque, adapter-interpreted string (Phase 1).
- **Phase 4's live gate recorded green 2026-07-13** (a host that happened to carry both vendors
  authenticated — a coincidence, not a capability; see CLAUDE.md). The first live attempt caught a
  real Windows-only bug in *both* adapters: each built one pre-quoted `cmd /c "..."` string, which
  aer-core's Windows spawn re-quoted and corrupted — fixed by passing each token as its own `Args`
  element on Windows (see `live-mixed-vendor-smoke.md`) (Phase 4).

**M11: First Real Run** — the milestone that made the library runnable: the canonical
worker-invocation protocol and adapter seam, the Claude adapter, the `aer run` pump, and a
recorded green live two-step run (`docs/runbooks/live-claude-smoke.md`).

- ✅ Phase 1 — Canonical worker-invocation protocol + `Aer.Adapters` seam (#84)
- ✅ Phase 2 — Claude worker adapter (headless `claude` CLI) (#85)
- ✅ Phase 3 — `aer run` pump (the CLI driver) (#86)
- ✅ Phase 4 — Live two-step Claude run (gated end-to-end) (#87)

Decisions of record from M11:

- **Live gates live in `Aer.Cli.SmokeTests`, deliberately absent from `AerFlow.slnx`** — default
  CI never discovers them, with no trait-based filtering; `pixi run smoke-*` targets the project
  directly, and a runbook per gate documents prerequisites and triage (Phase 4).
- **A worker role that reads an upstream artifact needs `Read` in its `PermissionScope`, not just
  `Write`** — a per-worker config fact (`PermissionScope` is opaque and adapter-interpreted), not
  engine behavior; the runbook calls it out for config authors (Phase 4).
- **`RunCommand.ExecuteAsync` takes the adapter registry as a plain argument, never constructing
  one** — `Program.cs`'s only production wiring decision is passing `WorkerAdapterRegistry.Default`;
  this is what lets tests reach the real adapter/bindings seam with a deterministic
  `ShellCommandWorkerAdapter` instead of a live LLM, with zero test-only production code (Phase 3).
- **`snapshot.json` existence is the fresh-vs-resumed signal**: `RunCommand` binds and persists a
  new snapshot only when absent, otherwise loads the persisted one and never re-reads the workflow
  file — `aer run` again resumes the same task (§21) while staying bound per §11.2. `--task-dir`
  defaults to `.aer/<workflow-file-stem>` under the current directory (Phase 3).
- **Malformed CLI arguments are `CliArgumentException : AerFlowException`**, parsed before any
  file is touched; `Program.cs`'s `Main` is the one place any `AerFlowException` becomes a stderr
  message + non-zero exit (Phase 3).
- **Adapters shell-wrap every invocation and never rely on cwd**: `sh -c`/`cmd /c` around the real
  vendor binary, both for explicit stdin redirection (spike #21's stall finding) and so
  per-execution paths reach the prompt as live `$AER_INPUT_<n>`/`$AER_OUTPUT_DIR` expansions.
  Config-authored text is escaped; the adapter's own generated env-var references deliberately
  aren't (Phase 2).
- **`WorkerInvocation` cannot carry a resolved, execution-specific file path.** `IWorkerAdapter.Resolve`
  runs once per worker-binding resolution, not once per execution — one `CoreDispatchTarget` per
  role is reused across every dispatch; per-execution dynamism stays in the env vars the unchanged
  `ArtifactManager` resolves fresh per dispatch (M7 Phase 6) (Phase 1).
- **Worker-binding config is a flat JSON object keyed by worker role name**, living entirely in
  `Aer.Adapters` (Adapter Isolation), deserialized with the repo's one case-sensitive
  no-naming-policy convention — and **every config entry resolves to `WorkerBinding.Process`**;
  `NonProcess` is constructed directly by whatever caller needs one (Phase 1).

**M10: Cancellation & Edge Cases** — on-demand cancellation through the single mutation surface (intent recorded first), and crash-recovery made whole by reading back the Core half of the log.

- ✅ Phase 1 — Cancellation mutation surface: record, validate, non-process targets (#69)
- ✅ Phase 2 — Live cancellation delivery: in-flight Core executions (#70)
- ✅ Phase 3 — Crash-recovery reconciliation: reading back the Core log (#71)
- ✅ Phase 4 — Cancellation + crash-recovery end-to-end integration tests (#72)

Decisions of record from M10:

- **The pump's own host process is the only delivery point for a live execution, by construction**:
  §15's guard is held for a mutation call's entire duration, so a second call — even from the same
  process — cannot acquire it while a pump is in flight. `InFlightExecutionRegistry` is an
  in-process handle the caller retains *before* calling the mutation surface, so cancellation of
  one specific live execution — or a host stop of everything in flight — reaches the pump with no
  second mutation-surface call and no daemon (Phase 2).
- **Every process dispatch is registered under its own `CancellationTokenSource`, never the ambient
  host token directly**: a host stop mints `CancellationRequested` for every in-flight execution
  (fsync'd) *before* any is signalled; a targeted cancel does the identical record-then-signal for
  exactly one (Phase 2).
- **Once a host stop is detected, the pump's own I/O switches to an uncancellable token** — the
  ambient token firing stops new dispatches, never the pump's ability to write its way to a
  consistent fixed point (Phase 2).
- **`IEventLogReader.ReadAllCoreEventsAsync` is additive** — every existing `ReadAllAsync` caller
  already treats it as Flow-events-only (Phase 3).
- **A dispatch the same call already registered is excluded from crash-recovery consideration,
  checked before any of the four crash states** — otherwise a genuinely in-flight stub dispatch
  looks like "never started" and gets wrongly resubmitted (Phase 3).
- **The orphan's best-effort cancellation re-issue is a documented no-op**: a crashed pump's
  `AerCancelHandle` cannot survive its process (no cross-process re-attach or kill-by-`Pid` in the
  binding); §7's "best-effort" phrasing accommodates this (Phase 3).

**M9: External Decisions** — pause points, the four external decisions, the automatic invalidation cascade, human workers.

- ✅ Phase 1 — Pause Engine (#57)
- ✅ Phase 2 — External Decision Handler: record, validate, Resume/Reject (#58)
- ✅ Phase 3 — RetryWithRevision + Supersede + the invalidation cascade (#59)
- ✅ Phase 4 — Human worker support (#60)
- ✅ Phase 5 — Pause/decision/supersede/human end-to-end integration tests (#61)

Decisions of record from M9:

- **Pause follows only settled outcomes**: automatic §10 retry runs first; `WorkflowPaused` is a **derived obligation** appended after `ExecutionSucceeded`, terminal failure, or `ExecutionCancelled` — evaluated from projected state at the top of each round, never welded into the dispatch continuation, so the outcome→pause crash window re-derives on the next call (Phase 1).
- **One resolving decision per pause**: supplementary executions occupy §17's "zero or more decisions" window without being decisions; each recorded decision resolves its pause, a second decision naming the same execution is invalid, and a step that pauses again does so under a new `ExecutionId` (Phase 2).
- **`Reject` is externally triggered exhaustion**: the step projects terminally failed with retry foreclosed regardless of remaining budget — and it applies to a *successful* paused outcome too (the approval-gate "no") (Phase 2).
- **Decision consequences are projected facts, not handler state**: an unfulfilled `RetryWithRevision`/`Supersede` (decision recorded, no newer accept for the affected step) is re-derived by any later pump, so the record→dispatch crash window loses nothing (Phase 3).
- **The supplementary artifact reaches workers via `AER_SUPPLEMENTARY_INPUT`**, a dedicated variable that can never collide with declared `AER_INPUT_<n>` names (Phase 3).
- **The resume race is recorded, not fixed**: a dependent of the pausing step that dispatches at resume against the pre-supersede result goes stale and reruns through the same cascade once the superseding rerun lands; preventing it would need the holding mechanism §17.5 declines to introduce (Phase 3).
- **Non-process executions are pending until satisfied, never `Failed`** — there is no exit signal to classify against; completion is detected at the top of every mutation call by full contract satisfaction (existence + §4.1 conditions). `ExecutionRequest.StepId` is optional: step-less supplementary executions are tracked execution-level and ignored for step state (Phase 4).

**M8: Reactive Scheduler** — fan-out/fan-in DAG with retries and concurrent dispatch.

- ✅ Phase 1 — Attempt-history projection (#45)
- ✅ Phase 2 — Retry Engine + retry-aware readiness (#46)
- ✅ Phase 3 — Reactive concurrent dispatch (#47)
- ✅ Phase 4 — Fan-out/fan-in + retry end-to-end integration tests (#48)

Decisions of record from M8:

- **Attempt counting is per round**: `ConsecutiveFailureCount` counts trailing consecutive failures *since the last success*, so a step re-run after M9's `Supersede` starts with a fresh retry budget — matching §11.3's "only the latest attempt per step matters" framing (Phase 1).
- **Retry decisions live in `Aer.Flow.Scheduling.RetryEngine`**, a pure predicate (`MayRetry`) consulted by the Dependency Resolver; "terminally failed" is a derived fact (`Failed` ∧ ¬`MayRetry`), never a stored event, per §5.2. `Cancelled` is never retried (§9, §10); `MaxAttempts` is total attempts per round and validated `>= 1` (Phase 2).
- **Determinism under concurrency (§13)**: `ExecutionRequestAccepted` events are appended and fsync'd sequentially in snapshot declaration order *before* their dispatches are awaited; completion order only influences *when* the next projection happens, never *what* it concludes (Phase 3).
- **No concurrency cap in M8**, recorded deliberately: `ExecutionRequestRejected` stays unexercised until an admission cap is a real, scoped design decision (rejection is durable; what re-admits a rejected step?) (Phase 3).
- **Manifest cache deferred** per §21's expectation: a 400-event log re-reads in ~3.8ms, dwarfed by real dispatch latency; revisit only if a per-task log grows large enough for this to show up in practice (Phase 4).

**M7: Foundation** — linear A → B → C end-to-end, happy path only.

- ✅ Phase 1 — Domain model (#7)
- ✅ Phase 2 — Log Manager (#8)
- ✅ Phase 3 — Template Parser + Snapshot Binder (#9)
- ✅ Phase 4 — State Projector (#10)
- ✅ Phase 5 — Dependency Resolver (#11)
- ✅ Phase 6 — Artifact Manager + Core Dispatcher (#12)
- ✅ Phase 7 — Outcome Classifier + Contract Validator + Mutation Interface (#13)
- ✅ Phase 8 — Concurrency Guard + end-to-end integration test (#14)

Decisions of record from M7:

- **Workflow definition files are plain JSON** (`.json`, one document — not `.jsonl`), deserialized through the same `System.Text.Json` converters as every other domain record and `flow.jsonl` itself (Phase 3).
- **Paths reach workers via environment variables**: `AER_INPUT_<n>` and `AER_OUTPUT_DIR`. `ArtifactManager.ResolveInputPaths` matches a step's declared `Inputs` names against its direct dependencies' declared `Outputs` names (Phase 6).
- **A single `flow.jsonl` records both Flow- and Core-originated events** (allowed because §5 leaves the storage backend implementation-defined); §5.1's dual-log ownership is enforced in the type system (`LogEntry.FlowLogEntry` vs. `LogEntry.CoreLogEntry`), not by physical file separation (Phase 6).
- **aer-core is consumed as a pinned git submodule** (`external/aer-core`), built from source via `pixi run build-core`. Revisit with a real package feed only once a second consumer exists (Phase 6; AER Overview §6).
- **Worker resolution shape**: the Mutation Interface takes `Worker`-name → `WorkerBinding` (the `WorkerContract`, the concrete `CoreDispatchTarget`, and a per-worker `Timeout`). The timeout deliberately lives on the binding, not the step, keeping the frozen `WorkflowDefinitionSnapshot` shape (§11.2) unchanged (Phase 7).
- **Where `FailureClassification` (§8.1) lives**: the first of the contract's declared `OptionalMetadata` file names (checked in order) that exists in the output directory, parses as JSON, and has a top-level `FailureClassification` field wins; absent or unrecognized is `null`, which every consumer treats as `Retryable` (Phase 7).
- **The concurrency guard is held by the Mutation Interface** for the full duration of the mutation call — the single mutation surface (§14) is the one place §15's guarantee needs enforcing. `flow.lock` is left on disk on release; its existence is deliberately meaningless — only the live `FileShare.None` hold signals "locked" (Phase 8).

---

## Open Questions (spec-level)

These are gaps in `aer-flow-behavioral-spec-v1.0.md` discovered during planning. Each should be resolved via a spec PR before the phase that first encounters it.

- ~~**`WorkflowTransition` event**~~ — resolved (#15): the event was removed from the spec; workflow-level status is a pure projection of step-level and pause/resume events (§5.2, §12).
- **Event Store performance** — full re-read vs. manifest-checkpoint-plus-tail (§21). Deferred until §20's no-daemon question is revisited.
- **Mutation Interface shape** — deliberately unspecified (§14); CLI is the reference implementation. Shape emerges from M7 implementation; the CLI surface itself lands in M11 (`aer run`) and M12 (`aer decide`/`aer cancel`).
- ~~**Orphaned mid-run executions**~~ — resolved (#77): §7 now defines the third crash state (`ExecutionStarted`, no `ExecutionExited`) — finalize as abandoned, a Flow-originated `ExecutionFailed`/`Retryable`, after a best-effort re-issued cancellation toward Core. Unblocks M10 Phase 3 (#71).
- ~~**Task-directory discovery (UI spec §3)**~~ — resolved (#126, UI spec v0.8 §3.1): a task directory is self-describing — identified by its durable contents (bound snapshot + event store), never by membership in any registry or list; any UI-side list of known task directories is Local UI Configuration (§4), a rebuildable convenience that is never authoritative; and no component of the trusted execution stack may be required to announce, register, or enumerate tasks. Unblocks M14 Phase 2 (#119).
- **UI spec maturity (v0.8 vs. the flow spec's v1.0)** — the UI spec is the only sibling below 1.0. M14–M16 planning and implementation should expect to surface more gaps like the one above, each resolved via a spec PR before the phase that hits it — this list is the ledger. Promotion to v1.0 is a natural M16-completion question, not a prerequisite for starting M14.

## Notes for future work

- **A third worker adapter (`Aer.Adapters`)** — Claude shipped in M11 Phase 2 (#85), Gemini/`agy` in M12 Phase 1 (#95). Before adding another vendor, read closed spike [#21](https://github.com/aer-works/aer-flow/issues/21)'s recorded findings — stdin stalls, permission-flag vocabularies, and path-interpolation behavior differ per CLI and are exactly what the adapter seam exists to absorb.
- **Whether MVVM spreads beyond the decision surface** — M15 Phase 2 (#138) deliberately scoped `CommunityToolkit.Mvvm` to the paused-step Approve/Reject buttons, the first *interactive, stateful* control surface (enabled state tied jointly to projected state and an in-flight mutation). The DAG/history/lineage/diff rendering stayed code-behind on purpose: it's one-directional (projection → controls, nothing to bind against), so a ViewModel there would be ceremony with no payoff. Phase 3 (Retry-with-revision, Send-back) and Phase 4 (Cancel) add more of the same interactive shape, so expect the ViewModel layer to grow phase over phase rather than needing a deliberate decision to introduce it again. Revisit whether the read-only surfaces are worth converting too only if M16 (Authoring) needs two-way binding there — not preemptively.
