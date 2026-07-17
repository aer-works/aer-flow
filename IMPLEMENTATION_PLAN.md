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
| **M19: Product UX** | — product-UX overhaul: task-first IA + decision inbox, plain language, guided authoring (no hand-edited config files), then the visual design pass | M18 |

M7–M10 complete the **v1.0 engine** (the behavioral spec is authoritative for it, and every §5.1 flow event now has a producer). M11 onward turns that engine into a runnable product: the worker adapters and the CLI pump the specs assume (§21, CLAUDE.md rule #2) but no engine milestone built, then distribution and — separately — the v0.7 UI.

M14–M16 are that UI track, splitting the roadmap's original single "UI" row the same way the engine split into M7–M10: **projection first** (capability 21 — every other UI capability renders on top of the read model), then the **control surface** (22) and **authoring** (23) as independent tracks behind it — M15 and M16 don't depend on each other, only on M14. Conversation-style views and live Observation-Tier turn streaming (UI spec §10) were deliberately assigned to *no* milestone throughout that track: they depend on Case 2 encapsulated multi-model workers (Flow spec §18.2) that didn't exist yet, and Overview §6's rule is to build the concrete thing before generalizing for it.

M17–M19 are the post-UI-track sequence, planned at M16's completion by re-checking the original project goal against what had shipped. Half of that goal exists and is proven live: vendor-to-vendor task hand-off on subscriptions (M12's recorded mixed-vendor gate). The other half — letting the two models actually talk to each other — does not: today §17.5's supersede loop makes the *human* the relay for every round of the exchange. **M17** builds the first Case 2 worker (the dialogue worker — the concrete thing the conversation view has been waiting on), opening with the real-use walkthrough the project is also missing. **M18** renders M17's durable transcript as UI spec §10's conversation view — load-on-refresh first; live Observation-Tier turn streaming stays unassigned until a concrete need names it. **M19** was originally scoped as a visual/UX design pass alone ("no new capability"), sequenced last so it styles the UI's final shape. At M18's completion the owner raised the bar: a user should not have to be an AI expert to use the product, and should never have to hand-edit a config file — with CyboFlow's human-first structure (a central view organized by what needs the human next: permission, decision, or action) the named inspiration. M19 is therefore redefined as a product-UX milestone; the original design pass survives as its next-to-last phase, still styling the final shape. M19's phase plan is below.

---

## M19: Product UX — Phase Plan

**Goal:** the product a non-expert can drive end to end (the owner's stated bar at M18's
completion): a task-first, plain-language UI where every path runs through pickers, forms, and
guided flows — never a typed path, a raw JSON textarea, or a hand-edited config file — organized
the way CyboFlow organizes attention: a central view grouped by what needs the human next, the
workflow graph as the primary visualization, review surfaces one click away. Everything the UI
does today survives; what changes is organization, language, and authoring ergonomics.

