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

## M9: External Decisions — Phase Plan

**Goal:** Pause a workflow at a `PausePoint` (§17.1), record each of the four external decision types through the single mutation surface (§17.2, §14), and drive their consequences to a fixed point — including §17.5's automatic invalidation cascade and §17.3's human (non-process) worker tier. Still no cancellation flow and no crash-recovery hardening (M10).

Three findings from M7/M8 shape this plan. First, the §17 machinery is further along than the capability map suggests: M7 Phase 1 shipped the full event vocabulary (`WorkflowPaused`, `ExternalDecisionRecorded`, `WorkflowResumed`, `DecisionType`), M7 Phase 4's projector already projects `Paused` and clears it on `WorkflowResumed`, M7 Phase 3's validator already enforces §17.1's `SupersedeTargets` rules, and the resolver already skips `Paused` steps — so M9's work concentrates in the mutation surface and scheduling consequences, not the domain model. Second, §17.5 mandates that the cascade is "a direct consequence of §11.3's amended Dependency Resolution Rule — no separate cascade mechanism, event type, or scheduling rule", and condition 2 has been implemented and consulted on every round since M7 Phase 5 (#11) with its staleness coverage explicitly deferred to M9 — so supersession needs only a decision-minted execution to trigger it, plus the deferred tests. Third, a paused step is never `Succeeded`, so pausing already blocks downstream through condition 1 — the Pause Engine only has to append the event at the right moment.

The phase boundaries follow the spec's own seams: §17.1 (Flow's derived obligation to pause) vs. §17.2 (an external party's mutation); within §17.2, the decision types that let existing scheduling proceed (`Resume`/`Reject`) vs. the two that mint new `ExecutionRequest`s (`RetryWithRevision`/`Supersede`); and §17.3's non-process worker tier last, because its second job — producing the immutable artifacts `SupplementaryExecutionId` names — completes the decision machinery rather than preceding it.

### Phase dependency table

| Phase | Requires output from |
|---|---|
| 1 — Pause Engine | — (consumes M7's `Paused` projection and M8's settled-round facts) |
| 2 — External Decision Handler (record, validate, Resume/Reject) | 1 (a paused workflow must exist to decide on) |
| 3 — RetryWithRevision + Supersede + invalidation cascade | 2 (the recording/validation surface) |
| 4 — Human worker support | 3 (supplementary human executions feed Phase 3's decisions) |
| 5 — Pause/decision/supersede/human end-to-end integration tests | 4 |

### Phase 1 — Pause Engine (#57)
Capability 13. Append `WorkflowPaused(ExecutionId, StepId)` when a `PausePoint` step's round settles, as a **derived obligation** evaluated from projected state at the top of each scheduling round — not welded into the dispatch continuation — so a crash between the outcome event and the pause event re-derives the same obligation on the next mutation call (§7, §13). `StepState` gains the fact the rule needs (pause-recorded-for-latest-execution, distinct from currently-`Paused`); `FlowState` gains a derived workflow-level status (§12) so callers can distinguish "finished" from "paused". The pump's fixed point already covers pausing: a paused step is neither ready nor in flight.

**Produces:** a mid-DAG `PausePoint` runs to `WorkflowPaused` and returns with Flow idle, downstream never dispatched; re-invoking the pump on a paused log appends nothing. Projector fixture cases plus mutation-level stub-dispatcher tests.
**Excludes:** decisions (Phases 2–3), human workers (Phase 4), pause-on-`ExecutionCancelled` end-to-end (rule covered, but nothing can exercise it until M10's cancellation flow).
**Open question resolved in this phase:** pause vs. retry when a `PausePoint` step fails. Automatic retry per §10 runs *first*; `WorkflowPaused` follows only the settled outcome (`ExecutionSucceeded`, `ExecutionFailed` with `MayRetry` false, or `ExecutionCancelled`). Pausing on every retryable failure would make `RetryPolicy` unreachable on any pause-point step, and §17.2 frames `RetryWithRevision` as applying to a step that "has not yet succeeded" — exactly the post-exhaustion state.

### Phase 2 — External Decision Handler: record, validate, Resume/Reject (#58)
Capability 14, first half. `MutationInterface.RecordDecisionAsync(...)`: same §15 guard, validate against projected state + snapshot, append `ExternalDecisionRecorded` then `WorkflowResumed` (fsync'd), then run the same pump to its fixed point. All four `DecisionType`s are *validated* here (§17.2's closed-set rules: currently-paused referent; `TargetStepId` only with `Supersede` and within the declared `SupersedeTargets`; `SupplementaryExecutionId` mandatory for `Supersede` and naming a successful execution; `RetryWithRevision` only pre-success, `Supersede` only post-success) — invalid decisions throw a typed `AerFlowException` subclass and append nothing, "not silently widened". `Resume` and `Reject` land fully: `Resume` needs zero new scheduling code; `Reject` projects the step as terminally failed with retry foreclosed regardless of remaining budget — equivalent to exhausting `RetryPolicy`, but externally triggered, and applicable to a *successful* paused outcome too (the approval-gate "no").

**Produces:** the approval-gate flow end-to-end through the unchanged pump. Validation-matrix unit tests; mutation-level Resume/Reject tests.
**Excludes:** `RetryWithRevision`/`Supersede` consequences (Phase 3), supplementary executions (Phase 4).
**Open question resolved in this phase:** one *resolving* decision per pause. §17's "zero or more decisions" window is occupied by supplementary executions (§17.3), which are not decisions; each recorded decision immediately resolves its pause, and a further decision naming the same execution is invalid. A step that pauses again does so under a new `ExecutionId`.

### Phase 3 — RetryWithRevision + Supersede + the invalidation cascade (#59)
Capabilities 14 (rest) and 15. Decision consequences are **projected facts, not handler state**: replay must let any pump re-derive an unfulfilled `RetryWithRevision`/`Supersede` (decision recorded, no newer `ExecutionRequestAccepted` for the affected step), so a crash between recording and dispatching loses nothing (§7, §13). `RetryWithRevision` reopens the referenced step's retry round (`MayRetry` true again — consistent with M8 Phase 1's budget-per-round decision), then flows through ordinary readiness. `Supersede`'s target — already `Succeeded`, therefore never "ready" via §11.3 — gets its new `ExecutionRequest` as the decision's direct consequence. A present `SupplementaryExecutionId`'s output directory joins the new attempt's inputs (§17.5). The cascade itself is zero new mechanism: the rerun's success makes each dependent stale through condition 2, one `StepId` at a time, halting at any downstream `PausePoint` — M7 Phase 5's deferred staleness coverage comes due here.

**Produces:** §17.5's architect–critic example reproducible from the log (A1, B1, pause, decision, A2, B2 recording `UpstreamExecutionIds: {A: A2}`), with A1's artifacts untouched (§10, §16). Mutation-level tests including the crash window.
**Excludes:** human-produced supplementary artifacts (Phase 4 — this phase uses existing step outputs, exactly like §17.5's own example), real-process tests (Phase 5).
**Open questions resolved in this phase:**
- How the supplementary artifact reaches the worker: a dedicated environment variable alongside M7's `AER_INPUT_<n>` convention (e.g. `AER_SUPPLEMENTARY_INPUT`), so it can never collide with a declared input name.
- Recorded consciously, not "fixed": a dependent of the *pausing* step that becomes eligible at resume may dispatch against the pre-supersede result; it goes stale and reruns through the same cascade once the superseding rerun lands. Preventing that would need a holding mechanism §17.5 explicitly declines to introduce.

### Phase 4 — Human worker support (#60)
Capability 16 (§17.3): a human is a worker tier whose "execution" is an external event — same `ExecutionRequest`, same artifact model ("exactly one artifact model in this system", §16), no Core process. A non-process `WorkerBinding` variant appends `ExecutionRequestAccepted` and pre-allocates the output directory but spawns nothing; the pump returns with the step awaiting external completion (no daemon, §20). At the top of every mutation call's round, an unfinalized non-process execution whose output satisfies the full contract — existence *and* §4.1 output conditions — gets `ExecutionSucceeded`, exactly as §8 defines. A mutation-surface operation mints step-less supplementary human executions during a pause, whose completed `ExecutionId`s serve as `SupplementaryExecutionId` — making "which version of the artifact does this decision apply to" unambiguous by construction (§17.3).

**Produces:** process → human → process DAGs across separate mutation calls; the supplementary-revision flow feeding Phase 3's decisions. Mutation-level tests where the test drops the files (the test *is* the human).
**Excludes:** real-process tests (Phase 5); any notification/inbox surface for the human (UI spec's concern); watching or polling (§20).
**Open questions resolved in this phase:**
- An unsatisfied contract means *still pending*, never `Failed` — there is no exit signal to classify against, and §16's immutability attaches only once completion is detected.
- How a step-less supplementary execution avoids perturbing any step's latest-attempt projection — leaning: `ExecutionRequest.StepId` becomes optional; the projector tracks execution-level success for step-less requests and ignores them for step state.

### Phase 5 — Pause/decision/supersede/human end-to-end integration tests (#61)
The M9 completion gate, playing #14's and #48's role: real processes, real filesystem, both CI platforms, the test acting as the human throughout. The approval gate (`Resume`); `Reject` on a successful outcome; exhaustion → pause → supplementary human revision → `RetryWithRevision` → success (the §10 ↔ §17.2 seam observed against real processes); the full §17.5 architect–critic loop (`Supersede` with the critic's own feedback artifact, the automatic rerun cascade, the re-pause, the final `Resume`); a human step mid-DAG; the invalid-decision matrix appending nothing. Artifact directories for all attempts — superseded, exhausted, successful — remain untouched afterward (§10, §16).

**Produces:** M9 complete. CI green on Windows and Linux.

---

## Current Milestone

**M9: External Decisions** — phase plan above. Progress:

- ✅ Phase 1 — Pause Engine (#57)
- ✅ Phase 2 — External Decision Handler: record, validate, Resume/Reject (#58)
- ✅ Phase 3 — RetryWithRevision + Supersede + the invalidation cascade (#59)
- ✅ Phase 4 — Human worker support (#60)
- ⬜ Phase 5 — Pause/decision/supersede/human end-to-end integration tests (#61)

## Completed Milestones

Completed milestones keep only their phase checklist and any decisions of record later work
still leans on. The full phase plans — goals, boundaries, and the open questions each phase
resolved — live in this file's git history and in the linked issues.

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
- **Mutation Interface shape** — deliberately unspecified (§14); CLI is the reference implementation. Shape emerges from M7 implementation.

## Notes for future work

- **Worker adapter implementation (`Aer.Adapters`, no milestone yet)** — closed issue [#21](https://github.com/aer-works/aer-flow/issues/21) spiked a raw Claude→agy handoff and recorded facts that must inform whatever phase eventually builds the real Claude/Gemini adapters: each vendor needs a different scoped permission flag (no shared vocabulary), agy does not honor the invoking process's cwd and requires `--add-dir` plus absolute paths interpolated into the prompt text, and Claude needs stdin explicitly redirected to avoid a per-call stall. Read #21's findings before starting that work.
