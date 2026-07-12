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

## M10: Cancellation & Edge Cases — Phase Plan

**Goal:** Cancel a live execution on demand through the single mutation surface (§9, §14), intent recorded first (§7), and make crash recovery whole: a mutation call invoked against a log that ends mid-flight — an unfinalized execution, an unfulfilled cancellation intent — re-derives and completes every outstanding obligation (§6, §7, §13). This is the roadmap's final milestone: after M10, every §5.1 flow event has a producer and every crash window named in the spec has a recovery test. Worker adapters and any UI surface remain separate tracks (Notes for future work; UI spec).

Three findings from M7–M9 shape this plan. First, cancellation's read side is already complete: M7 shipped both events (`CancellationRequested`, `ExecutionCancelled`), the Outcome Classifier maps `CoreExitReason.CancelRequested` to a cancelled verdict (never a failure, §8), M8's Retry Engine never retries a cancellation (§10), the resolver treats `Cancelled` as terminal, and M9's Pause Engine counts it as a settled outcome — but nothing ever *produces* `CancellationRequested`, and the one path that cancels today (the pump host's own `CancellationToken` flowing straight into Core dispatch) does so with no recorded intent — §9's step 1, inverted. M10's cancellation work is therefore the request/delivery side only. Second, §7's durability primitives all exist — every lifecycle append fsyncs before dispatch, the reader drops a torn trailing line and throws on a malformed complete one, the kernel-held guard evaporates with a crashed holder, and M9 deliberately shaped pause/decision/non-process consequences as derived obligations any later pump re-derives — but reconciliation does not: an unfinalized process execution projects `Running` forever and the workflow stalls, because "genuinely still running" and "its pump died" are indistinguishable to the projector today. Third, the fact that distinguishes them is already on disk and unread: Core-originated `ExecutionStarted`/`ExecutionExited` lines have been recorded in the single log since M7 Phase 6, but `FlowEventLogReader` skips `CoreLogEntry` lines — §6's causal link is recorded but never consulted. Reading back what Flow already writes is the substantive new mechanism of this milestone.

The phase boundaries follow the spec's own seams: within §9, recording an intent (a mutation, §14 — including the targets that need no Core at all) vs. delivering it to a live process (Core's kill sequence, under §7's intent-first ordering); then §7's recovery obligations, which need both; and the end-to-end completion gate last, as in every milestone before.

### Phase dependency table

| Phase | Requires output from |
|---|---|
| 1 — Cancellation mutation surface (record, validate, non-process targets) | — (consumes M7's classifier/event vocabulary and M9's derived-obligation pump) |
| 2 — Live cancellation delivery (in-flight Core executions) | 1 (the recording surface and its ordering rule) |
| 3 — Crash-recovery reconciliation (reading back the Core log) | 2 (re-issuing an unfulfilled intent is delivery machinery) |
| 4 — Cancellation + crash-recovery end-to-end integration tests | 3 |