Four facts shape the plan. First, **Flow, Core, and the workers change by zero lines, and the
durable contracts change by zero bytes.** Templates, bindings, worker-config sidecars, task
directories, and the mutation interface all keep their exact shapes — this milestone changes how
humans reach them, never what they are. Every §6 prohibition binds exactly as before: nothing
here grants the UI new authority, only new ergonomics over the authority it already has. Second,
**the scope redefinition is on the record** (roadmap above): the original "no new capability"
design pass survives as Phase 5, sequenced next-to-last for the same reason it was originally
sequenced last — it styles the final shape. Third, **the spec gap is settled at planning** —
"never hand-edit a config file" requires the UI to author the dialogue worker's config sidecar
(reached via `WorkerInvocation.PromptTemplate`, M17 Phase 4's decision), and §4's write list
named only template and bindings files; the amendment adds worker-configuration files a bindings
entry references, on the bindings files' own terms, with the file's *meaning* still owned by the
worker that reads it (§18.2). Fourth, **the inspiration maps structurally, not cosmetically**:
paused steps are already a decision inbox, the DAG is already the workflow visualization,
artifacts/conversations/diffs are already the review surface — the milestone builds the missing
organization and language around concepts the engine already guarantees, which is why the engine
does not move.

### Phase dependency table

| Phase | Requires output from |
|---|---|
| 1 — UX baseline, principles, and information architecture (#186) | — |
| 2 — Navigation shell: Home, Task, and Author views (#187) | 1 |
| 3 — Task view, human-first (#188) | 2 |
| 4 — Guided authoring: no hand-edited config files (#189) | 2 |
| 5 — Visual design pass (#190) | 3 + 4 |
| 6 — Gate: the non-expert path in default CI (#191) | 5 |

Phases 3 and 4 fan out after the shell lands — the same shape as M14–M16's projection-then-tracks
split. Phase 5 styles the finished structure; Phase 6 gates the whole path.

### Phase 1 — UX baseline, principles, and information architecture (#186)
The requirements capture before any code moves (the M17 Phase 1 pattern): walk
`docs/walkthroughs/first-real-workflow.md` as a deliberate non-expert and record every step that
assumes AI/systems expertise or a hand-edited file — each is a requirement on Phases 2–4.
Produce `docs/ux/`: the UX principles (plain language everywhere — no spec-section numbers or
engine jargon as user-facing text, with spec terms surviving in tooltips for §12 traceability; a
task-first vocabulary map, e.g. supersede → "send back", PausePoint → "review gate"; progressive
disclosure — advanced detail exists but is never the entry path) and the information
architecture (Home with task cards + the decision inbox, Task view, Author view).

**Produces:** the UX principles + IA doc; the audited requirements list.
**Excludes:** any code (Phases 2–5).

### Phase 2 — Navigation shell: Home, Task, and Author views (#187)
Replace the single stacked-sections window with the Phase 1 IA: a navigation shell whose Home
lists recent task directories as live status cards (rebuilt from durable contents per §3.1; the
recents list stays Local UI Configuration) with a decision inbox — everything across recent
tasks currently waiting on the human, grouped and one click from acting. A behavior-preserving
re-home of every existing surface: all headless round trips re-pointed and green.

**Produces:** the shell; every M14–M18 surface reachable in its new home.
**Excludes:** surface redesigns (Phase 3), authoring changes (Phase 4), styling (Phase 5).
**Open questions resolved in this phase:**
- **The inbox's scan scope** — only the open task vs. all recent task directories (both
  §11-deterministic; the difference is refresh cost and how the inbox feels when empty).

### Phase 3 — Task view, human-first (#188)
The task view rebuilt around the DAG as the primary surface: plain-language status vocabulary
(Phase 1's map), per-step drill-in for everything that today sprawls as separate stacked
sections — attempts, artifacts + preview, conversation (M18's view moves in as a tab, behavior
unchanged), recorded decisions — and decision actions inline on the paused step in plain words,
with the revision-file affordance a picker, never a typed path. Live-follow on by default.

**Produces:** the redesigned task view; all M15 decision round trips green through it.
**Excludes:** authoring (Phase 4); styling beyond structure (Phase 5).

### Phase 4 — Guided authoring: no hand-edited config files (#189)
The "never muck with a config file" phase: a New Workflow flow authoring template + bindings
form-first — pickers and dropdowns, never typed paths or raw JSON textareas; vendor presets for
bindings entries (the known adapters' sensible defaults, permission scopes explained in plain
words); the dialogue step as a first-class authored step type, writing its config sidecar in-app
per the §4 amendment — the file still exists as the durable format, the user just never opens
it. Validation surfaced as inline guidance; Run from the end of the flow without leaving it.

**Produces:** author → run entirely through UI controls, zero hand-authored files.
**Excludes:** new engine/file formats (durable contracts unchanged); styling (Phase 5).
**Open questions resolved in this phase:**
- **Where authored files live by default** — a UI-managed default workspace (paths visible but
  never required) vs. always-explicit locations.
- **Where the vendor presets' invocation knowledge lives** — without the UI re-encoding the
  adapter quirk catalog spike #21 isolated behind the adapter seam.

### Phase 5 — Visual design pass (#190)
The original M19 content, styling the final shape: one coherent visual system across the Phase
2–4 surfaces — typography, spacing, a color + status iconography system (one status → one
color/icon everywhere), empty states that say what to do next, window sizing that respects the
new IA.

**Produces:** the finished look; every surface visually consistent.
**Excludes:** any behavior change.

### Phase 6 — Gate: the non-expert path in default CI (#191)
The milestone's gate, unattended on all three OSes: one headless round trip driving the whole
non-expert path through the new UI's actual controls — author a workflow (including a dialogue
step) with zero hand-authored files, run to the review gate over stub CLIs, send back, resume to
terminal, read the conversation. Every pre-existing M14–M18 round trip green in its re-pointed
home; M14's golden-projection gate untouched. Plus the rewritten walkthrough:
`first-real-workflow.md` redone against the new UI, with the live run remaining the standing
human action item (CLAUDE.md's live-vendor rule).

**Produces:** M19 complete — the product a non-expert can drive, proven in default CI.

---

## Current Milestone

**M19: Product UX** — phase plan above. Progress:

- ⬜ Phase 1 — UX baseline, principles, and information architecture (#186)
- ⬜ Phase 2 — Navigation shell: Home, Task, and Author views (#187)
- ⬜ Phase 3 — Task view, human-first (#188)
- ⬜ Phase 4 — Guided authoring: no hand-edited config files (#189)
- ⬜ Phase 5 — Visual design pass (#190)
- ⬜ Phase 6 — Gate: the non-expert path in default CI (#191)

Per this document's session prompt: help implement the current phase only.

## Completed Milestones

Completed milestones keep only a one-paragraph summary here. Their phase checklists live in the
closed GitHub milestones; their decisions of record — the constraints and precedents later work
still leans on — in `docs/decisions-of-record.md`; and the full phase plans — goals, boundaries,
and the open questions each phase resolved — in this file's git history and the linked issues.

**M18: Conversation View** — UI spec §10's conversation view over M17's durable transcript:
the tolerant `Aer.Ui` read seam honoring §10.1's reader contract (the transcript artifact
contract, settled at M18 planning as a spec amendment — discovery by `transcript.jsonl` presence
alone, producing schema worker-owned per Flow spec §18.2), conversation rows plus per-execution
rendering in the main window (file order, prompt on demand, malformed lines marked in place,
selection re-rendered on every load so refresh follows a live exchange), gated by
`ConversationRoundTripTests`: the real dialogue worker run to terminal over stub CLIs and the
view asserted over its artifacts — including a failed exchange's forensic prefix. No live gate:
nothing in the milestone touches a vendor CLI.

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
- ~~**Worker-config sidecar files vs. the UI write model (UI spec §4 vs. M19's no-hand-edited-files bar)**~~ — resolved, during M19 planning (#185, before Phase 4/#189, which would otherwise have had to decide it mid-implementation): §4's write list now names worker-configuration files that a worker-bindings entry references (e.g. the dialogue worker's config sidecar, reached via `WorkerInvocation.PromptTemplate` per M17 Phase 4's decision), on the same terms as bindings files — a UI/CLI input, never durable task state, never written into a task directory — with the file's *meaning* still owned by the worker that reads it (Flow spec §18.2): the grant covers authoring, not reinterpreting. Unblocks M19 Phase 4 (#189).

## Notes for future work

- **A third worker adapter (`Aer.Adapters`)** — Claude shipped in M11 Phase 2 (#85), Gemini/`agy` in M12 Phase 1 (#95). Before adding another vendor, read closed spike [#21](https://github.com/aer-works/aer-flow/issues/21)'s recorded findings — stdin stalls, permission-flag vocabularies, and path-interpolation behavior differ per CLI and are exactly what the adapter seam exists to absorb. (M17 Phase 4's dialogue adapter is not this note's case: it spawns aer's own dialogue worker executable, not another vendor's CLI — the vendor quirks stay inside the worker, which inherits them from the two existing adapters' recorded findings.)
- **Whether MVVM spreads beyond the decision surface** — M15 Phase 2 (#138) deliberately scoped `CommunityToolkit.Mvvm` to the paused-step Approve/Reject buttons, the first *interactive, stateful* control surface (enabled state tied jointly to projected state and an in-flight mutation). The DAG/history/lineage/diff rendering stayed code-behind on purpose: it's one-directional (projection → controls, nothing to bind against), so a ViewModel there would be ceremony with no payoff. Phase 3 (Retry-with-revision, Send-back) and Phase 4 (Cancel) add more of the same interactive shape, so expect the ViewModel layer to grow phase over phase rather than needing a deliberate decision to introduce it again. Revisit whether the read-only surfaces are worth converting too only if M16 (Authoring) needs two-way binding there — not preemptively.
