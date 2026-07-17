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
| 24 | **Dialogue Worker** | The first Case 2 encapsulated multi-model worker: a bounded, multi-turn Claude ↔ Gemini exchange inside one `ExecutionRequest`, recorded as a durable transcript artifact; vendor CLIs invoked inside the worker boundary, subscriptions-only like the adapters | Flow spec §18.2; UI spec §10 |
| 25 | **Conversation View** | Render a dialogue execution's durable transcript as a conversation-style projection | UI spec §10 |

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
| **M17: Dialogue Worker** | 24 (first Case 2 worker); plus the real-use walkthrough doc | M12 (both vendor CLIs proven live) |
| **M18: Conversation View** | 25 | M17 (a transcript to project); M14 |
| **M19: UI Design Pass** | — (visual/UX quality across the finished surface; no new capability) | M18 |

M7–M10 complete the **v1.0 engine** (the behavioral spec is authoritative for it, and every §5.1 flow event now has a producer). M11 onward turns that engine into a runnable product: the worker adapters and the CLI pump the specs assume (§21, CLAUDE.md rule #2) but no engine milestone built, then distribution and — separately — the v0.7 UI.

M14–M16 are that UI track, splitting the roadmap's original single "UI" row the same way the engine split into M7–M10: **projection first** (capability 21 — every other UI capability renders on top of the read model), then the **control surface** (22) and **authoring** (23) as independent tracks behind it — M15 and M16 don't depend on each other, only on M14. Conversation-style views and live Observation-Tier turn streaming (UI spec §10) were deliberately assigned to *no* milestone throughout that track: they depend on Case 2 encapsulated multi-model workers (Flow spec §18.2) that didn't exist yet, and Overview §6's rule is to build the concrete thing before generalizing for it.

M17–M19 are the post-UI-track sequence, planned at M16's completion by re-checking the original project goal against what had shipped. Half of that goal exists and is proven live: vendor-to-vendor task hand-off on subscriptions (M12's recorded mixed-vendor gate). The other half — letting the two models actually talk to each other — does not: today §17.5's supersede loop makes the *human* the relay for every round of the exchange. **M17** builds the first Case 2 worker (the dialogue worker — the concrete thing the conversation view has been waiting on), opening with the real-use walkthrough the project is also missing. **M18** renders M17's durable transcript as UI spec §10's conversation view — load-on-refresh first; live Observation-Tier turn streaming stays unassigned until a concrete need names it. **M19** is the deliberate visual/UX design pass over the whole UI, sequenced last so it styles the UI's final shape — conversation view included — rather than a layout M18 is about to disrupt. M19 gets its phase plan when it becomes current (this document plans the current milestone only); M18's is below.

---

## M18: Conversation View — Phase Plan

**Goal:** render a dialogue execution's durable `transcript.jsonl` as UI spec §10's conversation
view (capability #25): a per-execution, conversation-style projection of the exchange M17
records, loaded on refresh like every other projection surface. M17 made the two models talk
inside one execution; M18 makes the exchange readable without opening a JSONL file. Deliberately
excluded and still assigned to no milestone: live Observation-Tier turn streaming (waits on a
concrete need to name it), and any action that pauses or steers the worker mid-exchange — UI
spec §10 forbids offering it because Flow spec §17.4 places the capability outside Flow's
contract; it does not exist to offer.

Four facts shape the plan. First, **Flow, Core, and the worker change by zero lines.** The
transcript already exists on disk under the execution's artifact directory (M17 Phases 2–3), and
artifact directories are already part of the UI's read model (UI spec §3); this milestone is
`Aer.Ui` only. Second, **the spec gap is settled at planning, not in a phase** — the ledger
entry's owed answer, resolved by this planning PR's UI spec §10 amendment (§10.1): discovery is
by durable artifact presence (`transcript.jsonl` in the execution's artifact directory — §3.1's
self-describing rule applied one level down; no registry, no binding inspection, no
special-casing which worker wrote it), and the UI spec names the *reader's* contract (sequence,
role, vendor, prompt, text) while the producing schema stays worker-owned per Flow spec §18.2 —
the UI consumes the spec's contract and never references `Aer.Workers.Dialogue`'s types. Third,
**determinism and transparency bind the view**: §11 (identical durable inputs → identical visual
state) and §12 (every element traceable to durable data) make the conversation view a pure
function of the artifact directory — and partial transcripts are honest data, not errors: a
failed exchange leaves its completed turns on disk as a forensic record (M17 Phase 3's
deliberate design), and the view renders exactly what is durably present, never the invented
remainder. Fourth, **the seam precedent is already drawn**: presentation-layer projections the
engine's read model deliberately omits live in `Aer.Ui` (`ExecutionHistory`, M14 Phase 1's
`TaskProjection` boundary, UI spec §2) — the transcript reader is that same shape, and no gate in
this milestone touches a vendor CLI: stub-produced artifacts exercise the identical read path a
live exchange would, so M18 owes no runbook and no live gate.