### Phase 1 — Cancellation mutation surface: record, validate, non-process targets (#69)
§9 steps 1 and 4. `MutationInterface.RequestCancellationAsync(...)`, the fourth public mutation operation: same §15 guard, validate against projected state, append `CancellationRequested(ExecutionId)` fsync'd (§7's intent-first discipline), then run the same pump to its fixed point. An `ExecutionId` unknown to the log throws a typed `AerFlowException` subclass and appends nothing (M9 Phase 2's "not silently widened" discipline); a known but already-terminal target is *not* an error. Targets with no Core process finalize entirely in this phase: a pending non-process execution (human step or step-less supplementary, §17.3) has nothing to forward, so the same round's derived obligation appends `ExecutionCancelled` directly. Pause-on-cancelled — the rule shipped in M9 Phase 1 with nothing to exercise it — gets its first exercise via a cancelled non-process `PausePoint` step.

**Produces:** cancel-a-pending-human-step end-to-end at mutation level (intent, then `ExecutionCancelled`; downstream never dispatched via §11.3 condition 1; no retry despite budget, §10); the too-late matrix appending exactly one line and changing nothing. Validation-matrix unit tests, projector fixture cases, mutation-level stub tests.
**Excludes:** live Core delivery (Phase 2), crashed executions (Phase 3), real-process tests (Phase 4).
**Open questions resolved in this phase:**
- What §9's "records that the request arrived too late" means under §5.1's closed vocabulary: the `CancellationRequested` append itself is the record; too-late-ness is a derived fact of projection, never stored, and the recorded outcome is never altered.
- Whether Flow may append `ExecutionCancelled` with no Core exit to classify: for the non-process tier, yes — §8's table classifies *Core-reported* outcomes, and §17.3 already made Flow that tier's completion authority (M9 Phase 4); §9's steps 2–3 are vacuous with no process. A cancelled supplementary execution is thereby also permanently ineligible as a `SupplementaryExecutionId` (§17.2 requires a *successful* one — validated since M9 Phase 2).

### Phase 2 — Live cancellation delivery: in-flight Core executions (#70)
§9 steps 1–3 for a running process. Delivery is the new machinery, classification is not: `CoreDispatcher` already surfaces `AerCancelException` as `CoreExitReason.CancelRequested`, and the existing outcome path already appends `ExecutionCancelled`. The pump gains an in-flight registry — `ExecutionId` → per-dispatch cancellation source — so one live execution can be signalled without touching its siblings (today the only token reaching `AerTask.RunAsync` is the pump's own). Ordering is the substance: the intent is appended and fsync'd *before* the per-dispatch signal, and the passive path that lets the host's token reach Core unrecorded is closed — a host-initiated stop mints `CancellationRequested` for every in-flight id, fsyncs, forwards, then awaits and classifies each exit before returning with the log consistent. A cancelled execution is one execution: siblings run to their own outcomes (cancelled is not failed, §8), downstream stays blocked through condition 1, and a cancelled `PausePoint` step pauses (settled round).

**Produces:** cancel one of two in-flight executions with the sibling unaffected; a host stop cancelling everything in flight. Stub-dispatcher (`TaskCompletionSource`) tests asserting the intent line hits the log before the signal arrives.
**Excludes:** crash windows around cancellation (Phase 3); real processes (Phase 4); Ctrl+C wiring in a real CLI host (`Aer.Cli` is still a stub — this phase builds the host-token surface the CLI will eventually wire to).
**Open questions resolved in this phase:**
- How an on-demand cancel reaches a live run at all, given the §15 guard is held by the pump for the whole mutation call (M7 Phase 8) and §20 forbids a daemon: the pump's host process is the delivery point. §21 already answers "who invokes Flow" with "the CLI is the pump"; cancelling a live run means signalling that process (Ctrl+C / the host's token), which routes through the same single mutation surface in-process — §14's "exactly one surface" is a code path, not a process boundary, and no second channel or IPC is invented. A *separate* process's `RequestCancellationAsync` therefore acts between pump calls (exactly Phase 1's targets) or arrives too late.
- No workflow-level "stop" operation: §9 is per-`ExecutionId`; a host stop is simply an intent minted for every in-flight id.

### Phase 3 — Crash-recovery reconciliation: reading back the Core log (#71)
§7 full robustness. Surface the Core half of the log — `CoreLogEntry` lines, same file since M7 Phase 6's single-log decision — and join them to Flow intents by `ExecutionId`, never by file order or timestamp (§6). Reconciliation is the M9 derived-obligation pattern at the top of every mutation call, no dedicated "recovery mode" (§13): an unfinalized intent with **no `ExecutionStarted`** is §7's named safe state — re-submit it under its existing `ExecutionId` (no new accept event, no `RetryPolicy` charge: it is the same attempt, and the held guard plus the absent trace keep §15's one-execution-per-request intact); one with **`ExecutionExited` but no outcome** ran while Flow was down — classify now from the recorded exit and the contract on disk (§8, §6), as if the completion had just arrived; an **unfulfilled `CancellationRequested`** follows §9's crash clause — cancel wins for a never-started target (finalize `ExecutionCancelled`, never dispatch), classification proceeds for an exited one (the intent derives as too late), and a still-live target gets the request re-issued, safe because Core no-ops on a finished execution. The fourth state — **`ExecutionStarted` with no `ExecutionExited`**, the orphan whose pump died mid-run — is a spec gap resolved by spec PR before this phase (see Open Questions below).

**Produces:** mutation-level crash-window tests — logs cut after accept-fsync/before spawn, after spawn/before exit, after exit/before classification, after `CancellationRequested`/before forward, after outcome/before pause event (M9 Phase 1's window, now tested against restart) — each re-run converging to the identical fixed point with no duplicate events and no double execution (§13). Torn-trailing-line and guard-reacquire cases folded into the same suite.
**Excludes:** real-process/real-kill tests (Phase 4); re-attaching to a live orphaned process (§20, no daemon); the manifest-checkpoint read strategy (§21, still deferred — reading the Core half changes what is read, not the full-reread strategy M8 Phase 4 measured as cheap).
**Open questions resolved in this phase:**
- Recovery re-submission is the *same attempt*, not a retry: the intent is already durably recorded, so no new `ExecutionRequestAccepted` is appended and nothing is charged against `RetryPolicy`.
- The orphan rule (pending the spec PR) — leaning: the attempt is *abandoned*; nothing can re-attach (§20 no daemon; the binding is spawn-and-await) and §15 forbids a second execution for the same request, so it is finalized from recorded facts as a failed, retryable attempt, after a best-effort re-issued cancellation toward Core; §16's fresh-output-directory-per-attempt already guarantees a still-live orphan can never corrupt a successor attempt.

### Phase 4 — Cancellation + crash-recovery end-to-end integration tests (#72)
The M10 completion gate, playing #14's, #48's, and #61's role: real processes, real filesystem, both CI platforms. A genuinely long-running real worker cancelled mid-flight via the host token (`CancellationRequested` durably precedes the kill; the concurrent sibling completes; downstream never dispatched; no retry despite budget); cancel → pause → `RetryWithRevision` rerunning the cancelled pause-point step to success (§17.2 applies to any not-yet-succeeded outcome); a pump host — a small test-only host process, `Aer.Cli` being a stub — killed hard at each §7 window, leaving a real orphan for the started-no-exit case, then a plain re-invocation completing the workflow; the torn trailing line a killed writer leaves behind skipped on re-read; the guard reacquired immediately with no stale lock (§15); the too-late matrix; artifact directories for every attempt — cancelled, abandoned, re-submitted, successful — untouched afterward (§10, §16).

**Produces:** M10 complete — the roadmap's four milestones done, every capability-map row shipped and tested. CI green on Windows and Linux.

---

## Current Milestone

**M10: Cancellation & Edge Cases** — phase plan above. Progress:

- ✅ Phase 1 — Cancellation mutation surface: record, validate, non-process targets (#69)
- ⬜ Phase 2 — Live cancellation delivery: in-flight Core executions (#70)
- ⬜ Phase 3 — Crash-recovery reconciliation: reading back the Core log (#71)
- ⬜ Phase 4 — Cancellation + crash-recovery end-to-end integration tests (#72)

## Completed Milestones

Completed milestones keep only their phase checklist and any decisions of record later work
still leans on. The full phase plans — goals, boundaries, and the open questions each phase
resolved — live in this file's git history and in the linked issues.

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
- **Mutation Interface shape** — deliberately unspecified (§14); CLI is the reference implementation. Shape emerges from M7 implementation.
- **Orphaned mid-run executions** — §7 defines recovery when the crash precedes the spawn ("intent recorded, no execution trace") and §6 covers a process that finished unclassified, but a process that was *live* when its Flow host died (`ExecutionStarted` recorded, no `ExecutionExited`, guard free) has no defined resolution: nothing can re-attach (§20, no daemon), and §15 forbids a second Core execution for the same `ExecutionRequest`. Resolve via spec PR before M10 Phase 3 (#71); the leaning — finalize the attempt as abandoned (failed, retryable) after a best-effort re-issued cancellation — is recorded there.

## Notes for future work

- **Worker adapter implementation (`Aer.Adapters`, no milestone yet)** — closed issue [#21](https://github.com/aer-works/aer-flow/issues/21) spiked a raw Claude→agy handoff and recorded facts that must inform whatever phase eventually builds the real Claude/Gemini adapters: each vendor needs a different scoped permission flag (no shared vocabulary), agy does not honor the invoking process's cwd and requires `--add-dir` plus absolute paths interpolated into the prompt text, and Claude needs stdin explicitly redirected to avoid a per-call stall. Read #21's findings before starting that work.
