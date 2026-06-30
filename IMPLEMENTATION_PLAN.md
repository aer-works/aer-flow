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

### Phase 1 — Domain model
Define all types: `ExecutionRequest`, `WorkerContract`, `WorkflowDefinition`, `WorkflowDefinitionSnapshot`, `FlowState`, and the complete `flow.jsonl` event discriminated union (`ExecutionRequestAccepted`, `ExecutionRequestRejected`, `ExecutionSucceeded`, `ExecutionFailed`, `ExecutionCancelled`, `CancellationRequested`, `WorkflowPaused`, `ExternalDecisionRecorded`, `WorkflowResumed`, `WorkflowTransition`).

**Produces:** compilable type system; test that all event variants serialize round-trip to/from JSON.  
**Excludes:** all I/O, all logic, anything that runs.  
**Why this boundary:** every subsequent phase imports these types. Wrong shapes (especially the event discriminated union) cause cascading rework across all later phases.

### Phase 2 — Log Manager
Append events to `flow.jsonl` atomically (write-buffer-flush or write-then-rename per §5.3). fsync lifecycle events synchronously before returning. Buffered flush acceptable for observation-tier events (not produced by Flow in M7, but establish the rule).

**Produces:** crash-safe event persistence. Unit tests: verify a partial write is never observable; verify lifecycle events are fsync'd; verify ordering (§7 write-sequence rule: intent before dispatch).  
**Excludes:** reading events, projecting state, dispatch.  
**Why this boundary:** the fsync ordering rule is easy to violate subtly. Testing it in isolation with injected failures is far easier than testing it entangled with projection logic.

### Phase 3 — Template Parser + Snapshot Binder
Load and validate `WorkflowDefinition` from file. Freeze into `WorkflowDefinitionSnapshot`; generate `SnapshotId`. Persist the snapshot alongside the task's log directory.

**Produces:** validated, immutable snapshot ready to feed the projector.  
**Excludes:** projection, scheduling, dispatch.  
**Open question resolved in this phase:** workflow definition file format (spec says implementation-defined; TOML is human-writable, JSONL is consistent with the event log — pick one and document it as the convention).

### Phase 4 — State Projector
Implement `Project(EventStore, WorkflowDefinitionSnapshot) → FlowState`. Read `flow.jsonl` linearly. Reconstruct per-step execution status. Causal linking strictly by `ExecutionId` (§6) — never by line order, file order, or timestamp. Handle the unfinalized-classification case (§6: process ran in Core, Flow has not yet written `ExecutionSucceeded/Failed`).

**Produces:** deterministic state reconstruction. Unit tests driven entirely by event fixture files — no real I/O required.  
**Excludes:** dependency resolution, dispatch.  
**Why this boundary:** the projector is the heart of the determinism guarantee (§13) and the most likely source of subtle correctness bugs. Exhaustive fixture-based tests here are the highest-leverage investment in the whole milestone. Build and prove it before anything else depends on its output.

### Phase 5 — Dependency Resolver
Implement §11.3 readiness check, both conditions:
- Condition 1: dependency's most recent attempt produced `ExecutionSucceeded`.
- Condition 2: this step does not already have a success whose `UpstreamExecutionIds` match the dependency's current most recent successful `ExecutionId`.

Return the set of ready `StepId`s from `FlowState` + `WorkflowDefinitionSnapshot`.

**Produces:** correct step scheduling. Unit tests covering: no steps ready, one step ready, dependency failed so nothing ready, step already succeeded so not re-queued.  
**Excludes:** dispatch, artifacts, retries. Condition 2 is included now per §11.3's "critical" designation, but its full test coverage (staleness after Supersede) requires M9 — note explicitly.  
**Why this boundary:** the resolver has clean, pure inputs (`FlowState` + `Snapshot`) and a clean output (ready steps). Testable with zero I/O. Worth isolating before it gets entangled with the dispatch pipeline.

### Phase 6 — Artifact Manager + Core Dispatcher
Pre-allocate `artifacts/execution_{N}/`. Compute input paths from prior steps' output directories. Call the aer-core M5 `AerTask` binding. Record Core lifecycle events to the log.

**Produces:** actual process execution through Flow. Integration test: trivial worker (`cmd /c echo hello` / `sh -c 'echo hello'`), assert output file appears and events are logged.  
**Excludes:** outcome classification, retry.  
**Open questions resolved in this phase:**
- How are paths passed to workers? Spec says "e.g. environment variables: `AER_INPUT_<n>`, `AER_OUTPUT_DIR`" — adopt this as the convention.
- Who writes Core's events? Spec says Core owns `events.jsonl` (§5.1), but in P/Invoke, Core is a library call inside the Flow process. Decision for M7: single `flow.jsonl` records both Flow and Core-originated events (allowed because §5 says storage backend is implementation-defined); ownership is enforced in the type system, not by physical file separation.

### Phase 7 — Outcome Classifier + Contract Validator + Mutation Interface
Apply §8 classification rules to Core's exit: `NaturalExit + code 0 + all ProducedOutputs exist` → `ExecutionSucceeded`; otherwise `ExecutionFailed`; `CancelRequested` → `ExecutionCancelled`. Walk `WorkerContract.ProducedOutputs` and verify each file exists on disk. Write classification event to the log. Wrap the full sequence — load snapshot → project → resolve → dispatch → classify — as a named `MutationInterface.StartWorkflow()` per §14.

**Produces:** complete end-to-end linear execution with correct, durable outcome classification. Integration test: full three-step workflow, all steps succeed.  
**Excludes:** retries, pauses, cancellation, concurrency protection.

### Phase 8 — Concurrency Guard + end-to-end integration test
Implement file lock per §15 (`FileShare.None` on a `FileStream`; explicitly not a sentinel file — sentinel files survive crashes). Integration test: full three-step linear workflow on a real filesystem; assert `FlowState` projects correctly after completion; assert artifacts exist and are immutable.

**Produces:** M7 complete. CI green on Windows and Linux.

---

## Current Milestone

**M7 — not started.** Blocked on aer-core M5.

## Completed Milestones

None.

---

## Open Questions (spec-level)

These are gaps in `aer-flow-behavioral-spec-v1.0.md` discovered during planning. Each should be resolved via a spec PR before the phase that first encounters it.

- **`WorkflowTransition` event** — listed in §5.2's event table but never defined elsewhere. Is this "workflow reached a terminal state"? Needs clarification before Phase 7.
- **Event Store performance** — full re-read vs. manifest-checkpoint-plus-tail (§21). Deferred until §20's no-daemon question is revisited.
- **Mutation Interface shape** — deliberately unspecified (§14); CLI is the reference implementation. Shape emerges from M7 implementation.
