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

---

## Milestone Roadmap

Which milestone introduces which capabilities.

| Milestone | Capabilities introduced | Blocked by |
|---|---|---|
| **M7: Foundation** | 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12 | aer-core M5 |
| **M8: Reactive Scheduler** | 10 (Retry Engine); full fan-out/fan-in DAG testing; manifest cache if scale demands | M7 |
| **M9: External Decisions** | 13, 14, 15, 16 (all pause/decision/supersede/human machinery) | M8 |
| **M10: Cancellation & Edge Cases** | §9 cancellation flow; crash recovery hardening (§7 full robustness) | M9 |

---

## M7: Foundation — Phase Plan

**Goal:** Execute a linear A → B → C workflow end-to-end. Happy path only. No retries, no pause points, no concurrent steps.

### Phase dependency table

| Phase | Requires output from |
|---|---|
| 1 — Domain model | — |
| 2 — Log Manager | 1 (event types to append) |
| 3 — Template Parser + Snapshot Binder | 1 (`WorkflowDefinition`, `SnapshotId` types) |
| 4 — State Projector | 1 (event discriminated union), 2 (log reader), 3 (`WorkflowDefinitionSnapshot`) |
| 5 — Dependency Resolver | 3 (`WorkflowDefinitionSnapshot`), 4 (`FlowState`) |
| 6 — Artifact Manager + Core Dispatcher | 1–5 (all types, log writer, snapshot, projector, resolver) |
| 7 — Outcome Classifier + Contract Validator + Mutation Interface | 6 (dispatch produces lifecycle events to classify) |
| 8 — Concurrency Guard | 7 (full end-to-end path exists to wrap and guard) |

### Phase 1 — Domain model
Define all types: `ExecutionRequest`, `WorkerContract`, `WorkflowDefinition`, `WorkflowDefinitionSnapshot`, `FlowState`, and the complete `flow.jsonl` event discriminated union (`ExecutionRequestAccepted`, `ExecutionRequestRejected`, `ExecutionSucceeded`, `ExecutionFailed`, `ExecutionCancelled`, `CancellationRequested`, `WorkflowPaused`, `ExternalDecisionRecorded`, `WorkflowResumed`). Workflow-level status is a pure projection, not an event — see spec §5.2.

**Produces:** compilable type system; test that all event variants serialize round-trip to/from JSON.  
**Excludes:** all I/O, all logic, anything that runs.  
**Why this boundary:** every subsequent phase imports these types. Wrong shapes (especially the event discriminated union) cause cascading rework across all later phases.

### Phase 2 — Log Manager
Append events to `flow.jsonl` atomically (write-buffer-flush or write-then-rename per §5.3). fsync lifecycle events synchronously before returning. Buffered flush acceptable for observation-tier events (not produced by Flow in M7, but establish the rule).

**Produces:** crash-safe event persistence. Unit tests: verify a partial write is never observable; verify lifecycle events are fsync'd; verify ordering (§7 write-sequence rule: intent before dispatch).  
**Excludes:** reading events, projecting state, dispatch.  
**Why this boundary:** consumes Phase 1 event types. The fsync ordering rule is easy to violate subtly — testing it in isolation with injected failures is far easier than testing it entangled with projection logic.

### Phase 3 — Template Parser + Snapshot Binder
Load and validate `WorkflowDefinition` from file. Freeze into `WorkflowDefinitionSnapshot`; generate `SnapshotId`. Persist the snapshot alongside the task's log directory.

**Produces:** validated, immutable `WorkflowDefinitionSnapshot` — the input both the State Projector (Phase 4) and Dependency Resolver (Phase 5) require.  
**Excludes:** projection, scheduling, dispatch.  
**Open question resolved in this phase:** workflow definition file format is plain JSON (`.json`, one document — not `.jsonl`), deserialized through the same `System.Text.Json` converters already used for every other domain record and for `flow.jsonl` itself. See `Aer.Flow.Templates.WorkflowDefinitionParser`.

