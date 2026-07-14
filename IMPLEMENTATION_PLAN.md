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

## M14: UI Projection — Phase Plan

**Goal:** the first UI-track milestone (capability #21; UI spec v0.8 §1, §3, §10–§12): a read-only projection surface over real task directories — browse a task's steps, executions, pauses, and decisions; see the DAG; trace artifacts along their dependency edges; and see when a bound snapshot has diverged from its source template — all reconstructed exclusively from durable state, exactly as UI spec §3/§11/§12 demand. No mutations (that's M15) and no authoring (M16).

Three facts shape the plan. First, **the read model already exists.** UI spec §3's input set — templates, bound snapshots, Flow events, Core events, artifact directories — is precisely what `SnapshotBinder.LoadFromFileAsync`, `IEventLogReader`, and `StateProjector.Project` already consume, and §11's determinism guarantee is Flow §13's re-stated for rendering. A UI that consumes these as a library inherits both by construction; a UI that reimplements projection in another language has to keep two implementations of the same semantics in lockstep forever. This is the dominant constraint on the stack choice, and it pulls hard toward .NET — but the decision is Phase 1's to record, not this plan's to pre-commit. Second, **the spec deliberately refuses to pick a form factor** (§13: desktop, web, TUI, IDE integration — all behaviorally equivalent), so someone has to, against Overview §6's criterion: build for the concrete single-developer need, no speculative deployment story. That decision — and with it, whether the UI lives in `AerFlow.slnx` or a new repo (Overview §7: don't split before a genuine reason; only `aer-core`'s cross-language boundary has ever earned one) — also resolves in Phase 1. Third, **the UI spec is below 1.0** — planning already surfaced one real gap before any code was written (task-directory discovery, resolved by v0.8's §3.1; see Open Questions below), and implementation should expect to surface more, each resolved by a spec PR before the phase that needs it, per this document's existing convention.

Unlike M11/M12, nothing in M14 can ever need live vendor auth: projection is a pure function of durable state (§11), so the milestone gate is golden-projection tests over recorded task-directory fixtures, wired into default CI — M13 Phase 4's placement, for the same reason.

### Phase dependency table

| Phase | Requires output from |
|---|---|
| 1 — Stack decision + walking skeleton | — |
| 2 — Task & execution projection + change observation | 1 |
| 3 — DAG view (snapshot topology + status overlay) | 1 |
| 4 — Artifact lineage + snapshot-vs-template diff | 2 + 3 |
| 5 — Golden-projection determinism gate (default CI) | 2 + 3 + 4 |

Phases 2 and 3 are independent of each other once Phase 1 lands — what a step's execution history looks like and how the graph is drawn are separable concerns, the same shape as M13 Phases 2 and 3. Phase 2's task-directory-discovery blocker is resolved (UI spec v0.8 §3.1, #126).

### Phase 1 — Stack decision + walking skeleton (#118)
Resolves the one decision everything else in the UI track builds on, then proves it end to end: a new UI project that opens one real task directory — persisted snapshot via `SnapshotBinder.LoadFromFileAsync`, events via `IEventLogReader`, state via `StateProjector` — and renders that task's per-step statuses. Deliberately minimal rendering; the point is the seam (§3's read model consumed as a library, inheriting §11's determinism by construction), not the pixels.

**Produces:** the UI project in its decided home, consuming the real read model, rendering one real task's step states.
**Excludes:** multiple tasks and execution detail (Phase 2); graph rendering (Phase 3); artifacts and diffing (Phase 4); any styling worth defending.
**Open questions resolved in this phase:**
- **Form factor + stack** (§13 candidates; criterion: §11 determinism via direct `Aer.Flow` read-model reuse, plus Overview §6's single-developer scope).
- **Repo + solution placement** — Overview §7's default is this repo/solution; a stack that can't link `Aer.Flow` directly is the only thing that would reopen it, and that would need an Overview §7 spec PR, not an implicit decision.
- **Project naming** (`Aer.Ui` vs `Aer.Flow.Ui` vs …).

### Phase 2 — Task & execution projection + change observation (#119)
The full read-model surface for a single task: per-step attempt history, retry state, pause state with its declared `SupersedeTargets`, recorded decisions, supplementary executions, human/non-process executions — everything `FlowState` and the bound snapshot jointly carry (M12 Phase 3's `CommandResult` already established that pairing as the right reporting unit). Plus the first live-ness: re-projecting when the event store grows while a run is in flight. §11 is not threatened by this — observation decides *when* to re-project, never *what* the projection concludes (the same when-vs-what line M8 Phase 3 drew for dispatch determinism).

**Produces:** a complete, live-updating read view of any single task directory.
**Excludes:** graph rendering (Phase 3); artifact browsing and template diffing (Phase 4); all mutations (M15).
**Open questions resolved in this phase:**
- **How task directories are opened/discovered** — implements UI spec §3.1 (#126): discovery is entirely client-side; a task directory is self-describing, and any known-directories list is rebuildable Local UI Configuration, never authoritative. The concrete mechanism (user-opened, remembered recents, a scanned root) is this phase's choice within that contract.
- **Change observation mechanism** (polling vs. `FileSystemWatcher` vs. re-project-on-demand) — an implementation choice invisible to the behavioral model, made here.

### Phase 3 — DAG view (snapshot topology + status overlay) (#120)
Renders the graph — steps, dependencies, `PausePoint`s and their `SupersedeTargets` — overlaid with each step's current projected status: §10's DAG view, plus §8's read-only items (visualize execution order, preview workflow topology). Per §5, a bound task renders its immutable `WorkflowDefinitionSnapshot`, never the live template it originated from; a not-yet-instantiated workflow renders the template.

**Produces:** the graph view over both bound tasks and raw templates.
**Excludes:** all editing (M16); §8's advisory scheduling simulation (nothing demands it yet — Overview §6); the snapshot-vs-template *diff* rendering (Phase 4).

### Phase 4 — Artifact lineage + snapshot-vs-template diff (#121)
The two remaining read-only projection surfaces. **Artifact lineage** (§10): every execution's `artifacts/execution_{N}/` contents, navigable along the dependency edges that fed them — the input resolution is already durable (`AER_INPUT_<n>` assignment per Flow spec §16), so the view walks recorded facts, never re-derives them. **Snapshot-vs-template diff** (§5): when a task's originating template still exists and has diverged from the bound snapshot, the divergence must be *visible* — rendered as a diff, never silently substituting one for the other.

**Produces:** artifact browsing with lineage, and the §5 divergence surface.
**Excludes:** artifact editing or submission (§5's revised-artifact path is a mutation through Flow's mutation interface — M15); rendering artifact *content* beyond a file listing + plain-text preview.

### Phase 5 — Golden-projection determinism gate, wired into default CI (#122)
The M14 completion gate: recorded task-directory fixtures (a completed run, a paused run mid-decision, a failed-and-retried run — produced by shell-stub workflows, the `RunCommandEndToEndTests` convention, so no live vendor is involved) projected to a canonical serialized form and asserted against golden files on all three CI OSes. This is §11 made executable — identical durable state, identical projected state — and it is what makes every future UI refactor safe. Belongs in `ci.yml` directly, for M13 Phase 4's reason: nothing here needs live vendor auth, so the gated-runbook pattern would be the wrong default.

**Produces:** M14 complete — a read-only UI over real task directories, with §11 determinism enforced by unattended CI.
**Excludes:** any mutation surface (M15); any authoring (M16); conversation/live-stream views (assigned to no milestone — see the roadmap note).

---

## Current Milestone

**M14: UI Projection** — phase plan above. Progress:

- ✅ Phase 1 — Stack decision + walking skeleton (#118)
- ✅ Phase 2 — Task & execution projection + change observation (#119)
- ✅ Phase 3 — DAG view (snapshot topology + status overlay) (#120)
- ✅ Phase 4 — Artifact lineage + snapshot-vs-template diff (#121)
- ✅ Phase 5 — Golden-projection determinism gate, wired into default CI (#122)

Per this document's session prompt: help implement the current phase only.

Decisions of record from M14:

- **Stack: Avalonia (a cross-platform, native .NET desktop UI framework), in this repo/solution,
  referencing `Aer.Flow` directly** — resolves both of Phase 1's named open questions at once. UI
  spec §13 treats desktop/web/TUI/IDE-integration as behaviorally equivalent, so the deciding
  criteria are AER Overview §6 (a single-developer tool: "run the exe" is a desktop app's whole
  deployment story, no server/browser/hosting layer) and §11's determinism guarantee, which direct
  in-process `Aer.Flow` library reuse inherits by construction — the same seam `Aer.Cli` already
  proved for the write side. Avalonia specifically, over WPF/MAUI, because it is genuinely
  cross-platform (matching the existing win/linux/mac CI matrix) and renders real vector graphics —
  what Phase 3's DAG view and Phase 4's artifact/diff views actually want, not a text
  approximation. An initial plain-console-app version of this decision was reopened and replaced
  with this one before the phase shipped, once it was clear "any styling worth defending" (this
  phase's own exclusion) had been read as license to defer the real stack question rather than
  answer it (Phase 1).
- **Repo/solution placement was never actually in question**: nothing about the stack choice above
  needed a cross-language or cross-solution boundary, so Overview §7's default (this repo, this
  solution) applies unchanged — `Aer.Ui.csproj`/`Aer.Ui.Tests.csproj` are new leaves in
  `AerFlow.slnx`, not a new repo (Phase 1).
- **Project name is `Aer.Ui`, not `Aer.Flow.Ui`**: the UI is architecturally outside the trusted
  execution stack (UI spec §2) and must never be mistaken for part of Flow's own namespace, even
  though it references `Aer.Flow` directly — `Aer.Cli` set the same flat-naming precedent (M7) for
  a project that also only exists to drive `Aer.Flow` (Phase 1).
- **The walking skeleton opens exactly one task directory it is told about, with no discovery
  mechanism yet** — `Aer.Ui`'s own `TaskProjectionLoader.LoadAsync` takes a task-directory path
  argument directly (the app's single launch argument), consistent with UI spec §3.1 (#126): a task
  directory is self-describing and confirmed by its contents (a persisted `snapshot.json`), not by
  membership in any list. Any recents/roots list, and any actual folder-picker UI, is Phase 2's
  concern, since minting one has nothing to do with proving the seam (Phase 1).
- **`MainWindow` renders directly against named controls in code-behind, with no ViewModel or
  data-binding layer** — `LoadAsync` sets `TextBlock`/`StackPanel` contents itself, the simplest
  thing that could render `TaskProjection`, matching this phase's explicit exclusion of "any
  styling worth defending." An MVVM layer is exactly the kind of abstraction CLAUDE.md's guidance
  says not to build ahead of a second concrete need for it (M15's interactive control surface, or
  Phase 3's graph view, may well introduce one) (Phase 1).
- **`MainWindow.LoadAsync` is public and directly awaitable, not fired from the constructor or a
  `Loaded` event** — the only way a test can drive it deterministically (await it, then assert on
  the now-populated controls) without pumping the dispatcher on a timer or racing a background
  task (Phase 1).
- **A failed load (an invalid task directory, a malformed snapshot or event log) renders as an
  in-window message, not a crash or an `Aer.Cli`-style stderr-plus-exit-code failure** — a GUI app
  has no console/exit-code convention to fail into the way `Aer.Cli`'s `Program.cs` boundary does;
  `MainWindow.LoadAsync` catches `AerFlowException` itself and writes the message into the status
  text block (Phase 1).
- **UI tests drive the real `Aer.Ui.App`/`MainWindow` through `Avalonia.Headless`/
  `Avalonia.Headless.XUnit`**, not a plain-text stand-in renderer — proving the phase's "renders
  that task's per-step statuses" claim against actual rendered controls (`FindControl<TextBlock>`),
  offscreen and without a display server, which is what makes it safe to run unattended on all
  three CI OSes. This forced `Aer.Ui.Tests` onto xunit v3 (`Avalonia.Headless.XUnit`'s `[AvaloniaFact]`
  is a v3-only attribute, incompatible with the xunit v2 packages every other test project in this
  repo uses) — an isolated exception, confined to this one project, for a genuinely different
  concern (headless UI testing) no other project has (Phase 1).

- **`ExecutionHistory`/`ExecutionHistoryProjector` is a new `Aer.Ui`-only read-model type, not an
  addition to `Aer.Flow.Domain.FlowState`.** `StateProjector.Project` deliberately collapses each
  step to its *latest* attempt (§12); the full per-step attempt history, and the full (not just
  still-unresolved) decision list, are presentation-layer facts derivable from the same event list
  a second time, never dispatch-affecting ones. `ExecutionHistoryProjector.Project` walks
  `IReadOnlyList<FlowEvent>` independently — re-deriving the same "what happened to this
  `ExecutionId`" facts `StateProjector` computes internally, but keyed per-execution instead of
  collapsed per-step — rather than calling into or duplicating `StateProjector`'s retry/staleness/
  readiness logic. `TaskProjectionLoader.LoadAsync` calls both projectors over the same `events`
  list; `TaskProjection` gained a third field, `History`, alongside `Snapshot`/`State` (Phase 2).
- **A non-process/human execution is identified the same way M11 Phase 1 already recorded it**:
  `ExecutionRequest.Timeout is null`. No new durable fact was needed — bindings themselves are
  never persisted to the task directory, so this was the only signal already on disk that
  distinguishes a `Mutation.WorkerBinding.NonProcess` dispatch from an ordinary one once the read
  side has nothing but the event log and snapshot to go on (Phase 2).
- **Task-directory discovery (UI spec §3.1's implementation choice) is "ask the user for a path,
  or pick a remembered one" — never a scanned root.** A new `TaskDirectoryPathBox`/`OpenButton`
  pair plus a `RecentsPanel` (Local UI Configuration, §3.1/§4) cover the concrete mechanism; a
  scanned-root discovery mode was considered and dropped as unnecessary complexity for a
  single-developer tool with no fixed "tasks live under one root" convention (Phase 2).
- **`LocalUiConfigurationStore` is a small, explicit JSON file store** (`recent-task-directories.json`
  under a per-user config directory via `CreateDefault()`), never backed by a database or a
  platform-specific settings API — matching this repo's "no speculative infrastructure" bias
  (Overview §6). It is deliberately non-authoritative per §3.1: a missing or corrupt file loads as
  an empty list rather than an error, and a remembered path that no longer exists on disk is
  silently dropped on load rather than surfaced as stale-list breakage. Capped at 10 entries,
  deduplicated by full path, most-recently-opened first — a bounded convenience, not a full
  history (Phase 2).
- **`MainWindow` takes a `LocalUiConfigurationStore` as a constructor argument**, defaulting to
  `CreateDefault()` only via a parameterless overload — the same "production wiring is the
  caller's decision, not baked into the type" seam `RunCommand`'s adapter-registry argument
  established (M11 Phase 3). This is what lets `Aer.Ui.Tests` point every window at a temp config
  file instead of ever touching this host's real per-user config directory (Phase 2).
- **Change observation is polling via a 2-second `DispatcherTimer`, not a `FileSystemWatcher`** —
  issue #119's named open question. Simplest thing that behaves identically across the
  win/linux/mac CI matrix without depending on a given filesystem's (or container's) watch
  semantics; the existing `flow.jsonl` re-read cost is already known cheap (M8 Phase 4's ~3.8ms
  finding). Polling stops once the projected `WorkflowStatus` reaches `Terminal` — nothing further
  can change per spec §12, so there is nothing left to observe (Phase 2).
- **`MainWindow.RefreshAsync` is public and directly awaitable, the same reason `LoadAsync` was
  (Phase 1, issue #118)**: it is what the `DispatcherTimer`'s tick calls in production, but a test
  drives exactly one re-projection deterministically by calling it directly, rather than pumping
  the headless dispatcher and racing a real elapsed-time tick. `OpenAsync` is the richer entry
  point (`LoadAsync` plus recents-recording plus starting/stopping the live-refresh timer) that the
  Open button, a `RecentsPanel` click, and `App`'s CLI-argument launch path all now go through —
  `LoadAsync` itself is untouched from Phase 1, so its existing rendering contract (and
  `MainWindowTests`' assertions against it) stay stable (Phase 2).
- **`StatusText`/`StepsPanel`'s rendering format is byte-for-byte unchanged from Phase 1.** Every
  new read-model surface (attempt history, retry/pause state with declared `SupersedeTargets`,
  decisions, supplementary/human executions) renders into new, separate panels
  (`HistoryPanel`/`DecisionsPanel`/`SupplementaryPanel`) rather than being folded into the existing
  per-step line — preserves Phase 1's tested rendering contract exactly rather than reopening it
  (Phase 2).
- **A test-only `internal bool IsLiveRefreshTimerEnabled` property, gated by
  `[InternalsVisibleTo("Aer.Ui.Tests")]`, is the only way the live-refresh timer's start/stop state
  is observed at all** — production code never reads it. The alternative (asserting on real
  elapsed-time timer ticks) would reintroduce exactly the dispatcher-pumping flakiness Phase 1's
  `LoadAsync`-is-public-and-awaitable decision was designed to avoid (Phase 2).
- **Giving `Aer.Ui` its own release-please package (independent version from the rest of the repo)
  was tried as a Phase 2 follow-up and reverted.** The mechanics worked for the version number
  itself, but a same-repo `exclude-paths` split can't cleanly separate `Aer.Ui`'s changelog from the
  root package's: `exclude-paths` excludes a commit only if *every* file it touches falls under the
  excluded paths, and every M14 phase commit also updates this file (`IMPLEMENTATION_PLAN.md`'s own
  decisions-of-record convention), a root-level path that was never a candidate for exclusion — so
  every UI commit still legitimately touched the root package too, defeating the split. A live
  release-please run also surfaced unrelated-commit pollution in the new package's changelog,
  matching known upstream `exclude-paths` reliability reports in monorepo/manifest mode
  ([googleapis/release-please#2301](https://github.com/googleapis/release-please/issues/2301),
  [#2230](https://github.com/googleapis/release-please/issues/2230)). The real fix — a genuinely
  separate repo, the same boundary `aer-core` has — was judged not worth reopening M14 Phase 1's
  repo-placement decision for right now; `Aer.Ui` stays on the shared root version like every other
  project until that tradeoff is worth it on its own terms, not as a side effect of chasing a
  release-please config workaround.
- **`DagLayoutEngine.Layout` takes `IReadOnlyList<WorkflowStepDefinition>` directly — the shape both
  `WorkflowDefinition` (a raw template) and `WorkflowDefinitionSnapshot` (a bound task) already
  expose — rather than two overloads or a new shared wrapper type.** One graph view over both bound
  tasks and raw templates (issue #120) falls out for free: the layout and rendering code never
  branches on which kind of source it was handed, only the status overlay does (`null` for a
  template, projected `FlowState` for a task) (Phase 3).
- **Layering is longest-path-from-root ranking, columns assigned in declaration order within a
  rank** — the simplest layout that reads correctly for the small, mostly-linear-with-occasional-fan
  DAGs this project's own workflows are (three-to-a-handful of steps), against Overview §6's bias
  against building a general graph-layout library (barycenter/Sugiyama crossing-minimization, etc.)
  for a need that doesn't exist yet. Output order (both `DagLayout.Nodes` and `.Edges`) is derived
  entirely by walking the input `steps` list and each step's own `DependsOn`/`SupersedeTargets` list
  — never by `Dictionary`/`HashSet` enumeration order, which .NET does not guarantee stable — so the
  layout is as deterministic as spec §11 requires, ready for Phase 5's golden-projection gate to
  assert against directly (Phase 3).
- **An ordinary `DependsOn` edge and a declared `PausePoint.SupersedeTargets` edge render distinctly
  (solid vs. dashed)**, per the issue's explicit "PausePoints and their SupersedeTargets" scope — the
  two mean different things and must not be visually conflated: one is an execution-order constraint
  that has already held, the other a *possible future* decision that may never be taken (Phase 3).
- **`TemplateProjectionLoader` is a new, separate loader — not a branch inside
  `TaskProjectionLoader`.** A raw template file and a task directory are different durable-state
  shapes (a single `WorkflowDefinition` JSON document vs. a directory containing a bound
  `WorkflowDefinitionSnapshot` plus an Event Store); giving each its own loader keeps
  `TaskProjectionLoader`'s existing contract (and its Phase 1/2 tests) untouched, the same
  one-file-not-found-check-before-delegating shape as `TaskProjectionLoader` itself (Phase 3).
- **`MainWindow.OpenAsync` dispatches on `File.Exists` vs. `Directory.Exists` of the given path,
  rather than adding a second path box or a mode toggle** — Overview §6's bias against speculative
  UI surface: a task directory and a template file are never ambiguous on disk, so the existing
  single path-box-plus-Open-button pair already has enough information to route correctly. Opening a
  template does not record to `LocalUiConfigurationStore` (that store is task-directory recents
  specifically, per its Phase 2 decision of record) and does not start the live-refresh timer — a
  template has no execution state to observe changing (Phase 3).
- **`ArtifactLineageProjector` walks each recorded `ExecutionRequest`'s `UpstreamExecutionIds`
  directly — never `ArtifactManager.ResolveInputPaths`/the current `FlowState`'s `LatestExecutionId`
  — to resolve which upstream execution fed each input.** The durable fact of *which specific
  execution* produced a given input is only ever recorded once, at that request's own dispatch time;
  re-deriving it against today's state would silently substitute a step's *current* latest execution
  for the one an earlier, possibly-superseded attempt actually consumed. A step's declared
  `Inputs` names (read from the snapshot, matched against each `DependsOn` step's declared
  `Outputs`) are what the projector walks to find each producer `StepId` — never
  `ExecutionRequest.Inputs`, which holds `ArtifactManager.ResolveInputPaths`' already-resolved file
  paths, not bare input names, and does not key against the producer lookup at all (caught by
  `TaskProjectionLoaderTests`' real `MutationInterface`-pumped fixture; a hand-built event-list unit
  test alone did not catch it, since it could assign whatever meaning it liked to `Inputs`) (Phase 4).
- **Output-file listings are read straight off disk (`Directory.GetFiles`), never from a step's
  declared `Outputs`** — a contract names what a worker was supposed to produce, not what is really
  in `artifacts/execution_{N}/`; sorted ordinally rather than left in platform-dependent enumeration
  order, since UI spec §11 requires identical projected state on all three CI OSes and Phase 5's
  golden gate will assert against exactly this ordering (Phase 4).
- **The snapshot-vs-template diff compares `WorkflowStepDefinition` fields with `SequenceEqual`,
  never the records' own generated `Equals`.** `Inputs`/`Outputs`/`DependsOn` are
  `IReadOnlyList<string>`/`IReadOnlyList<StepId>`; C#'s positional-record equality compares a list
  member with `EqualityComparer<T>.Default`, which is reference equality for a list — since the
  snapshot and template are always separate object graphs (deserialized from separate sources), that
  would report every field of every step as changed, always. Caught before it shipped, not by a
  test: the naive "identical content, separate instances" case is exactly the one a same-instance
  test fixture would hide (Phase 4).
- **A `WorkflowTemplateId` mismatch is reported as `TemplateIdMismatch`, never folded into
  `HasDiverged`.** Comparing two unrelated templates and calling the result a "diff" would be the
  exact silent-substitution error UI spec §5 warns against — divergence means the *same* template
  changed over time, not that the wrong file was compared. `WorkflowTemplateVersion` is carried on
  the diff purely as an informational field, never as part of the `HasDiverged` predicate: a
  hand-edited template can diverge without a version bump, and a version bump alone doesn't prove
  the steps actually differ (Phase 4).
- **There is no durable link from a bound task back to the template file it originated from** — the
  snapshot carries only `WorkflowTemplateId`/`WorkflowTemplateVersion`, never a path (confirmed
  against `WorkflowDefinitionSnapshot`, `SnapshotBinder`, and every event in `FlowEvent`). The diff
  surface therefore takes the template file path directly from the user (a new
  `TemplateComparePathBox`/`CompareButton` pair, gated on a task already being open via
  `MainWindow.CompareToTemplateAsync`), the same "ask, don't infer" answer Phase 2 gave for
  task-directory discovery, rather than inventing a new stored pointer this phase has no spec basis
  for (Phase 4).
- **The artifact preview is scoped to plain text, capped at 8,000 characters, with no content-type
  detection.** An artifact is not guaranteed to be small or textual; `File.ReadAllTextAsync`'s
  default UTF-8 decoding never throws on non-UTF-8 bytes (replacement characters instead), so a
  binary file still renders as garbled text rather than crashing — the phase's own "cheap preview,
  not a text-viewer" ceiling, not a special-cased binary detector (Phase 4).
- **Each output-file button's click handler captures the resolved file path at render time
  (`RenderArtifactLineage`'s own `taskDirectoryPath` parameter), never `_currentTaskDirectoryPath`.**
  `LoadAsync` is a supported, directly-callable entry point in its own right (Phase 1) that a caller
  may invoke without ever going through `OpenAsync` (the only place that field is set) — a button
  wired to the field would silently do nothing if `LoadAsync` was called directly. Caught by a test
  that calls `LoadAsync` the same way `MainWindowDagTests` already does, then drives the rendered
  button's real `Click` event rather than calling `ShowArtifactPreviewAsync` directly (Phase 4).
- **"Wired into default CI" means an ordinary `[Fact]` in `Aer.Ui.Tests`, with zero `ci.yml`
  changes** — unlike M13 Phase 4 (a separate bash step in the `pack` job, since that check needed a
  packaged tool to exist first), this gate only needs the projection code already in `Aer.Flow`/
  `Aer.Ui`, so `pixi run test` — already run on all three OS matrix entries by the existing `test`
  job — covers it for free (Phase 5).
- **`GoldenProjectionCanonicalizer` tokenizes every runtime-minted ID (`ExecutionId`, `DecisionId`)
  to a stable `"execution-N"`/`"decision-N"` string by first appearance in `Lineage.Executions`/
  `History.Decisions`, and explicitly sorts only the fields backed by `Dictionary`/`HashSet`
  (`UpstreamExecutionIds`, `AttemptsByStepId`, `CancellationRequestedExecutionIds`) — every
  List-backed field (`Steps`, `Decisions`, `DagLayout.Nodes`/`Edges`, etc.) is left in its natural,
  walk-order-derived order.** That natural order is itself the determinism property spec §11
  requires and Phase 5 exists to check; re-sorting it would hide real ordering bugs instead of
  catching them (Phase 5).
- **A new `paused-run-workflow.json` fixture was authored from scratch for the paused-mid-decision
  golden scenario, rather than reusing the existing `diamond-workflow-with-pause.json`.** That
  fixture declares two steps (`a`, `b`) with the same `Worker` name but different declared
  `Outputs` — safe as a raw, never-pumped template (its only prior use), but dispatch-unsafe once
  pumped for real: sharing one `WorkerBinding` entry between steps with different output contracts
  would make `OutcomeClassifier` check the wrong step's output file. The new fixture gives every
  step a distinct worker name, matching the already-safe pattern in
  `Aer.Flow.Tests/Fixtures/diamond-dag-workflow.json` (Phase 5).
- **Golden files are bootstrapped and refreshed via an opt-in `AER_UPDATE_GOLDEN_FILES=1`
  environment variable read by the test itself, never hand-authored.** Hand-writing expected JSON
  for tokenized IDs and dictionary-sort order would be error-prone and self-fulfilling; the
  regeneration path writes to the source-tree fixture path (resolved via `[CallerFilePath]`) so a
  deliberate, reviewable diff is the only way a golden file changes (Phase 5).

## Completed Milestones

Completed milestones keep only their phase checklist and any decisions of record later work
still leans on. The full phase plans — goals, boundaries, and the open questions each phase
resolved — live in this file's git history and in the linked issues.

**M13: Distribution** — turned `aer` from a checkout-only build into an installable
`dotnet tool`: single-platform packing, version wiring from `release-please`, multi-RID
native-lib bundling, and an unattended CI round-trip check proving install → run → uninstall
works with no live vendor auth (`pixi run verify-pack`, `scripts/verify-pack-roundtrip.sh`).

- ✅ Phase 1 — Pack `aer` as a `dotnet tool` (single-platform) (#107)
- ✅ Phase 2 — Version wiring (release-please → package `Version`) (#108)
- ✅ Phase 3 — Multi-RID native-lib bundling (Windows/Linux/macOS) (#109)
- ✅ Phase 4 — Installed-tool round-trip check (wired into default CI) (#110)

Decisions of record from M13:

- **The round-trip check's "no live vendor" requirement is met by stubbing the `claude` binary
  itself, not by using a separate, unregistered test-only adapter.** `WorkerAdapterRegistry.Default`
  — the exact registry the installed `aer` binary's `Program.cs` wires up — only ever resolves
  `"claude"`/`"gemini"` bindings; the `"shell"` adapter `RunCommandEndToEndTests` uses is
  `Aer.Cli.Tests`-internal and never reachable from a real installed tool. So
  `scripts/verify-pack-roundtrip.sh` puts a trivial local script named `claude` ahead of any real
  one on `PATH`; `ClaudeWorkerAdapter`'s shell-wrapped invocation finds and runs it like it would
  the real CLI, and the stub satisfies the declared output contract by reading the real
  `AER_OUTPUT_DIR` process environment variable directly (M7 Phase 6) rather than parsing the
  prompt text — proving the packaged `aer_core` dispatch and adapter shell-wrapping work end to
  end from the installed global tool, settling the step `Succeeded` rather than reusing Phases
  1/3's `ExitCode:127`-on-missing-`claude` proof shape (Phase 4).
- **The check is a plain bash script (`scripts/verify-pack-roundtrip.sh`) invoked by a new
  `pixi run verify-pack` task, not a `dotnet test`.** Unlike `smoke-claude`/`smoke-mixed-vendor`
  (real `dotnet test` projects, gated out of `AerFlow.slnx` because they need real subscription
  auth), this check drives the literal end-user install/run/uninstall commands from
  `README.md`'s new "Installing `aer`" section, so the script *is* the documentation, verified
  (Phase 4).
- **Wired into the existing CI `pack` job (`ubuntu-latest`), not a new job or a runbook.** The
  phase's own named question — CI vs. a gated runbook, like M11/M12's pattern — resolves to CI
  specifically because nothing here needs live vendor auth, matching the phase plan's stated
  reasoning. It runs after "Pack aer (multi-RID)" in the same job, so it exercises the real
  multi-RID nupkg the `test` matrix's three OS jobs just built (Phase 4).

- **The native-lib packaging problem the phase plan flagged as "genuinely new" turned out to need
  no extra MSBuild plumbing at all.** `PackAsTool` packs from a *publish* output, not a plain build
  output — and `dotnet publish` already folds in every referenced project's
  `Content`/`CopyToOutputDirectory` items, including `Aer.Core.csproj`'s existing `aer_core` copy
  (M7 Phase 6), landing it at `tools/$(TargetFramework)/any/` next to the managed DLLs for free.
  An explicit `<None Pack="true" PackagePath="tools/...">` item was tried first and produced NuGet
  warning NU5118 ("file already added") — removed once inspecting the nupkg's contents confirmed
  the automatic inclusion already puts the native library exactly where P/Invoke probing looks for
  it. `Aer.Cli.csproj` therefore only needed `PackAsTool`/`ToolCommandName`/`PackageId` (Phase 1).
- **`PackageId`/`ToolCommandName` are both `aer`** — no public feed exists to collide with (AER
  Overview §6), so this is the simplest local-convenience choice, not a namespace decision (Phase 1).
- **The round trip was verified for real, offline, without a live vendor call**: `dotnet pack` →
  `dotnet tool install --global --add-source <dir> aer` → `aer run` against a one-step
  `draft`/`claude`-adapter workflow with `claude` deliberately absent from `PATH` → `dotnet tool
  uninstall --global aer`. The stripped `PATH` makes the shell-wrapped `claude` invocation fail
  fast (`sh: 1: claude: not found`, exit 127) instead of reaching the network, while still driving
  a real OS process through the real, packaged `aer_core` native library end to end — confirmed via
  `flow.jsonl`'s `executionStarted`/`executionExited` pair carrying a real `Pid` and `ExitCode:127`.
  This proves exactly what Phase 1 needs to prove (the native lib resolves and P/Invoke dispatch
  works from the installed global tool) without redoing M11/M12's already-proven live-engine
  behavior or touching the "Live-vendor smoke tests" gate CLAUDE.md reserves for a human (Phase 1).
- **`pixi run pack`** (`dotnet pack src/Aer.Cli/Aer.Cli.csproj -o bin/pack`) is its own task,
  deliberately not folded into `build`/`test`/`lint` — packing isn't part of everyday development,
  only the install round trip this phase's verification exercises and Phase 4's future CI check
  will automate (Phase 1).
- **The version's single source of truth is a root `Directory.Build.props` `<Version>`, not a
  CI-only pack-time `-p:Version=` override.** `release-please-config.json`'s `.` package gained an
  `extra-files` entry (`type: "xml"`, `xpath: "/Project/PropertyGroup/Version"`) that bumps it
  directly on every release PR merge, the same mechanism that already bumps
  `.release-please-manifest.json`/`CHANGELOG.md` — visible to every local `dotnet build`, not just
  a CI pack step, per the phase plan's stated preference for the option with wider visibility
  (Phase 2).
- **`IncludeSourceRevisionInInformationalVersion` is explicitly `false`.** The SDK's default behavior
  appends `+<git-sha>` to `AssemblyInformationalVersion`, which would make the packed tool's version
  output (`0.7.0+<sha>`) never equal `CHANGELOG.md`'s plain `0.7.0` entry — caught by the phase's own
  round-trip test failing on the first attempt, not anticipated up front (Phase 2).
- **`aer --version` is handled directly in `Program.cs`, ahead of the `knownSubcommands` dispatch,
  not as a fifth subcommand.** It takes no options, touches no task directory or bindings file, and
  returns before any of the mutation-surface machinery every other command goes through; `VersionInfo.GetVersion`
  (a one-line `Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()` read, `Aer.Cli`)
  is the only piece worth unit-testing on its own (Phase 2).
- **A single fat package, not three RID-qualified packages** — resolving Phase 3's named open
  question. `ci.yml`'s `test` matrix gained a real `macos-latest` job (cross-compiling `aer_core`
  to `aarch64-apple-darwin` from the existing Linux runner isn't practical without Apple's SDK) and
  now uploads each OS's own `cargo build` output as a keyed artifact; a new `pack` job downloads the
  two platforms it isn't running on into `artifacts/native-libs/<rid>/` and runs `pixi run pack`, so
  the packing machine's own OS is still folded in automatically by the existing build-time
  mechanism (Phase 1's decision) while the other two arrive as gathered files. The same
  `dotnet tool install --global aer` then works unchanged regardless of host OS — no `--arch`
  selection, no per-RID package variants (Phase 3).
- **No runtime OS-detection/P/Invoke-resolution code was needed** — the second half of Phase 3's
  named open question turned out to be moot. `NativeMethods.cs`'s `DllImport`/`LibraryImport`
  attributes reference the bare library name `"aer_core"`, and .NET's default native-library probing
  already appends the host-appropriate prefix/extension (`aer_core.dll` / `libaer_core.so` /
  `libaer_core.dylib`) when resolving it at P/Invoke call time. Since every platform's binary has a
  distinct filename, all three coexist in the same flat `tools/$(TargetFramework)/any/` directory
  with no collision and no custom resolution logic — confirmed by inspecting a locally packed nupkg
  (real Linux `libaer_core.so` alongside placeholder `aer_core.dll`/`libaer_core.dylib` standing in
  for the two platforms this sandbox can't build) and by a live round trip: pack → install →
  `aer run` against a one-step `draft`/`claude`-adapter workflow with `claude` stripped from `PATH`
  → `flow.jsonl`'s `executionStarted`/`executionExited` pair carrying a real `Pid` and
  `ExitCode:127`, the same proof-of-dispatch shape Phase 1 used → uninstall (Phase 3).
- **`Aer.Cli.csproj`'s three new `<None Pack="true">` items are each `Condition="Exists(...)"`
  against `artifacts/native-libs/<rid>/`**, so a plain single-platform `pixi run pack` with no
  gathered-artifacts folder present (Phase 1's original local round trip) packs exactly as before —
  Phase 3 adds nothing that requires CI to run to keep local packing working (Phase 3).
- **`/artifacts/` is gitignored, not a tracked convention directory** — it exists only as a
  CI-job-to-CI-job (or locally, human-to-`dotnet pack`) hand-off point, populated fresh by
  `actions/download-artifact` before every `pack` job run, never committed content (Phase 3).

**M12: Full Control Surface** — the milestone that made the runnable library drivable: a second
vendor (Gemini's `agy`) behind M11's unchanged protocol, and the mutation surface M9/M10 built
exposed as `aer decide`/`aer cancel`, proven by a live mixed-vendor paused run decided from the
terminal (`docs/runbooks/live-mixed-vendor-smoke.md`).

- ✅ Phase 1 — Gemini worker adapter (headless `agy` CLI) (#95)
- ✅ Phase 2 — `aer cancel` + Ctrl+C host-stop wiring (#96)
- ✅ Phase 3 — `aer decide` + supplementary artifact recording (#97)
- ✅ Phase 4 — Live mixed-vendor paused run (gated end-to-end) (#98)

Decisions of record from M12:

- **`aer supply` mints, populates, and settles a supplementary execution in one call, rather than
  reporting a path for a human to drop a file into out-of-band.** `MutationInterface.RecordSupplementaryExecutionAsync`
  deliberately never runs the pump (§17.3: minting alone changes no readiness), so a settling call is
  still required before the execution's `ExecutionId` is a valid `--supplementary` argument — but
  since `aer supply` already holds the worker-binding it just constructed, it calls
  `StartWorkflowAsync` itself immediately after copying `--file`'s content into the assigned output
  directory, rather than requiring a separate `aer run` invocation in between. This is what makes the
  supply → decide round trip two CLI invocations, not three, and sidesteps a consistency problem a
  cross-invocation design would have had: the transient `WorkerContract` a purely-supplementary role
  needs (arbitrary output names unrelated to any DAG step) would otherwise have to be reconstructed
  identically by whichever command runs the settling pump (Phase 3).
- **The non-process `WorkerBinding` a supplementary execution dispatches under is constructed
  directly by `aer supply` from `--worker`/`--output`, never looked up in the bindings file** — per
  M11's decision of record that worker-binding config entries only ever resolve to
  `WorkerBinding.Process`. This phase does not reopen that decision or extend
  `WorkerBindingConfigParser`'s schema; `aer supply` builds the one `WorkerContract` it needs
  (a single declared output, no required inputs) in-process and merges it into the config-resolved
  Process bindings for its own call only (Phase 3).
- **`aer supply` is scoped to a single declared output**, populated from a single `--file` source,
  rather than a general multi-output contract — every existing supplementary-artifact fixture (M9's
  human-worker tests, this phase's own) is a single revision file; a multi-output supplementary
  execution is a hypothetical this phase declines to design for (Phase 3).
- **`aer run`, `aer cancel`, and `aer decide` all now return a `CommandResult` (`FlowState` plus the
  bound `WorkflowDefinitionSnapshot`), not a bare `FlowState`** — the pause-aware reporting this phase
  requires (a paused step's `SupersedeTargets`) is only resolvable against the snapshot's declared
  `PausePoint`s, which `FlowState` alone does not carry. `FlowStateReporter` is the one shared
  formatter every command's output goes through, so `aer run` and `aer decide` report a paused
  workflow identically (Phase 3).
- **The input-directory grant is one vendor-neutral environment variable, not a per-input
  adapter-side derivation.** `ArtifactManager.BuildEnvironment` gained `AER_ARTIFACTS_ROOT` —
  emitted unconditionally, exactly like `AER_OUTPUT_DIR` — because a step's own output directory
  and every upstream input it reads are already sibling `execution_{id}` directories under the same
  artifacts root (§16). `GeminiWorkerAdapter` grants it once via `--add-dir`, covering every input
  and the output directory with a single flag; the alternative (a per-input `dirname`-style grant
  derived in the shell wrapper) would have needed its own, uglier answer on Windows for no benefit.
  `ClaudeWorkerAdapter` has no use for the new variable and simply never references it (Phase 1).
- **The registry key is the vendor name, not the binary name**: `WorkerAdapterRegistry.Default`
  registers the Gemini adapter as `"gemini"`, matching `"claude"`'s convention, even though the
  binary it invokes is `agy` — the key names who you're talking to, not what you type to reach them
  (Phase 1).
- **`agy` is shell-wrapped and has its stdin redirected exactly like `ClaudeWorkerAdapter`**, even
  though spike #21 recorded no stdin stall for it: the wrapper already exists for `--add-dir`/prompt
  path expansion, so redirecting is free insurance against the same class of stall Claude hit, not a
  proven necessity for `agy` specifically (Phase 1).
- **`agy`'s scoped-permission flag is `--mode`, defaulting to `"accept-edits"`** when
  `WorkerInvocation.PermissionScope` is unset — the exact value #21 confirmed pre-authorizes file
  edits (v1.1.1+), coarser than Claude's per-tool `--allowedTools` and further confirmation
  `PermissionScope` must stay an opaque, adapter-interpreted string (Phase 1).
- **Phase 4's gate mirrors M11 Phase 4's shape exactly**: `LiveMixedVendorPausedRunSmokeTest`
  lives in the same `Aer.Cli.SmokeTests` project (still absent from `AerFlow.slnx`), driving
  `RunCommand.ExecuteAsync` then `DecideCommand.ExecuteAsync` against a `draft` (Claude) → `review`
  (Gemini/`agy`) fixture where `review` declares the `PausePoint`, so the fixed point after `aer
  run` is `Paused` and the fixed point after `aer decide --type resume` is `Terminal`. A dedicated
  `pixi run smoke-mixed-vendor` task (filtered to just this test, same project as `smoke-claude`)
  and `docs/runbooks/live-mixed-vendor-smoke.md` (a new file, not a rewrite of
  `live-claude-smoke.md`, so M11's recorded run stays an unmodified historical record) round it
  out. **Recorded green 2026-07-13** on a host that happened to carry both `claude` and `agy`
  authenticated (a coincidence of that host, not a capability — see CLAUDE.md's "Live-vendor smoke
  tests"; the phase that implemented this test only had `claude`, so it left the run un-executed).
  The first live attempt caught a real, Windows-only bug in both adapters (not a fixture bug this
  time): `ClaudeWorkerAdapter`/`GeminiWorkerAdapter` each built one pre-quoted `cmd /c "..."` string,
  which aer-core's Windows spawn re-quoted and corrupted a second time — fixed by passing each token
  as its own `Args` element on Windows instead (see `live-mixed-vendor-smoke.md`'s recorded-run
  entry). With that fix, `pixi run smoke-mixed-vendor` ran to completion end to end.

**M11: First Real Run** — the milestone that made the library runnable: the canonical
worker-invocation protocol and adapter seam, the Claude adapter, the `aer run` pump, and a
recorded green live two-step run (`docs/runbooks/live-claude-smoke.md`).

- ✅ Phase 1 — Canonical worker-invocation protocol + `Aer.Adapters` seam (#84)
- ✅ Phase 2 — Claude worker adapter (headless `claude` CLI) (#85)
- ✅ Phase 3 — `aer run` pump (the CLI driver) (#86)
- ✅ Phase 4 — Live two-step Claude run (gated end-to-end) (#87)

Decisions of record from M11:

- **The gate lives in its own test project, `Aer.Cli.SmokeTests`, deliberately absent from
  `AerFlow.slnx`** — a solution/CI-invoked `dotnet test`/`build`/`lint` never discovers, builds, or
  runs it, which is what keeps a real, key-gated `claude` call out of default CI without any
  trait-based test filtering. `pixi run smoke-claude` (`dotnet test tests/Aer.Cli.SmokeTests`)
  targets the project directly; the runbook (`docs/runbooks/live-claude-smoke.md`) documents
  prerequisites, what "green" means, and how to triage a failure (Phase 4).
- **A worker role that must read an upstream artifact needs `Read` in its `PermissionScope`, not
  just `Write`** — caught by the gate itself, not written in from the start: `ClaudeWorkerAdapter`'s
  default (`"Write"`) is exactly right for a source step with no inputs, but a downstream step's
  worker-binding config must opt into `"Read,Write"` (or list whatever tools its prompt actually
  needs) or `claude` refuses the unapproved `Read` tool call and the step fails its output contract.
  Nothing about this is engine or adapter behavior to fix — `PermissionScope` is deliberately an
  opaque, adapter-interpreted string (Phase 1's decision); it is a per-worker config fact the
  `draft-review-bindings.json` fixture now gets right, and the runbook calls it out for anyone
  authoring a new worker-binding config (Phase 4).
- **`RunCommand.ExecuteAsync` takes the adapter registry as a plain argument, never constructing
  one itself**: `Program.cs`'s only production wiring decision is passing
  `WorkerAdapterRegistry.Default` (`Aer.Adapters`, `{"claude": ClaudeWorkerAdapter}`) — every other
  layer (argument parsing, snapshot/bindings loading, the pump call) is identical whether the
  caller is the real CLI or a test supplying its own registry. This is what let Phase 3's
  completion gate reach the real `IWorkerAdapter`/bindings-config seam end to end with a
  deterministic shell-stub adapter (`ShellCommandWorkerAdapter`, test-only — runs its
  `WorkerInvocation.PromptTemplate` directly as a shell command, the same `sh -c`/`cmd /c`
  convention `ClaudeWorkerAdapter` and the M7 shell-stub workers already use) instead of a live
  LLM, without a single line of the production driver being test-only code (Phase 3).
- **A task directory's `snapshot.json` existence is the fresh-vs-resumed signal, not a separate
  flag**: `RunCommand` binds and persists a new snapshot from the workflow file only when
  `snapshot.json` is absent; otherwise it loads the persisted one (`SnapshotBinder.LoadFromFileAsync`,
  new) and never re-reads the workflow file at all. This is what makes `aer run`'s own resume story
  (§21: "running `aer run` again picks up from the log") match spec §11.2's guarantee that a task
  stays bound to the snapshot it was created from, even if the source template file changes or
  disappears between runs (Phase 3).
- **`--task-dir` defaults to `.aer/<workflow-file-stem>` under the current directory when omitted**,
  so `aer run workflow.json` twice in the same directory naturally resumes the same task without
  requiring an explicit path every time — still overridable, and still the one thing that must stay
  stable across a resume (Phase 3).
- **Malformed CLI arguments are their own exception type** (`CliArgumentException : AerFlowException`,
  `Aer.Cli`), parsed by `RunOptionsParser` before any file is touched — mirrors
  `WorkflowDefinitionValidationException`/`WorkerBindingConfigException` one layer up, per
  CLAUDE.md's error-handling rules. `Program.cs`'s `Main` is the one place any `AerFlowException`
  is caught at all, turning it into a stderr message and a non-zero exit code instead of a raw stack
  trace (Phase 3).
- **The de-risking spike's question — whether the real M5 binding works as the dispatcher
  assumes — was already answered by M7**: `WorkflowEndToEndTests` has dispatched through the real
  `CoreDispatcher` and the real `aer-core` binding (never `StubCoreDispatcher`) since Phase 8, and
  passes on both CI platforms today. Phase 3 adds no separate throwaway spike file; the same
  dual-OS CI green on the existing and newly added end-to-end suites *is* the spike's answer,
  re-confirmed rather than re-litigated (Phase 3).

- **The Claude adapter shell-wraps every invocation and never relies on cwd.** `ClaudeWorkerAdapter`
  spawns `sh -c`/`cmd /c` around the real `claude` invocation rather than the bare binary, for two
  reasons that share one mechanism: spike #21 found a per-call stdin stall without explicit
  redirection (aer-core's own process spawn never sets stdin itself — it inherits the host's), and
  per-execution paths must reach the prompt as live `$AER_INPUT_<n>`/`$AER_OUTPUT_DIR` shell
  expansions, not embedded literal paths, per the `WorkerInvocation` decision below. #21's raw spike
  script happened to work by relying on the invoking process's cwd, but that finding validates spec
  §16's actual design (paths via env vars, never cwd inference) rather than licensing a cwd-based
  shortcut here — `CoreDispatchTarget` has no cwd concept for an adapter to set even if it wanted to
  (Phase 2).
- **Config-authored text (prompt template, model, permission scope) is escaped before being
  embedded in the generated shell command; the adapter's own generated `AER_INPUT_<n>`/
  `AER_OUTPUT_DIR` references are deliberately left unescaped**, so they still expand live at spawn
  time — the same shell-wrapping mechanism serves both stdin redirection and path interpolation
  without one undermining the other (Phase 2).
- **`WorkerInvocation` cannot carry a resolved, execution-specific file path.** `MutationInterface.StartWorkflowAsync` captures the `IReadOnlyDictionary<string, WorkerBinding>` once and loops internally to a fixed point (§21) — one `CoreDispatchTarget` per worker role is reused across every round, every step, and every concurrent fan-out dispatch of that role. `IWorkerAdapter.Resolve(WorkerInvocation, WorkerContract)` therefore runs once, when a worker-binding config entry is resolved into a binding, not once per execution. Per-execution dynamism stays exactly where M7 Phase 6 put it: `AER_INPUT_<n>`/`AER_OUTPUT_DIR` environment variables, resolved fresh per dispatch by the unchanged `ArtifactManager`. An adapter that needs literal absolute paths in its prompt text (`agy`, M12 — spike #21) gets there by shell-wrapping its `CoreDispatchTarget` so the shell expands the env var at spawn time, the same convention the shell-stub test workers already use — no new mechanism (Phase 1).
- **The canonical record doesn't duplicate `WorkerContract`.** `IWorkerAdapter.Resolve` takes the `WorkerContract` alongside `WorkerInvocation` rather than folding `RequiredInputs`/`ProducedOutputs` into the invocation record — the contract already carries the ordered input role names and declared outputs; `WorkerInvocation` adds only what it doesn't: the human-authored `PromptTemplate`, and the opaque vendor-specific `Model`/`PermissionScope` strings (spike #21: no shared permission vocabulary across vendors) (Phase 1).
- **Worker-binding config is a flat JSON object keyed by worker role name**, deserialized with the same case-sensitive, no-naming-policy `JsonSerializer` defaults `WorkflowDefinitionParser` already established for templates — one config format convention for the whole repo, not two. Lives in `Aer.Adapters` (`WorkerBindingConfigParser`/`WorkerBindingConfigEntry`/`WorkerBindingResolver`), entirely outside `Aer.Flow`, per CLAUDE.md's Adapter Isolation rule. `WorkerBindingResolver.Resolve` takes the adapter registry (`IReadOnlyDictionary<string, IWorkerAdapter>`) as a plain caller-supplied argument — no adapter-registration mechanism was built, since Phase 1 excludes every adapter but the fake/echo test double; Phase 2/3 register the real one the same way (Phase 1).
- **Every worker-binding config entry resolves to `WorkerBinding.Process`.** A worker-binding config describes a real vendor invocation; `WorkerBinding.NonProcess` (spec §17.3, human/non-process parties) is unrelated to this seam and continues to be constructed directly by whatever caller needs one, unchanged since M9 (Phase 1).

**M10: Cancellation & Edge Cases** — on-demand cancellation through the single mutation surface (intent recorded first), and crash-recovery made whole by reading back the Core half of the log.

- ✅ Phase 1 — Cancellation mutation surface: record, validate, non-process targets (#69)
- ✅ Phase 2 — Live cancellation delivery: in-flight Core executions (#70)
- ✅ Phase 3 — Crash-recovery reconciliation: reading back the Core log (#71)
- ✅ Phase 4 — Cancellation + crash-recovery end-to-end integration tests (#72)

Decisions of record from M10:

- **The pump's own host process is the only delivery point for a live execution, by construction**:
  §15's guard is held for a mutation call's entire duration, so a second call — even from the same
  process — cannot acquire it while a pump is in flight (verified empirically: .NET's
  `FileShare.None` conflicts across handles in the same process on Linux, not just across
  processes). `InFlightExecutionRegistry` is therefore an in-process handle the caller retains
  *before* calling `StartWorkflowAsync`/`RecordDecisionAsync`/`RequestCancellationAsync`, populated
  as each call dispatches, so cancellation of one specific live execution — or a host-initiated
  stop of everything in flight — can reach the pump while it is still running, with no second
  mutation-surface call and no daemon (Phase 2).
- **Every process dispatch is registered under its own `CancellationTokenSource`, never the
  ambient host token directly**: closes the passive path where a host's own token used to reach
  Core with nothing recorded. A host stop mints `CancellationRequested` for every execution still
  in flight (fsync'd, one append per execution) *before* any of them is signalled; a targeted
  `InFlightExecutionRegistry.RequestCancellationAsync` call does the identical
  record-then-signal for exactly one, leaving its siblings untouched (Phase 2).
- **Once a host stop is detected, the pump's own I/O switches to an uncancellable token**: the
  ambient `CancellationToken` firing must not stop the pump from reading/writing its way to a
  consistent fixed point — only from admitting new dispatches. Reusing the now-cancelled token for
  later reads/writes would throw immediately and strand the call mid-shutdown (Phase 2).
- **`IEventLogReader` gained `ReadAllCoreEventsAsync` rather than widening `ReadAllAsync`'s return
  type**: every existing caller already treats `ReadAllAsync` as Flow-events-only, so an additive
  method reads back Core's half (§6) for the first time since M7 Phase 6 wrote it without touching
  any of that call-site surface (Phase 3).
- **A dispatch this same call already has registered is excluded from crash-recovery consideration
  entirely, checked before any of the four crash states**: caught in review — `StubCoreDispatcher`
  never writes a `CoreEvent`, so without this exclusion every genuinely in-flight stub dispatch
  looked identical to "never started" and got wrongly resubmitted mid-flight. The fix generalizes
  what was originally only the orphan branch's guard: `InFlightExecutionRegistry` now exposes a
  `RegisteredExecutionIds()` snapshot the detector checks first (Phase 3).
- **The orphan's best-effort cancellation re-issue is a documented no-op, not a new mechanism**:
  aer-core's binding has no cross-process re-attach or kill-by-`Pid` capability (confirmed against
  `Aer.Core`'s P/Invoke surface) — a crashed pump's `AerCancelHandle` cannot survive the process
  that created it. §7's "best-effort" phrasing already accommodates this; Phase 3 does not invent a
  new kill-by-`Pid` capability to make the re-issue do anything (Phase 3).

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

- **Worker adapter implementation (`Aer.Adapters`)** — the **Claude** adapter shipped in M11 Phase 2 (#85); the **Gemini** adapter (`agy` — antigravity, Google Gemini's CLI) is M12 Phase 1 (#95), with the facts closed spike [#21](https://github.com/aer-works/aer-flow/issues/21) recorded folded into its phase plan above. Read #21's findings before starting it.
