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
| 21 | **Projection / Authoring UI** | Read model + template/DAG authoring over the event store | UI spec v0.7 |

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
| *(UI track — separate)* | 21 | M11 (UI spec v0.7) |

M7–M10 complete the **v1.0 engine** (the behavioral spec is authoritative for it, and every §5.1 flow event now has a producer). M11 onward turns that engine into a runnable product: the worker adapters and the CLI pump the specs assume (§21, CLAUDE.md rule #2) but no engine milestone built, then distribution and — separately — the v0.7 UI.

---

## M11: First Real Run — Phase Plan

**Goal:** run one real two-step workflow against a real worker (Claude, headless) through a minimal `aer run`, producing real artifacts on disk (§21, §3, §16). M7–M10 completed the v1.0 engine, but every execution to date ran against `StubCoreDispatcher` or a shell command standing in for a worker; `Aer.Adapters` is an empty class and `Aer.Cli` prints `Hello, World!`. This is the milestone that makes the library runnable — deliberately happy-path only, exactly as M7 scoped the engine's first end-to-end (linear, no failures): one vendor, no `decide`/`cancel` from the CLI yet (M12), no second vendor (M12), no UI.

Two facts shape the plan. First, **the pump already exists as test code.** `WorkflowEndToEndTests` already drives the engine's only loop — project state, resolve ready steps, dispatch through the single Mutation Interface, await, repeat to a terminal state — against stubs. `aer run` is that loop with real adapters wired in and a real host process around it; the engine surface it calls does not change. The genuinely new code is small and sits at the edges: the adapter seam, one adapter, and a config that maps a workflow's abstract worker names to real invocations. Second, **the vendor choice is settled by de-risking, not preference.** Closed spike #21 recorded that the `claude` CLI needs exactly one invocation accommodation (stdin redirected to avoid a per-call stall), while `agy` — antigravity, Google Gemini's CLI — needs three (it ignores the invoking process's cwd, so it needs `--add-dir` and absolute input paths interpolated into the prompt text). The first slice takes the vendor with the smallest invocation surface; the fiddlier one is M12, once the seam it plugs into is proven.

The phase boundaries follow the same seam discipline as every milestone before: define the vendor-agnostic protocol and its seam with nothing real behind it, then one real adapter behind it, then the driver that turns the library into a process, then the live gate last.

### Phase dependency table

| Phase | Requires output from |
|---|---|
| 1 — Canonical worker-invocation protocol + `Aer.Adapters` seam | — (consumes M7's `CoreDispatchTarget`, `WorkerBinding`, `ArtifactManager`) |
| 2 — Claude worker adapter (headless `claude` CLI) | 1 (the seam it implements) |
| 3 — `aer run` pump (the CLI driver) | 2 (a real adapter to dispatch through) + M5 binding live |
| 4 — Live two-step Claude run (gated end-to-end) | 3 |

### Phase 1 — Canonical worker-invocation protocol + `Aer.Adapters` seam (#84)
CLAUDE.md rule #2 made concrete. Define the vendor-neutral record the engine hands a worker adapter — the resolved prompt/template, the input artifact paths `ArtifactManager` already resolves (§16), the assigned `AER_OUTPUT_DIR`, declared outputs, permission scope, model — and `IWorkerAdapter`, which maps it to a `CoreDispatchTarget` (the command/args/env the `CoreDispatcher` shipped in M7 Phase 6 already executes). Every vendor quirk lives behind this interface; `Aer.Flow` gains no vendor knowledge. Adapter resolution wires into `WorkerBinding` construction (worker name → adapter), leaving the frozen `WorkflowDefinitionSnapshot` untouched — dispatch details stay off the step, exactly where M7 Phase 7 put the timeout.

**Produces:** the canonical → `CoreDispatchTarget` mapping under a fake/echo adapter; the worker-binding config parsed and resolved into bindings. Unit tests only — no real vendor, no live process.
**Excludes:** the Claude adapter (Phase 2), the pump (Phase 3), live runs (Phase 4).
**Open questions resolved in this phase:**
- **Where worker-binding config lives.** A workflow names abstract workers; the mapping `worker name → {adapter, model, permission scope, prompt template}` is a run-time sidecar config, *not* the snapshot — the snapshot stays a pure frozen §11.2 template and the same run is reproducible from workflow + bindings. Mirrors the M7 decision to keep timeout on the binding, not the step.
- **What the canonical protocol carries vs. what the adapter owns.** The protocol is paths, names, and intent; how those become a command line — flag vocabulary, cwd handling, stdin — is entirely the adapter's, so adding a second vendor (M12) changes no engine or protocol code.

### Phase 2 — Claude worker adapter (headless `claude` CLI) (#85)
The first real adapter. `ClaudeWorkerAdapter` builds the `CoreDispatchTarget` for a headless `claude` invocation, honoring the facts spike #21 recorded (read them first): Claude's scoped-permission flag (each vendor's is different — the reason this isn't shared code), stdin explicitly redirected to avoid #21's per-call stall, and a prompt-template convention that tells Claude to write each declared output to its assigned path under `AER_OUTPUT_DIR`. A worker that fails to write its declared output is not a special case — `ContractValidator` (M7 Phase 7) already reads that as an unsatisfied contract, which the classifier maps to a retryable failure (§8) and the Retry Engine handles (§10).

**Produces:** the constructed command / args / env / prompt asserted by unit test. No live API call in CI (Phase 4's gate).
**Excludes:** the Gemini/`agy` adapter and the canonical protocol's generalization across two vendors (M12); the pump (Phase 3); live runs (Phase 4).
**Open questions resolved in this phase:**
- **How an LLM worker's success is defined.** By the existing contract: declared output files present on disk (§4.1, §8). The adapter's only job toward this is a prompt that reliably instructs the writes; verification stays the engine's, unchanged.

### Phase 3 — `aer run` pump (the CLI driver) (#86)
§21's "the CLI is the pump" made real. `aer run <workflow.json> [--bindings <config>]` parses the workflow and its bindings, resolves adapters into `WorkerBinding`s, and runs the project → resolve → dispatch → await loop through the single Mutation Interface to a terminal state — the loop `WorkflowEndToEndTests` has exercised since M7, now with a real adapter and a real host process (the same host M10 Phase 2 built the in-flight cancellation surface for, still un-wired to a CLI). Malformed workflow or bindings surface as typed `AerFlowException`s, never bare `InvalidOperationException` (CLAUDE.md error rules).

This phase **opens with a spike**, because it is where the P/Invoke boundary is first crossed for real: every prior milestone ran against `StubCoreDispatcher`, and the `external/aer-core` submodule is uninitialized in a fresh checkout. Before building the pump, dispatch a trivial `echo` worker through the real M5 binding (`git submodule update --init`, `pixi run build-core`) and confirm an `AerTask` round-trips. If the binding needs work, that surfaces here, on one line of throwaway code, not woven through the driver.

**Produces:** `aer run` driving a multi-step workflow end-to-end through the shell-stub adapter — deterministic, CI-safe — to completion; the real-binding spike green on both platforms.
**Excludes:** `aer decide` / `aer cancel` (M12 — the mutation surface has existed since M9/M10; this is only about exposing it on the CLI); any live LLM (Phase 4).
**Open questions resolved in this phase:**
- **Whether the real M5 binding works as the dispatcher assumes.** Answered by the opening spike before any pump code depends on it. If it does not, M11 stops here and the fix is scoped as its own work — the risk is named, not absorbed.

### Phase 4 — Live two-step Claude run (gated end-to-end) (#87)
The M11 completion gate, playing the role #14/#48/#61/#72 played for the engine milestones — but against a real worker, so it cannot live in default CI. A real draft → review workflow run through `aer run` against the real `claude` CLI, producing real artifacts, behind a key-gated `pixi run` smoke task with a documented runbook. This is the first time aer-flow runs a real workflow against a real worker; after it, `Aer.Adapters` and `Aer.Cli` are no longer stubs.

**Produces:** M11 complete — the library is runnable. A green live run recorded in the runbook; the shell-stub path still guarding the same loop in CI.
**Excludes:** the second vendor and CLI mutation commands (M12); packaging as a `dotnet tool` (M13); the UI (v0.7 spec).

---

## Current Milestone

**M11: First Real Run** — phase plan above. Progress:

- ⬜ Phase 1 — Canonical worker-invocation protocol + `Aer.Adapters` seam (#84)
- ⬜ Phase 2 — Claude worker adapter (headless `claude` CLI) (#85)
- ⬜ Phase 3 — `aer run` pump (the CLI driver) (#86)
- ⬜ Phase 4 — Live two-step Claude run (gated end-to-end) (#87)

Decisions of record accrue here as phases land.

## Completed Milestones

Completed milestones keep only their phase checklist and any decisions of record later work
still leans on. The full phase plans — goals, boundaries, and the open questions each phase
resolved — live in this file's git history and in the linked issues.

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

## Notes for future work

- **Worker adapter implementation (`Aer.Adapters`)** — now scheduled: the **Claude** adapter is M11 Phase 2 (#85), the **Gemini** adapter (`agy` — antigravity, Google Gemini's CLI) is M12. Closed issue [#21](https://github.com/aer-works/aer-flow/issues/21) spiked a raw Claude→Gemini handoff and recorded the facts both phases must honor: each vendor needs a different scoped permission flag (no shared vocabulary), `agy` does not honor the invoking process's cwd and requires `--add-dir` plus absolute paths interpolated into the prompt text (three accommodations), and `claude` needs stdin explicitly redirected to avoid a per-call stall (one) — which is why M11 takes Claude first. Read #21's findings before starting Phase 2.