### Phase 4 — State Projector
Implement `Project(EventStore, WorkflowDefinitionSnapshot) → FlowState`. Read `flow.jsonl` linearly (via Phase 2's log reader). Reconstruct per-step execution status. Causal linking strictly by `ExecutionId` (§6) — never by line order, file order, or timestamp. Handle the unfinalized-classification case (§6: process ran in Core, Flow has not yet written `ExecutionSucceeded/Failed`).

**Produces:** `FlowState` — the primary input to Phase 5's Dependency Resolver. Unit tests driven entirely by event fixture files, no real I/O required.  
**Excludes:** dependency resolution, dispatch.  
**Why this boundary:** the projector is the heart of the determinism guarantee (§13) and the most likely source of subtle correctness bugs. Exhaustive fixture-based tests here are the highest-leverage investment in the whole milestone. Build and prove it before the Dependency Resolver depends on its output.

### Phase 5 — Dependency Resolver
Implement §11.3 readiness check, both conditions:
- Condition 1: dependency's most recent attempt produced `ExecutionSucceeded`.
- Condition 2: this step does not already have a success whose `UpstreamExecutionIds` match the dependency's current most recent successful `ExecutionId`.

Takes `FlowState` (from Phase 4) + `WorkflowDefinitionSnapshot` (from Phase 3) as inputs. Returns the set of ready `StepId`s.

**Produces:** correct step scheduling. Unit tests covering: no steps ready, one step ready, dependency failed so nothing ready, step already succeeded so not re-queued.  
**Excludes:** dispatch, artifacts, retries. Condition 2 is included now per §11.3's "critical" designation, but its full test coverage (staleness after Supersede) requires M9 — note explicitly.  
**Why this boundary:** both inputs (`FlowState`, `WorkflowDefinitionSnapshot`) are now available; the resolver's output (ready `StepId`s) is clean and testable with zero I/O. Isolate before it gets entangled with dispatch.  
**Open question resolved in this phase:** condition 2 needs each step's recorded `UpstreamExecutionIds` to compare against a dependency's current latest success, but Phase 4's `FlowState`/`StepState` didn't carry it. Extended `StepState` with an `UpstreamExecutionIds` field (populated by the State Projector from each step's latest `ExecutionRequestAccepted`) rather than having the resolver re-read raw events — keeps the resolver's declared inputs (`FlowState` + `WorkflowDefinitionSnapshot`) accurate. A step whose latest attempt is `Failed`/`Cancelled` is excluded from readiness (M7 has no Retry Engine; re-running it would be an undeclared ad hoc retry).

### Phase 6 — Artifact Manager + Core Dispatcher
Pre-allocate `artifacts/execution_{N}/`. Compute input paths from prior steps' output directories. Call the aer-core M5 `AerTask` binding. Record Core lifecycle events to the log (Phase 2's writer).

**Produces:** actual process execution through Flow. Integration test: trivial worker (`cmd /c echo hello` / `sh -c 'echo hello'`), assert output file appears and events are logged.  
**Excludes:** outcome classification, retry.  
**Open questions resolved in this phase:**
- How are paths passed to workers? Spec says "e.g. environment variables: `AER_INPUT_<n>`, `AER_OUTPUT_DIR`" — adopt this as the convention. `ArtifactManager.ResolveInputPaths` matches a step's declared `Inputs` names against its direct dependencies' declared `Outputs` names to find each input's producing step.
- Who writes Core's events? Spec says Core owns `events.jsonl` (§5.1), but in P/Invoke, Core is a library call inside the Flow process. Decision for M7: single `flow.jsonl` records both Flow and Core-originated events (allowed because §5 says storage backend is implementation-defined); ownership is enforced in the type system (`LogEntry.FlowLogEntry` vs. `LogEntry.CoreLogEntry`), not by physical file separation.
- How does `Aer.Flow` consume the aer-core M5 binding, given aer-core publishes no package? Vendored as a pinned git submodule (`external/aer-core`); `pixi run build-core` builds its native library from source. Revisit with a real package feed only once a second consumer of aer-core exists (AER Overview §6) — not needed for a single-developer project today.

### Phase 7 — Outcome Classifier + Contract Validator + Mutation Interface
Apply §8 classification rules to Core's exit: `NaturalExit + code 0 + all ProducedOutputs exist` → `ExecutionSucceeded`; otherwise `ExecutionFailed`; `CancelRequested` → `ExecutionCancelled`. Walk `WorkerContract.ProducedOutputs` and verify each file exists on disk. Write classification event to the log. Wrap the full sequence — load snapshot → project → resolve → dispatch → classify — as a named `MutationInterface.StartWorkflow()` per §14.

**Produces:** complete end-to-end linear execution with correct, durable outcome classification. Integration test: full three-step workflow, all steps succeed.  
**Excludes:** retries, pauses, cancellation, concurrency protection.  
**Why this boundary:** Phase 6 produces lifecycle events but does not classify them — classification is its own correctness concern (§8's rules are non-trivial) and must be locked down before the concurrency guard wraps the whole path.  
**Open questions resolved in this phase:**
- `WorkflowTransition` spec gap: already resolved before this phase (see Open Questions below, #15) — §5.2 itself now states workflow-level status is a pure projection, not a stored event. Nothing further to do here.
- `MutationInterface`'s worker-resolution shape (spec §4, §14): `StartWorkflowAsync` takes an `IReadOnlyDictionary<string, WorkerBinding>` mapping a step's `Worker` role name to the `WorkerContract` (for classification), the `CoreDispatchTarget` (the concrete binary — `Aer.Adapters` doesn't exist yet, no milestone), and a per-worker `Timeout`. `WorkflowStepDefinition` itself carries no timeout field, so this is where one had to be introduced; scoping it to the binding rather than the step keeps the frozen `WorkflowDefinitionSnapshot` shape (§11.2) unchanged.
- Where a worker's self-reported `FailureClassification` (§8.1) lives: the first of the contract's declared `OptionalMetadata` file names (checked in order) that exists in the output directory, parses as JSON, and has a top-level `FailureClassification` field wins. Absent or unrecognized is `null`, which callers (and the domain type itself) already treat as `Retryable`.
- A latent Dependency Resolver bug (Phase 5, #11) surfaced only once Phase 7 actually drove execution in a loop: a root step (no `DependsOn`) that already succeeded was vacuously "ready" forever, because §11.3 condition 2 is only ever checked inside the `DependsOn` loop. Fixed in `DependencyResolver.IsReady` with an explicit early return; covered by a new `DependencyResolverTests` case.

### Phase 8 — Concurrency Guard + end-to-end integration test
Implement file lock per §15 (`FileShare.None` on a `FileStream`; explicitly not a sentinel file — sentinel files survive crashes). Wraps the complete Phase 7 end-to-end path. Integration test: full three-step linear workflow on a real filesystem; assert `FlowState` projects correctly after completion; assert artifacts exist and are immutable.

**Produces:** M7 complete. CI green on Windows and Linux.  
**Why this boundary:** the guard must wrap a complete, proven path — adding it before Phase 7's integration test would be guarding an unproven sequence.  
**Open questions resolved in this phase:**
- Where the guard lives: `ConcurrencyGuard.Acquire(taskDirectoryPath)` (`Aer.Flow.Concurrency`) is held for the full duration of `MutationInterface.StartWorkflowAsync` (now `WorkflowLockedException`-throwing and `taskDirectoryPath`-scoped) rather than left to each caller to remember — consistent with §14's "exactly one well-defined mutation surface" framing, since the single mutation surface is the only place that guarantee needs enforcing today.
- Lock file naming: `flow.lock`, one per task directory, left on disk (not deleted) on release — its existence is deliberately meaningless per §15; only the live `FileShare.None` hold signals "locked".

---

## M8: Reactive Scheduler — Phase Plan

**Goal:** Execute a fan-out/fan-in DAG (A → B, C → D) end-to-end with failed steps retried per `RetryPolicy` (§10), the worker-reported `Permanent` short-circuit honored (§8.1), and every ready step in flight concurrently. Still no pauses, no external decisions, no supersede (M9); no cancellation (M10).

Two findings from M7 shape this plan. First, §4.1 output conditions already landed in Phase 7's Contract Validator, so §10.1's retry-as-bounded-self-iteration pattern needs zero new mechanism — it emerges the moment the Retry Engine exists, and Phase 4 only has to *test* it. Second, M7's mutation loop already re-projects and re-dispatches until a fixed point, so retries do not need a new driver: making the Dependency Resolver retry-aware (Phase 2) is sufficient, and the loop rework (Phase 3) is purely about concurrency, not about retry.

### Phase dependency table

| Phase | Requires output from |
|---|---|
| 1 — Attempt-history projection | — (extends M7 Phase 4's projector) |
| 2 — Retry Engine + retry-aware readiness | 1 (`ConsecutiveFailureCount`, `LatestFailureClassification` in `StepState`) |
| 3 — Reactive concurrent dispatch | 2 (retry-aware readiness must be settled before the loop dispatches many steps at once) |
| 4 — DAG + retry end-to-end integration tests | 3 |

### Phase 1 — Attempt-history projection (#45)
Extend `StepState` with the two facts the Retry Engine consumes, both already durably recorded in `flow.jsonl` and merely not yet surfaced: `ConsecutiveFailureCount` (trailing consecutive `ExecutionFailed` attempts; resets to 0 on success) and `LatestFailureClassification` (from the latest attempt's `ExecutionFailed` event; `null` means `Retryable` per §8.1). Purely additive — nothing consumes the fields until Phase 2.

**Produces:** the Retry Engine's inputs, populated from event history alone (§13). Fixture-driven `StateProjectorTests` cases, same style as M7 Phase 4.
**Excludes:** any behavior change.
**Open question resolved in this phase:** what an "attempt" counts against. Consecutive failures *since the last success* (not all attempts ever), so that when M9's `Supersede` re-runs an already-succeeded step, the new round starts with a fresh retry budget — matching §11.3's "only the latest attempt per step matters" framing.

### Phase 2 — Retry Engine + retry-aware readiness (#46)
Capability 10. A pure predicate — `RetryEngine.MayRetry(StepState, RetryPolicy)` — true exactly when the latest attempt is `Failed`, the classification is not `Permanent`, and `ConsecutiveFailureCount < MaxAttempts` (`MaxAttempts` is total attempts per round). `Cancelled` is never retried (§9, §10). The Dependency Resolver's M7 "latest attempt `Failed` is terminal" skip becomes: a `Failed` step passing `MayRetry` proceeds into the normal §11.3 readiness check. `WorkflowDefinitionValidator` gains `MaxAttempts >= 1` (previously unvalidated).

**Produces:** end-to-end retry through the *unchanged* M7 loop — each iteration re-projects, the failed step shows up ready again, and `ExecuteStepAsync` already mints a fresh `ExecutionId` per dispatch (§10's new-request rule). Unit tests for the predicate and resolver; a mutation-level fail-once-then-succeed test asserting §10's history shape.
**Excludes:** concurrent dispatch, real-process tests, retry backoff/delay (no spec basis — `RetryPolicy` is attempt counts only).
**Open questions resolved in this phase:**
- Where the retry decision lives: `Aer.Flow.Scheduling.RetryEngine`, a separate pure class consulted by the Dependency Resolver — §10's logic gets a single named home matching the capability map, and the resolver's declared inputs (`FlowState` + `WorkflowDefinitionSnapshot`) stay accurate since `RetryPolicy` rides in the snapshot.
- "Terminally failed" is a derived fact (`Failed` ∧ ¬`MayRetry`), never a stored event — per §5.2's rule that anything replay can derive belongs in the projection.

### Phase 3 — Reactive concurrent dispatch (#47)
Rework `MutationInterface.StartWorkflowAsync` from dispatch-one-await-one to an in-flight set with completion-driven reaction: each round dispatches *all* ready steps not already in flight; when *any* in-flight execution completes (`Task.WhenAny`, not `WhenAll`), classify, append the outcome, and run the next round while the rest stay in flight. Fixed point generalizes: return when nothing is in flight and nothing is ready.

**Produces:** the milestone's namesake. B and C of a diamond run simultaneously; a slow C neither delays B's downstream nor a retry of B. Stub-dispatcher tests with `TaskCompletionSource`-controlled completion order.
**Excludes:** concurrency caps, real-process tests.
**Open questions resolved in this phase:**
- Determinism under concurrency (§13): `ExecutionRequestAccepted` events are appended and fsync'd sequentially in snapshot declaration order *before* their dispatches are awaited — emission order within a round stays deterministic even though completion order is not. Completion order only influences *when* the next projection happens, never *what* it concludes.
- Log-writer safety: `FlowEventLogWriter`'s existing `SemaphoreSlim` gate already serializes interleaved appends — verified with a concurrent-append test, not re-implemented.
- **No concurrency cap in M8**, recorded deliberately: `ExecutionRequestRejected` (§5.2, "e.g. concurrency cap") stays unexercised until an admission cap exists. Adding one is a real design decision (rejection is durable; what re-admits a rejected step?) deferred until a workload needs it.

### Phase 4 — Fan-out/fan-in + retry end-to-end integration tests (#48)
The M8 completion gate, playing #14's role for this milestone: real processes, real filesystem, both CI platforms. Diamond DAG with real shell workers; a deterministically flaky worker (fails first attempt, succeeds second — keyed off durable state in a shared *input* directory, since each attempt's output directory is fresh by design, §16); the §10.1 self-iteration pattern (`verdict.json` `needs_revision` → `approved` under a §4.1 output condition); the `Permanent` short-circuit (exactly one attempt despite remaining budget); exhaustion (`MaxAttempts: 2` → exactly two attempts, downstream never dispatched). Artifact directories for failed attempts remain untouched — history is never cleaned up (§10, §16).

**Produces:** M8 complete. CI green on Windows and Linux.
**Also in this phase:** the roadmap's "manifest cache if scale demands" gets its answer — measure full-log re-read overhead in the integration suite and record the verdict here. Expectation per §21: per-task-namespace scoping keeps logs small; defer the manifest cache (§12.1), since the efficient read strategy depends on the unresolved no-daemon question (§20/§21) and should not be pre-empted by this milestone.

---

## Current Milestone

**M8: Reactive Scheduler** — phase plan above. Progress:

- ⬜ Phase 1 — Attempt-history projection (#45)
- ⬜ Phase 2 — Retry Engine + retry-aware readiness (#46)
- ⬜ Phase 3 — Reactive concurrent dispatch (#47)
- ⬜ Phase 4 — Fan-out/fan-in + retry end-to-end integration tests (#48)

## Completed Milestones

**M7: Foundation.** Phase progress:

- ✅ Phase 1 — Domain model (#7)
- ✅ Phase 2 — Log Manager (#8)
- ✅ Phase 3 — Template Parser + Snapshot Binder (#9)
- ✅ Phase 4 — State Projector (#10)
- ✅ Phase 5 — Dependency Resolver (#11)
- ✅ Phase 6 — Artifact Manager + Core Dispatcher (#12)
- ✅ Phase 7 — Outcome Classifier + Contract Validator + Mutation Interface (#13)
- ✅ Phase 8 — Concurrency Guard + end-to-end integration test (#14)

---

## Open Questions (spec-level)

These are gaps in `aer-flow-behavioral-spec-v1.0.md` discovered during planning. Each should be resolved via a spec PR before the phase that first encounters it.

- ~~**`WorkflowTransition` event**~~ — resolved (#15): the event was removed from the spec; workflow-level status is a pure projection of step-level and pause/resume events (§5.2, §12).
- **Event Store performance** — full re-read vs. manifest-checkpoint-plus-tail (§21). Deferred until §20's no-daemon question is revisited.
- **Mutation Interface shape** — deliberately unspecified (§14); CLI is the reference implementation. Shape emerges from M7 implementation.

## Notes for future work

- **Worker adapter implementation (`Aer.Adapters`, no milestone yet)** — closed issue [#21](https://github.com/aer-works/aer-flow/issues/21) spiked a raw Claude→agy handoff and recorded facts that must inform whatever phase eventually builds the real Claude/Gemini adapters: each vendor needs a different scoped permission flag (no shared vocabulary), agy does not honor the invoking process's cwd and requires `--add-dir` plus absolute paths interpolated into the prompt text, and Claude needs stdin explicitly redirected to avoid a per-call stall. Read #21's findings before starting that work.