### Phase dependency table

| Phase | Requires output from |
|---|---|
| 1 — Transcript read seam | — |
| 2 — The conversation view | 1 |
| 3 — Gate: conversation round trip in default CI | 2 |

Linear, unlike M14–M17's fan-out: the view renders what the seam projects, and the gate drives
the finished surface. A three-phase milestone is the honest size — the heavy lifting (the
transcript itself, the dispatch machinery, the UI's projection infrastructure) all shipped in
M14 and M17.

### Phase 1 — Transcript read seam (#177)
A tolerant `transcript.jsonl` reader plus discovery, per amended §10.1: given an execution's
artifact directory, report whether a transcript exists (by artifact presence alone) and project
its turns into a read-model type the view renders from. Owned by `Aer.Ui` per the
`ExecutionHistory` precedent; references no `Aer.Workers.Dialogue` type — the contract lives in
the spec, not in a shared assembly.

**Produces:** a transcript projection type + loader, unit-tested against complete, partial (a
failed exchange's forensic prefix), torn-final-line (crash mid-append), and absent transcripts.
**Excludes:** any rendering (Phase 2).
**Open questions resolved in this phase:**
- **How a malformed line projects** — skipped silently vs. surfaced as an explicit marker the
  view can render. Must stay deterministic (§11) and must never invent state (§12);
  `DecisionRecord.Resolved`'s render-the-crash-window-honestly precedent leans toward the
  explicit marker.

### Phase 2 — The conversation view (#178)
The rendering surface: for a selected execution that has a transcript, an ordered
conversation-style view of its turns — each labeled with its role and vendor, its text as
recorded (the worker already strips stop sentinels; the transcript carries the participant's
actual words). Load-on-refresh like every other projection surface. Each turn's full `Prompt` is
durable data and stays traceable (§12), but it embeds the entire prior transcript (M17 Phase 3's
full-transcript threading), so the default rendering shows `Text` and reveals `Prompt` per turn
only on demand. Retried and superseded attempts each have their own artifact directory and
therefore their own conversation — the view is strictly per-execution.

**Produces:** the conversation view, reachable from the execution surfaces the UI already shows,
rendering complete and partial transcripts.
**Excludes:** any new action or mutation (the view is read-only; §10 forbids mid-exchange
control); any streaming.
**Open questions resolved in this phase:**
- **Where the view lives in the window** — a top-level view alongside DAG/timeline/lineage vs. a
  per-execution detail panel anchored to the execution-history surface; settled by which anchor
  the existing surfaces make natural, not by new navigation chrome ahead of M19's design pass.

### Phase 3 — Gate: conversation round trip in default CI (#179)
The milestone's gate, unattended on all three OSes: bind and run a dialogue step to terminal
over stub vendor CLIs (the machinery `DialogueDispatchEndToEndTests` proved in M17 Phase 4),
then load the conversation projection from the resulting artifacts and assert it — turns in
order, roles and vendors correct, text matching what the stub exchange produced — plus a
partial-transcript assertion (a failed exchange's forensic prefix renders, per Phase 1's
contract). M14's golden-projection gate stays green untouched: nothing in this milestone changes
projection semantics.

**Produces:** M18 complete — the recorded exchange is readable in the UI, proven end to end in
default CI.
**Excludes:** the visual/UX quality pass (M19 styles the finished surface); any live gate or
runbook (nothing here touches a vendor CLI).

### Phase 1 — Real-workflow walkthrough (§18.1 baseline) (#164)
The missing "how do I actually use this" document: a `docs/walkthroughs/` guide driving one real
task (not the smoke fixture) end to end through the machinery M11–M16 shipped — author the
template and bindings (in the UI or by hand), run it (the UI's Run action or `aer run`), watch
the DAG, hit the `PausePoint`, send critique back via Send-back / `aer supply` + `aer decide`,
and land at terminal with both vendors' artifacts on disk. This is the human-relayed version of
the exchange M17 automates, so writing it down is also the milestone's requirements capture:
every manual step the walkthrough forces the reader through is a candidate for what the dialogue
worker absorbs.

**Produces:** the walkthrough doc; a reusable non-fixture example template + bindings pair.
**Excludes:** any product code; any dialogue-worker content (Phases 2+).
**Human action items:** actually performing the live run the walkthrough describes (CLAUDE.md's
live-vendor rule) — the doc itself, the example files, and a stub-CLI dry run of the same flow
are all buildable in an agent session.

### Phase 2 — Transcript contract + dialogue worker skeleton (#165)
The seam decisions everything else builds on: where the worker lives, and what it writes.
Defines the `transcript.jsonl` schema (one JSON object per turn — sequence, speaker role, vendor,
the prompt sent, the turn text produced; documented alongside the worker, tracked in the ledger
entry below) and the worker's own config surface (the two participants' vendor + model + per-side
preamble, turn budget, stop condition). The skeleton runs a fixed number of alternating turns
against stub vendor CLIs and writes a schema-valid transcript plus a declared final output.

**Produces:** a runnable dialogue executable (stub vendors only), the transcript schema, the
worker config format.
**Excludes:** real termination/failure semantics (Phase 3); Flow dispatch (Phase 4).
**Open questions resolved in this phase:**
- **Where the worker lives** — a new `Aer.Workers.Dialogue` leaf in `AerFlow.slnx` (Overview §7's
  default; testable like every other project) vs. a script under `scripts/`; and how it ships —
  riding `aer`'s existing `dotnet tool` package as a second command vs. its own package (M13's
  packing decisions are the precedent to extend, not reopen).
- **Transcript schema ownership** — documented with the worker for now; whether UI spec §10 names
  it is settled at M18 planning (see the ledger entry).

### Phase 3 — Turn loop, termination, and failure semantics (#166)
The real exchange: context threading (how much of the transcript each next CLI call carries —
full transcript vs. a window; spike #21's prompt-size and CLI-argument realities apply here,
*inside* the worker), stop conditions (turn budget exhausted; a side signals completion), and
failure mapping — a vendor CLI exiting nonzero or producing an empty turn mid-exchange ends the
execution as a failure (nonzero exit / missing declared output), so `ContractValidator` + §10
retry treat a broken dialogue exactly like any other failed worker. No partial-progress
resumption: §18.2's tradeoff, restated deliberately, not worked around.

**Produces:** a complete dialogue run against stub CLIs, every termination path tested.
**Excludes:** dispatch (Phase 4); live vendors (Phase 5).
**Open questions resolved in this phase:**
- **The stop-signal shape** — a sentinel in the turn text vs. a structured per-turn output file
  the worker reads (parsing is legitimate inside the boundary; the question is which is more
  robust across two different vendors' output habits).

### Phase 4 — Dispatch integration: the third adapter (#167)
A `DialogueWorkerAdapter` in `Aer.Adapters` (registry key naming the capability — e.g.
`"dialogue"` — the M12 "vendor name, not binary name" convention generalized) resolving a
`WorkerInvocation` to the dialogue executable. A workflow step bound to it runs via `aer run`
*and* the UI's Run action over stub vendor CLIs, end to end; M12's Windows token rule (never
pre-quote one string) applies to any shell wrapping this adapter does.

**Produces:** dialogue-as-a-step, runnable from CLI and UI, with `PausePoint`/retry/cancel
applying to it like any worker.
**Excludes:** live vendors (Phase 5); any UI rendering beyond what M14–M15 already show for any
execution.
**Open questions resolved in this phase:**
- **How the worker's dialogue config reaches it** — via `WorkerInvocation`'s existing per-role
  fields (prompt template, model, permission scope are per-role config already) vs. a config file
  path the binding's contract names as a required input.

### Phase 5 — Gates: stub round trip in default CI + live dialogue runbook (#168)
The milestone's two gates, placed exactly like M11–M16 placed theirs: (a) an unattended
stub-vendor dialogue round trip in default CI on all three OSes — bind and run a dialogue step to
terminal, transcript schema-asserted; (b) `pixi run smoke-dialogue` +
`docs/runbooks/live-dialogue-smoke.md` — a real, bounded Claude ↔ `agy` exchange, living in
`Aer.Cli.SmokeTests` outside `AerFlow.slnx` like every live gate, **permanently a human action
item** (CLAUDE.md's live-vendor rule). M14's golden-projection gate must stay green untouched:
nothing in this milestone changes projection semantics.

**Produces:** M17 complete — the two models can talk inside one execution, provable on stubs in
CI, proven live by a recorded human run.
**Excludes:** the conversation view (M18 renders what this milestone records).

---

## Current Milestone

**M18: Conversation View** — phase plan above. Progress:

- ✅ Phase 1 — Transcript read seam (#177)
- ⬜ Phase 2 — The conversation view (#178)
- ⬜ Phase 3 — Gate: conversation round trip in default CI (#179)

Per this document's session prompt: help implement the current phase only.

Decisions of record from M18 (move to `docs/decisions-of-record.md` at completion):

- **A malformed transcript line projects as an explicit `TranscriptLine.Malformed` marker in
  place, never a silent skip and never a whole-projection failure** — resolving Phase 1's named
  open question in favor of the marker, per `DecisionRecord.Resolved`'s
  render-the-damage-honestly precedent. The marker carries only the 1-based line number: the raw
  bytes stay on disk for §12 traceability, and the view has nothing else it could honestly
  render. Blank/whitespace-only lines are skipped outright (they carry nothing to mark), and
  extra JSON fields beyond §10.1's five never malform a turn — the contract says "at least"
  (Phase 1).
- **File order is projection order** — `TranscriptProjection.Lines` preserves the file exactly;
  `Sequence` is projected data, never a sort key. §10.1 names file order as the order the turns
  happened, so reordering would let a buggy producer's claim override the durable record
  (Phase 1).
- **`TranscriptProjectionLoader` opens with `FileShare.ReadWrite`** — the producing worker holds
  the file open (`FileMode.Append` + `FileShare.Read`) for the whole exchange, so a
  load-on-refresh against a still-running execution must read what is durably there so far
  rather than fail on a sharing violation; whole lines are the writer's flush unit, so the only
  mid-write shape a reader can observe is a torn final line, which projects as `Malformed`
  (Phase 1).

## Completed Milestones

Completed milestones keep only a one-paragraph summary here. Their phase checklists live in the
closed GitHub milestones; their decisions of record — the constraints and precedents later work
still leans on — in `docs/decisions-of-record.md`; and the full phase plans — goals, boundaries,
and the open questions each phase resolved — in this file's git history and the linked issues.

**M17: Dialogue Worker** — the first Case 2 encapsulated multi-model worker (Flow spec §18.2):
`Aer.Workers.Dialogue`, a single executable running a bounded, multi-turn Claude ↔ Gemini (`agy`)
exchange — each model's turn threaded into the other's next prompt — writing a durable
`transcript.jsonl` plus its declared output, dispatched by Flow like any other worker through a
third adapter registry entry (`"dialogue"`), runnable from CLI and UI, with the stub-vendor
round trip proven in default CI on all three OSes and the live exchange gated by `pixi run
smoke-dialogue` (permanently a human action item, not yet recorded). Opened with the real-use
walkthrough the project had been missing (`docs/walkthroughs/first-real-workflow.md`).

**M16: UI Authoring** — the last milestone of the original UI track: template and worker-bindings
authoring in `Aer.Ui` — create/edit steps, dependencies, retry policies, metadata,
`PausePoint`s/`SupersedeTargets`, and bindings entries, with live structural validation through
Flow's own `WorkflowDefinitionValidator`, the stack's first template and bindings writers held to
round-trip fidelity through the engine's own parsers/validators, and full authoring round trips
(author from blank → save → run to terminal; edit a bound task's template → the diff view shows
the divergence while the bound rendering stays byte-identical) proven in default CI on all three
OSes.

**M15: UI Control Surface** — the second UI-track milestone: every §7 user action — start/resume
a workflow, Approve/Reject, Retry-with-revision, Send-back, and Cancel (targeted and host stop) —
exposed in `Aer.Ui` exclusively through Flow's mutation interface, via in-process reuse of the CLI
command layer, mapped onto Flow's closed `DecisionType` set, and proven by UI-driven round trips
over shell-stub workers on all three CI OSes in default CI.

**M14: UI Projection** — the first UI-track milestone: `Aer.Ui`, an Avalonia desktop app
consuming `Aer.Flow`'s read model in-process — task/execution/decision projection with live
polling, the DAG view, artifact lineage, the snapshot-vs-template diff, and a golden-projection
determinism gate in default CI. Read-only throughout: no mutations (M15), no authoring (M16).

**M13: Distribution** — turned `aer` from a checkout-only build into an installable
`dotnet tool`: single-platform packing, version wiring from `release-please`, multi-RID
native-lib bundling, and an unattended CI round-trip check proving install → run → uninstall
works with no live vendor auth (`pixi run verify-pack`, `scripts/verify-pack-roundtrip.sh`).

**M12: Full Control Surface** — the milestone that made the runnable library drivable: a second
vendor (Gemini's `agy`) behind M11's unchanged protocol, and the mutation surface M9/M10 built
exposed as `aer decide`/`aer cancel`, proven by a live mixed-vendor paused run decided from the
terminal (`docs/runbooks/live-mixed-vendor-smoke.md`).

**M11: First Real Run** — the milestone that made the library runnable: the canonical
worker-invocation protocol and adapter seam, the Claude adapter, the `aer run` pump, and a
recorded green live two-step run (`docs/runbooks/live-claude-smoke.md`).

**M10: Cancellation & Edge Cases** — on-demand cancellation through the single mutation surface (intent recorded first), and crash-recovery made whole by reading back the Core half of the log.

**M9: External Decisions** — pause points, the four external decisions, the automatic invalidation cascade, human workers.

**M8: Reactive Scheduler** — fan-out/fan-in DAG with retries and concurrent dispatch.

**M7: Foundation** — linear A → B → C end-to-end, happy path only.

---

## Open Questions (spec-level)

These are gaps in `aer-flow-behavioral-spec-v1.0.md` discovered during planning. Each should be resolved via a spec PR before the phase that first encounters it.

- ~~**`WorkflowTransition` event**~~ — resolved (#15): the event was removed from the spec; workflow-level status is a pure projection of step-level and pause/resume events (§5.2, §12).
- **Event Store performance** — full re-read vs. manifest-checkpoint-plus-tail (§21). Deferred until §20's no-daemon question is revisited.
- **Mutation Interface shape** — deliberately unspecified (§14); CLI is the reference implementation. Shape emerges from M7 implementation; the CLI surface itself lands in M11 (`aer run`) and M12 (`aer decide`/`aer cancel`).
- ~~**Orphaned mid-run executions**~~ — resolved (#77): §7 now defines the third crash state (`ExecutionStarted`, no `ExecutionExited`) — finalize as abandoned, a Flow-originated `ExecutionFailed`/`Retryable`, after a best-effort re-issued cancellation toward Core. Unblocks M10 Phase 3 (#71).
- ~~**Task-directory discovery (UI spec §3)**~~ — resolved (#126, UI spec v0.8 §3.1): a task directory is self-describing — identified by its durable contents (bound snapshot + event store), never by membership in any registry or list; any UI-side list of known task directories is Local UI Configuration (§4), a rebuildable convenience that is never authoritative; and no component of the trusted execution stack may be required to announce, register, or enumerate tasks. Unblocks M14 Phase 2 (#119).
- ~~**UI spec maturity (v0.9 vs. the flow spec's v1.0)**~~ — resolved at M16 completion, during M17 planning: promoted to v1.0 (`spec/aer-flow-ui-behavioral-spec-v1.0.md`), no behavioral changes, on the same terms the Flow spec reached v1.0 — projection, control surface, and authoring cover everything the spec names for those surfaces, and no known gap blocks a current capability. Post-1.0 gaps keep resolving by amendment (the Flow spec's own §11.1 was amended post-1.0 during M16 planning); this list stays the ledger.
- ~~**The transcript artifact contract (UI spec §10 vs. the worker boundary)**~~ — resolved, during M18 planning (#176, before Phase 1/#177, which would otherwise have had to decide it mid-implementation): UI spec §10.1 now names the **reader's contract** — an execution offers a conversation projection iff its artifact directory contains `transcript.jsonl` (discovery by durable content alone, §3.1's self-describing rule applied one level down), each line one JSON object with at least sequence/role/vendor/prompt/text — while the *producing* schema stays worker-owned per Flow spec §18.2 (the first producer is `Aer.Workers.Dialogue`'s `TranscriptTurn`, whose fields the contract mirrors; the UI consumes the spec's contract, never the worker's types). Partial transcripts are honest data, not errors. Unblocks M18 Phase 1 (#177).
- ~~**Template version-increment ownership (Flow spec §11.1)**~~ — resolved, during M16 planning (before Phase 1/#150, which would otherwise have had to decide it mid-implementation): `WorkflowTemplateVersion` is ordinary template data Flow only copies into the snapshot at instantiation, never computes or enforces; incrementing it is the editor's (or a hand-editor's) responsibility, on every content-changing save, never on a no-op save, never at finer granularity than a save.
- ~~**The worker-bindings file vs. the UI write model (UI spec §4 vs. §9)**~~ — resolved, during M16 planning (before Phase 4/#153): §4's write list now names worker-bindings configuration files directly (UI spec v0.9), closing the gap against §9's pre-existing "edit worker bindings" grant.

## Notes for future work

- **A third worker adapter (`Aer.Adapters`)** — Claude shipped in M11 Phase 2 (#85), Gemini/`agy` in M12 Phase 1 (#95). Before adding another vendor, read closed spike [#21](https://github.com/aer-works/aer-flow/issues/21)'s recorded findings — stdin stalls, permission-flag vocabularies, and path-interpolation behavior differ per CLI and are exactly what the adapter seam exists to absorb. (M17 Phase 4's dialogue adapter is not this note's case: it spawns aer's own dialogue worker executable, not another vendor's CLI — the vendor quirks stay inside the worker, which inherits them from the two existing adapters' recorded findings.)
- **Whether MVVM spreads beyond the decision surface** — M15 Phase 2 (#138) deliberately scoped `CommunityToolkit.Mvvm` to the paused-step Approve/Reject buttons, the first *interactive, stateful* control surface (enabled state tied jointly to projected state and an in-flight mutation). The DAG/history/lineage/diff rendering stayed code-behind on purpose: it's one-directional (projection → controls, nothing to bind against), so a ViewModel there would be ceremony with no payoff. Phase 3 (Retry-with-revision, Send-back) and Phase 4 (Cancel) add more of the same interactive shape, so expect the ViewModel layer to grow phase over phase rather than needing a deliberate decision to introduce it again. Revisit whether the read-only surfaces are worth converting too only if M16 (Authoring) needs two-way binding there — not preemptively.
