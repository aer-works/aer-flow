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

Completed milestones keep only their phase checklist and any decisions of record later work
still leans on. The full phase plans — goals, boundaries, and the open questions each phase
resolved — live in this file's git history and in the linked issues.

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
- **Mutation Interface shape** — deliberately unspecified (§14); CLI is the reference implementation. Shape emerges from M7 implementation.

## Notes for future work

- **Worker adapter implementation (`Aer.Adapters`, no milestone yet)** — closed issue [#21](https://github.com/aer-works/aer-flow/issues/21) spiked a raw Claude→agy handoff and recorded facts that must inform whatever phase eventually builds the real Claude/Gemini adapters: each vendor needs a different scoped permission flag (no shared vocabulary), agy does not honor the invoking process's cwd and requires `--add-dir` plus absolute paths interpolated into the prompt text, and Claude needs stdin explicitly redirected to avoid a per-call stall. Read #21's findings before starting that work.
