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

M17–M19 are the post-UI-track sequence, planned at M16's completion by re-checking the original project goal against what had shipped. Half of that goal exists and is proven live: vendor-to-vendor task hand-off on subscriptions (M12's recorded mixed-vendor gate). The other half — letting the two models actually talk to each other — does not: today §17.5's supersede loop makes the *human* the relay for every round of the exchange. **M17** builds the first Case 2 worker (the dialogue worker — the concrete thing the conversation view has been waiting on), opening with the real-use walkthrough the project is also missing. **M18** renders M17's durable transcript as UI spec §10's conversation view — load-on-refresh first; live Observation-Tier turn streaming stays unassigned until a concrete need names it. **M19** is the deliberate visual/UX design pass over the whole UI, sequenced last so it styles the UI's final shape — conversation view included — rather than a layout M18 is about to disrupt. M18 and M19 get their phase plans when each becomes current (this document plans the current milestone only); M17's is below.

---

## M17: Dialogue Worker — Phase Plan

**Goal:** the first Case 2 encapsulated multi-model worker (capability #24; Flow spec §18.2): a
single executable that internally runs a bounded, multi-turn Claude ↔ Gemini (`agy`) exchange —
each model's turn threaded into the other's next prompt — writing a durable `transcript.jsonl`
plus its declared outputs, dispatched by Flow like any other worker. Plus, as Phase 1, the
real-use walkthrough the project has been missing: a document driving one real task through the
machinery M11–M16 already shipped. This milestone turns "the two models can hand work to each
other, with a human relaying every round" (§18.1 + §17.5, proven live in M12) into "the two
models can talk to each other inside one execution" — the second half of the project's original
goal. Deliberately excluded and assigned to M18: any UI rendering of the transcript (the
conversation view, UI spec §10). Deliberately excluded and assigned to no milestone: live
Observation-Tier turn streaming, and pausing/steering the worker mid-exchange (§17.4 places that
outside Flow's contract above the worker boundary; nothing in this milestone builds it inside
the boundary either).

Four facts shape the plan. First, **Flow and Core change by zero lines.** §18.2 already defines
the boundary: one `ExecutionRequest`, one process lifecycle, one output directory — to the engine
a dialogue execution is indistinguishable from running `cargo test`. Retry (§10), pause
before/after (§17.1), contract validation (§8), cancellation (§9), and the artifact machinery
(§16) all apply to it for free, because it is just a worker. The only production code above the
worker boundary that learns anything is `Aer.Adapters`: `WorkerAdapterRegistry.Default` resolves
only `claude`/`gemini`, so dispatching the dialogue executable needs a third registry entry
(Phase 4) — and that adapter spawns *our own* executable, not a vendor CLI, so spike #21's
per-vendor quirk catalog mostly does not apply to it: the vendor quirks live inside the worker,
which reuses the invocation shapes the two vendor adapters already encode. Second, **the
discipline/intelligence rule inverts inside the boundary.** Flow must never parse model output
(CLAUDE.md rule #1), but §18.2 explicitly places multi-model coordination logic inside the worker
— the dialogue worker may (and must) read each turn to thread context and detect a stop signal.
Turn budget, per-side prompt preambles, and stop conditions are the worker's own configuration,
never workflow-template or engine concepts. Third, **subscription discipline is inherited, not
re-invented**: the worker shells out to the same already-authenticated `claude`/`agy` CLIs the
adapters do, owning zero key-handling code of its own; its live gate is a human-run runbook
exactly like M11/M12's (CLAUDE.md's live-vendor rule — permanently a human action item); and
everything else runs against stub CLIs in default CI. Fourth, **the transcript schema is M18's
data model**, per UI spec §10: every visible conversation step must correspond to durable
artifacts, and the transcript is that artifact — so it gets settled early (Phase 2) and recorded,
with the ledger entry below tracking where it eventually gets specified, not improvised at the
end. Its crash semantics are §18.2's stated tradeoff, accepted as-is: the transcript is worker
*output*, never resumable state; a crash restarts the whole exchange from turn one.

### Phase dependency table

| Phase | Requires output from |
|---|---|
| 1 — Real-workflow walkthrough (§18.1 baseline) | — |
| 2 — Transcript contract + dialogue worker skeleton | — |
| 3 — Turn loop, termination, and failure semantics | 2 |
| 4 — Dispatch integration: the third adapter | 2 |
| 5 — Gates: stub round trip in default CI + live dialogue runbook | 3 + 4 |

Phases 3 and 4 are independent once Phase 2 lands — the same fan-out shape as M14–M16. Phase 1
has no code dependents at all: it documents what already shipped, and what the human-relay
experience lacks is Phase 2's requirements baseline.

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

**M17: Dialogue Worker** — phase plan above. Progress:

- ✅ Phase 1 — Real-workflow walkthrough (§18.1 baseline) (#164)
- ✅ Phase 2 — Transcript contract + dialogue worker skeleton (#165)
- ✅ Phase 3 — Turn loop, termination, and failure semantics (#166)
- ⬜ Phase 4 — Dispatch integration: the third adapter (#167)
- ⬜ Phase 5 — Gates: stub round trip in default CI + live dialogue runbook (#168)

Per this document's session prompt: help implement the current phase only.

Decisions of record from M17:

- **The walkthrough documents verified behavior, not intent** — every command in
  `docs/walkthroughs/first-real-workflow.md` was executed end to end over stub vendor CLIs
  (run → pause → supply → supersede cascade → resume → terminal, exit 0) before being written
  down. Two facts the dry run corrected against the code: the default task directory is
  `.aer/<workflow-file-stem>` (the *file* stem, not the template id), and
  `AER_SUPPLEMENTARY_INPUT` names the supplementary execution's output *directory*
  (`ArtifactManager.ResolveSupplementaryInputPath` — "addressed the same way as any other
  execution's"), never the supplied file itself (Phase 1).
- **Requirements captured for the dialogue worker**, recorded in the walkthrough's §8: the
  supplementary path is not surfaced in any adapter's generated prompt — and can't be surfaced
  per-dispatch under the current seam, since `IWorkerAdapter.Resolve` runs once per role
  (M11's decision of record), so an unconditional env-var reference is the only available
  adapter-level shape (Phase 4's open question now has this constraint attached); a
  vendor-bound step that must *consume* a send-back needs a shell-capable `PermissionScope`
  (`"Bash,Read,Write"` for Claude) purely to discover the feedback path; and the live
  walkthrough run itself remains a human action item per CLAUDE.md's live-vendor rule —
  the stub dry run is the part an agent session can and did verify (Phase 1).
- **The worker lives at a new `Aer.Workers.Dialogue` leaf** (`src/Aer.Workers.Dialogue`, tested by
  `tests/Aer.Workers.Dialogue.Tests`), Overview §7's default, resolving Phase 2's first open
  question — not a `scripts/` shell script. It references neither `Aer.Flow` nor `Aer.Adapters`:
  per §18.2, a Case 2 worker is "indistinguishable from running `cargo test`" to the engine, so
  nothing above the worker boundary needs to change, and nothing inside this boundary needs to
  reach back across it. How it ships (riding `aer`'s existing `dotnet tool` package vs. its own) is
  left for Phase 4/5, when dispatch integration and packaging actually need an answer (Phase 2).
- **`transcript.jsonl`'s schema is `TranscriptTurn` (sequence, role, vendor, prompt, text), one per
  line** — documented on the record itself rather than a separate markdown file, this codebase's
  existing convention for spec-bearing types (`WorkerContract`, `WorkerInvocation`). `Role` is the
  configured participant's logical name (e.g. `"initiator"`/`"responder"`), never a vendor name, so
  a transcript reader can tell who argued which side independent of which vendor played it. Whether
  UI spec §10 names this schema is still open, per the ledger entry, for M18 planning to settle
  (Phase 2).
- **The worker's own config surface (`DialogueWorkerConfig`) is a JSON sidecar the executable reads
  from a config-file-path argument** — mirrors `WorkerBindingConfigParser`'s "parse, then validate
  structurally" shape and exception style, but is its own type family (`DialogueWorkerConfigException`
  extends `Exception` directly, not `Aer.Flow.AerFlowException`), since this worker depends on
  neither `Aer.Flow` nor `Aer.Adapters`. Carries a provisional `StopSentinel` field so the format
  does not change shape again once Phase 3 decides the real stop-signal mechanism; the skeleton
  itself ignores it and always runs the full `TurnBudget`. *How* this config path reaches the
  worker once Flow actually dispatches it remains Phase 4's open question, deliberately left
  unresolved here (Phase 2).
- **Per-turn vendor invocation is direct process spawning with no shell wrapper** — unlike
  `Aer.Adapters`'s vendor adapters, a per-turn call never touches `AER_INPUT_<n>`/`AER_OUTPUT_DIR`
  (those are Flow's top-level dispatch convention, meaningless to a call made entirely inside the
  worker's own process), so nothing needs shell-based environment-variable expansion. Each
  participant names a `Command` and an `Args` list containing one literal `"{PROMPT}"` token,
  substituted with the turn's prompt at spawn time and passed via `ProcessStartInfo.ArgumentList`
  — every argument reaches the child exactly once, correctly quoted by the runtime, with none of
  `ClaudeWorkerAdapter`/`GeminiWorkerAdapter`'s shell-quoting hazards. Real per-vendor argument
  shaping (the actual `claude`/`agy` flag vocabularies, spike #21's realities) stays out of this
  skeleton — Phase 3's turn loop is where that lands. Context threading is deliberately minimal for
  the same reason: each turn's prompt is its speaker's preamble plus only the immediately preceding
  turn's text, not the full transcript — enough to prove the loop and the schema, not a context-
  window design (Phase 2).
- **The stop-signal shape is a literal substring in a turn's own text, not a structured per-turn
  output file** — resolving Phase 3's named open question. Spike #21 already recorded that vendor
  CLIs are unreliable about writing extra files on cue (the walkthrough's §8 finding: `agy` asking
  a clarifying question and writing nothing at all) but reliably produce stdout text, which
  `DialogueRunner` already reads for every turn regardless — parsing that same text for a sentinel
  needs no new per-turn output-file contract each vendor CLI would separately have to honor, and is
  the more robust of the two shapes across two different vendors' output habits for exactly that
  reason. `DialogueWorkerConfig.StopSentinel`, carried as provisional config since Phase 2, is now
  live: `DialogueRunner` checks each turn's raw (post-empty-check, pre-recording) text for the
  configured sentinel substring; if present, it is stripped from the text recorded on the
  transcript and threaded forward — a transcript reader sees the participant's actual words, never
  the control token — and the exchange ends after that turn, before `TurnBudget` is necessarily
  exhausted (Phase 3).
- **Context threading is the full transcript so far, not a sliding window** — resolving the
  phase's other named question. `DialogueWorkerConfig.TurnBudget` is this worker's own config and
  deliberately small (the phase plan's "bounded" exchange), so a bounded turn count is what keeps
  the full transcript's size a non-issue for spike #21's CLI-argument-length realities, without
  this worker inventing a token-budget or summarization scheme of its own — the same reasoning
  that kept `OutputCondition` free of a general expression language (behavioral spec §4.1): a
  narrower mechanism sized to the actual bounded need, not a general one built ahead of a concrete
  requirement for it. `DialogueRunner.RunAsync` now builds each turn's prompt from the speaker's
  preamble, the exchange's `SeedPrompt`, and every prior turn's role and text in order — Phase 2's
  "only the immediately preceding turn's text" placeholder is gone (Phase 3).
- **Failure mapping: a non-zero vendor exit or an empty turn throws `DialogueExecutionException`
  mid-loop, deliberately before the failing turn is appended to the transcript and before
  `FinalOutputName` is ever written.** `Program` maps the exception to a non-zero process exit, so
  Flow's `OutcomeClassifier`/`ContractValidator` (spec §8) see a broken dialogue fail on both counts
  at once — non-zero exit *and* a missing declared output — deliberately redundant, not
  either-or, so the failure is unambiguous however a caller happens to check it, mirroring the
  `agy`-writes-nothing precedent `ContractValidator` already handles for any other worker.
  Whatever `transcript.jsonl` lines were appended for turns that succeeded *before* the failing one
  stay on disk as a forensic record; per §18.2's tradeoff, restated deliberately and not worked
  around, there is no resumption from them — the step's ordinary `RetryPolicy` (spec §10) restarts
  the whole exchange from turn one on retry, exactly like any other worker's retry (Phase 3).
- **`IVendorTurnClient.SendTurnAsync` returns a new `VendorTurnResult(Text, ExitCode, StandardError)`
  record instead of a bare string** — `DialogueRunner` needs the exit code to classify a turn as
  failed (the same "exit code alone is not success" split `OutcomeClassifier` applies one layer up
  in Flow) and captured stderr to put something a human can act on into the failure message;
  `ProcessVendorTurnClient` now redirects and reads stderr, concurrently with stdout via
  `Task.WhenAll` before `WaitForExitAsync`, avoiding the pipe deadlock a chatty CLI's unread stream
  would otherwise risk (Phase 3).

## Completed Milestones

Completed milestones keep only their phase checklist and any decisions of record later work
still leans on. The full phase plans — goals, boundaries, and the open questions each phase
resolved — live in this file's git history and in the linked issues.

**M16: UI Authoring** — the last milestone of the original UI track: template and worker-bindings
authoring in `Aer.Ui` — create/edit steps, dependencies, retry policies, metadata,
`PausePoint`s/`SupersedeTargets`, and bindings entries, with live structural validation through
Flow's own `WorkflowDefinitionValidator`, the stack's first template and bindings writers held to
round-trip fidelity through the engine's own parsers/validators, and full authoring round trips
(author from blank → save → run to terminal; edit a bound task's template → the diff view shows
the divergence while the bound rendering stays byte-identical) proven in default CI on all three
OSes.

- ✅ Phase 1 — Template write seam + create/save walking skeleton (#150)
- ✅ Phase 2 — Step & graph editing with live structural validation (#151)
- ✅ Phase 3 — PausePoint + SupersedeTargets editing (#152)
- ✅ Phase 4 — Worker-binding configuration editing (#153)
- ✅ Phase 5 — Authoring round trips in default CI (#154)

Decisions of record from M16:

- **The template writer is `WorkflowDefinitionWriter`, beside its parser in `Aer.Flow.Templates`** —
  not inside `Aer.Ui`, resolving the phase plan's named seam decision. Round-trip fidelity
  (save → parse → validate through the exact code every other consumer uses) is a domain-layer
  property, guaranteed by construction only when both directions share the same
  `System.Text.Json` converters, and `SnapshotBinder.PersistAsync` already established that
  domain-record file writers live in this namespace. Flow's engine still never writes a template
  on any execution path — the writer has no caller inside `Aer.Flow` itself; the UI is the caller,
  exactly as UI spec §4 assigns. Output is indented (a template is a human-editable file, §11.1's
  own framing), and the round-trip bar is parse-level, never byte-level (Phase 1).
- **The writer validates before writing** — `WorkflowDefinitionWriter.Serialize` runs
  `WorkflowDefinitionValidator.Validate` first and writes nothing on failure, the same
  public-entry-point reasoning as `SnapshotBinder.Bind`. Phase 2's save-validity open question
  (may an invalid in-progress graph be saved as a draft?) can loosen this deliberately; until it
  does, a saved template file is engine-valid by construction (Phase 1).
- **The editing model is in-memory `WorkflowDefinition` + explicit Save with dirty tracking,
  as a separate editor surface riding the MVVM layer** — `TemplateEditorViewModel` (a child of
  `MainWindowViewModel`, the first two-way-bound surface, exactly the shape M15's
  notes-for-future-work entry anticipated) holds the metadata fields and the baseline they were
  loaded from; `MainWindow.NewTemplate`/`OpenTemplateInEditorAsync`/`SaveTemplateAsync` own all
  file I/O, the same state-in-VM/I-O-in-window split `PausedStepViewModel` established.
  M14 Phase 3's read-only template projection is untouched: `OpenAsync` still routes a template
  file straight to the read-only DAG view — inspecting and authoring are separate surfaces, so
  the read-only view never has to defend against a half-edited state (Phase 1).
- **§11.1's version-increment rule is implemented in `MainWindow.SaveTemplateAsync` exactly as the
  spec amendment settled it**: a content-changing save increments `WorkflowTemplateVersion` from
  the loaded baseline — unless the user explicitly set a different version themselves, which is
  respected as-is (a hand-editor may legitimately do the same); a no-op save writes nothing and
  increments nothing (`No changes to save.`); and a brand-new template's first save has no saved
  predecessor to distinguish from, so it saves the version as entered
  (`TemplateEditorViewModel.BaselineIsPersisted` is how Save tells the two apart). The incremented
  version is written back into the editor's fields after save, so the box never silently diverges
  from disk (Phase 1).
- **Template saves are deliberately not gated on `IsMutationInFlight`** — a template file is not
  durable task state, no §15 task lock is involved, and an edit is visible only to future
  instantiations regardless (UI spec §5), so the editor stays usable while a pump is in flight
  (Phase 1).
- **The save-validity discipline: Save stays blocked until the in-progress graph is valid — Phase
  1's rule is not loosened.** `TemplateEditorViewModel.BuildCandidate` returns every parse and
  `WorkflowDefinitionValidator` violation found; `SaveTemplateAsync` refuses to write while any
  remain, surfacing them verbatim in `StatusText`. There is no draft-storage concept elsewhere in
  the stack — a template file is the sole authoring artifact and exactly what instantiation reads —
  and a blocked Save loses no work: the in-memory `Steps` collection and every field persist for the
  whole editing session regardless of current validity, so the user can edit across a temporarily
  invalid intermediate state and only needs to reach validity once, at the moment they choose to
  save (Phase 2).
- **`DependsOn` is edited as a checkbox per declared step (`StepEditorViewModel.DependsOnOptions`),
  never free text** — candidates are offered from the template's other declared steps only,
  excluding the step itself (impossible-by-construction, not a validator rule tripped), the
  authoring counterpart of M15's "reflect, don't invent" send-back discipline. A step's
  `SelectedDependsOn` set is keyed by target `StepId` text and survives `DependsOnOptions` being
  rebuilt after an unrelated edit; a rename elsewhere is treated as a new identity — an old
  selection under stale text is not carried forward — the same way the validator itself would flag
  a stale reference as unresolved rather than silently repair it (Phase 2).
- **The DAG preview re-layout is gated on full validation passing, never attempted against an
  invalid in-progress graph.** `DagLayoutEngine.Layout` assumes an already-structurally-valid graph
  (acyclic, every `DependsOn` reference resolvable) and does not itself guard a cycle or a dangling
  reference — calling it on an invalid graph risks an unbounded recursion or a dictionary-lookup
  crash, not a graceful validator-style rejection. `TemplateEditorViewModel.PreviewLayout` is
  `null` whenever `ValidationErrors` is non-empty; the editor's dedicated `TemplateEditorDagCanvas`
  simply clears rather than rendering a stale layout (Phase 2).
- **Dirty tracking and the no-op-save check use a dedicated structural-equality helper
  (`TemplateEditorViewModel.DefinitionsAreEqual`), not `WorkflowDefinition`'s record `==`.** Once
  `Steps` is editable, every list-typed member compares by reference under default record equality,
  not by content, so `==` silently under-reports changes. `DependsOn` compares as a set (the
  validator never treats it as ordered); every other list compares in sequence (Phase 2).
- **`WorkflowDefinitionValidator` gains a public `ComputeTransitiveAncestors(WorkflowDefinition)`,
  reusing the exact ancestor walk `Validate` already runs internally for `SupersedeTargets`
  ancestry, rather than a second implementation in `Aer.Ui`.** The authoring counterpart of "Flow
  carries discipline": a `SupersedeTargets` candidate list needs the live in-edit graph's actual
  ancestor set, and re-deriving that in the UI would risk drifting from the validator's own rule.
  Carries the same precondition `DagLayoutEngine.Layout` already does — acyclic, every `DependsOn`
  reference resolvable — so it is only ever called once `WorkflowDefinitionValidator.Validate` has
  already succeeded (Phase 3).
- **`SupersedeTargets` is edited as a checkbox per this step's actual transitive ancestor
  (`StepEditorViewModel.SupersedeTargetOptions`), gated on the whole graph currently validating** —
  the same "reflect, don't invent" shape `DependsOn` established in Phase 2, extended with a
  validity gate `DependsOn`'s own candidate list didn't need (an ancestor walk, unlike "every other
  declared step," isn't safe to compute against a cyclic or dangling-reference graph). A
  `PausePoint`'s own toggle (`HasPausePoint`) is independent of its targets — turning it off writes
  `PausePoint = null` regardless of what remains selected, and turning it back on does not clear
  prior selections (Phase 3).
- **An edit that orphans an already-selected `SupersedeTargets` entry (removing the `DependsOn`
  path that made it an ancestor) is never silently dropped — it rides into the candidate unchanged
  and surfaces as a live `WorkflowDefinitionValidator` "not a transitive ancestor" violation**,
  exactly as the phase plan requires (live, not save-time). `StepEditorViewModel.SelectedSupersedeTargets`
  is the authoritative selection state independent of whatever `SupersedeTargetOptions` currently
  offers — an option list is allowed to go briefly stale while the graph is otherwise invalid, since
  ancestor computation isn't safe against invalid input, but the underlying selection is never
  touched by that staleness (Phase 3).
- **`PausePoint` equality for dirty tracking and the no-op-save check is content-based
  (`TemplateEditorViewModel.PausePointsAreEqual`), not the reference equality Phase 2's pass-through
  relied on** — `BuildCandidate` now constructs a fresh `PausePoint` from editor state on every call,
  so a loaded step's original instance is no longer threaded through untouched once `PausePoint`
  itself is editable (Phase 3).
- **The bindings writer is `WorkerBindingConfigWriter`, beside its parser in `Aer.Adapters`** — not
  inside `Aer.Ui` or `Aer.Flow.Templates`, resolving the phase plan's named seam decision the same
  way Phase 1 resolved it for templates. The bindings shape (adapter names, `WorkerContract`,
  prompt/timeout/model/permission scope) lives entirely in `Aer.Adapters` already (Adapter
  Isolation, CLAUDE.md's own architecture rule) — putting the writer anywhere else would split a
  format's read and write sides across the isolation boundary the rule exists to prevent. `Aer.Ui`
  is the writer's only caller, exactly as UI spec §4 assigns (Phase 4).
- **The writer validates by round-tripping through the parser, not a separate validator** — there is
  no `WorkerBindingConfigValidator`; `WorkerBindingConfigParser.Parse`'s own field checks (non-blank
  `Adapter`, a present `Contract`, non-blank `PromptTemplate`) are this format's only validation.
  `WorkerBindingConfigWriter.Serialize` proves them by parsing its own serialized output before ever
  returning it, and writes nothing on failure — the same "public entry point re-validates, saved
  state is always engine-valid" discipline `WorkflowDefinitionWriter.Serialize` established via
  `WorkflowDefinitionValidator`, adapted to a format whose only validation already lives in its
  parser (Phase 4).
- **Adapter names are offered per-row, from the registry `MainWindow` was constructed with** — each
  `WorkerBindingEntryViewModel` carries its own `AdapterCandidates` list (set once from
  `MainWindow`'s `IReadOnlyDictionary<string, IWorkerAdapter>` constructor argument, M15 Phase 1's
  decision of record) rather than a shared binding to a root-level list, because inside an
  `ItemsControl.ItemTemplate` the bound `DataContext` is the row itself — an ancestor/relative-source
  binding back to `MainWindowViewModel` is the awkward path in Avalonia, a per-item list is the
  established one (`PausedStepViewModel.SendBackTargets` already does this). Not a hard gate: the
  `Adapter` box is an editable `ComboBox` seeded with these candidates, since nothing in
  `WorkerBindingConfigParser.Parse` validates an entry's `Adapter` against any registry either
  (Phase 4).
- **Structured vs. opaque editing on `WorkerContract`**: `Adapter`, `PromptTemplate`, `Timeout`,
  `Model`, `PermissionScope` (the entry's scalars) and `RequiredInputs`/`OptionalMetadata` (its two
  plain-string lists, edited as comma-separated text) all get real structured editing.
  `ProducedOutputs` does not — each entry is a small record of its own (`Name` plus an optional
  `OutputCondition` carrying a `JsonScalar` sum type: string/number/bool/null), and a safe small
  editable surface for that shape (per-item add/remove, a scalar-type picker) is new list-editing
  machinery this phase's scope doesn't call for. It round-trips opaquely instead, as a raw JSON text
  box using the same `System.Text.Json` converters the parser/writer use, so fidelity — including
  `OutputCondition` — is guaranteed by construction rather than by a hand-written mapping this phase
  would otherwise have to get right (Phase 4).
- **Dirty tracking cannot reuse Phase 1's `==`-on-record trick** — `TemplateEditorViewModel`'s Save
  builds its candidate via `baseline with { ... }`, which keeps the very same `Steps` list reference
  when steps are untouched, so record equality (which does not deep-compare `IReadOnlyList` fields)
  already happens to be correct there. A bindings save always rebuilds a fresh `Dictionary` from the
  editable rows, so two structurally-identical configs are never reference-equal.
  `BindingsEditorViewModel` uses a manual deep-equality check (`ConfigEquals`/`EntryEquals`,
  `SequenceEqual` on the list fields) instead, recomputed via `PropertyChanged` subscriptions on
  every row rather than the per-field `OnXChanged` partial methods `TemplateEditorViewModel` and
  `PausedStepViewModel` use — one central subscription per row instead of ten near-identical partial
  methods (Phase 4).
  Note: Phase 2 later gave `TemplateEditorViewModel` its own dedicated structural-equality helper
  (`DefinitionsAreEqual`) once `Steps` became editable there too, for the same underlying reason —
  see Phase 2's own decision above.
- **The template↔bindings advisory cross-check reads `TemplateEditorViewModel.Baseline`** — "the
  currently-open template" (the phase's own open-question wording) is read from the template
  *editor's* in-memory state, not the read-only DAG view's `LoadTemplateAsync`, which never retains
  its loaded definition as a field at all. This is a read-only consultation of already-computed
  state, not a change to template-editing code: nothing here writes to, or is called from,
  `TemplateEditorViewModel` or `OpenTemplateInEditorAsync`, honoring the phase's exclusion of
  touching Phases 1-3's surface. `MainWindow.RefreshBindingsTemplateCrossCheck` is called explicitly
  (New/Open/Save bindings, adding a row, or a dedicated "Check against open template" button) rather
  than wired to any template-editor change notification, for the same reason. Strictly one-directional
  (template workers missing a binding, never the reverse) and never consulted by
  `SaveBindingsAsync` — advisory display only, per UI spec §9 (Phase 4).
- **Unlike M15 Phase 5, the gate needed genuinely new test code, not a relabeling of each earlier
  phase's own end-to-end proof.** Grepping Phases 1–4's test classes for `RunAsync`/
  `CompareToTemplateAsync` returned zero matches — every prior M16 test drives `SaveTemplateAsync`/
  `SaveBindingsAsync` and stops at the saved file, never the Run action or the diff view. The three
  round trips the phase plan names live in a new `AuthoringRoundTripTests`, each stitching an
  authoring surface (Phases 1–4) to a surface an earlier milestone shipped: a template built from
  blank through `TemplateEditorViewModel` (Phase 2's own walking-skeleton shape), saved, and run to
  `Terminal` through `MainWindow.RunAsync` (M15 Phase 1) over a directly-written shell-stub bindings
  file; a template file a bound task's snapshot already reflects, edited and saved through the same
  editor (adding a step, `WorkflowTemplateVersion` incrementing per §11.1), then compared back
  through `MainWindow.CompareToTemplateAsync` (M14 Phase 4) — asserting both the diff panel reports
  the added step and the *bound task's own* `StepsPanel` rendering is byte-identical before and
  after, since `RenderDiff` only ever touches `DiffPanel`; and a bindings file built entirely from
  blank rows through `BindingsEditorViewModel.AddEntry`/`SaveBindingsAsync` (Phase 4), then driving
  the same `RunAsync` call to `Terminal` with zero bindings content written by hand (Phase 5).
- **No new CI workflow or job — the same "`Aer.Ui.Tests` is already a leaf" precedent M14 Phase 5
  and M15 Phase 5 established holds a third time.** `AuthoringRoundTripTests` runs unattended in
  `pixi run test`'s plain `dotnet test` on all three of `ci.yml`'s matrix OSes. Verified green
  end to end for this phase: `dotnet build -warnaserror` (lint), `dotnet format --verify-no-changes`
  (fmt-check), and the full `dotnet test` run — all four `AerFlow.slnx` test projects, 527 tests
  total (`Aer.Ui.Tests` 138, up from 135), including every M14 Phase 5 golden-projection fact —
  pass with no changes to `Aer.Flow`, `Aer.Adapters`, or `Aer.Cli`: this phase adds only test code
  (Phase 5).
- **The UI spec v0.9 → v1.0 promotion question the phase plan flags is deliberately left open by
  this phase, not answered by it.** The phase plan says milestone completion "owes the ledger" the
  answer, not that this PR must execute a promotion — renaming a canonical spec and declaring its
  status is a different kind of change than the round-trip tests this phase exists to add, and
  bundling it into a test-only PR would put a doc-status call on the same merge-on-green path as
  mechanical, easily-verified test code. The recommendation (worth a deliberate follow-up, not
  silently dropped): M14 + M15 + M16 together now cover every UI-track capability the roadmap
  named (projection, control surface, authoring), so v1.0 looks earned on the same terms the Flow
  spec itself reached v1.0 on — not "every hypothetical covered," but "no known gap blocking
  current capabilities." Conversation/live-streaming views (blocked on Case 2 multi-model workers)
  and scheduling simulation/cost display (spec "may"s with no concrete need naming them) stay
  deliberately unassigned to any milestone either way (Phase 5).

**M15: UI Control Surface** — the second UI-track milestone: every §7 user action — start/resume
a workflow, Approve/Reject, Retry-with-revision, Send-back, and Cancel (targeted and host stop) —
exposed in `Aer.Ui` exclusively through Flow's mutation interface, via in-process reuse of the CLI
command layer, mapped onto Flow's closed `DecisionType` set, and proven by UI-driven round trips
over shell-stub workers on all three CI OSes in default CI.

- ✅ Phase 1 — Mutation seam + start/resume a workflow (#137)
- ✅ Phase 2 — Resolve decisions: Approve / Reject (#138)
- ✅ Phase 3 — Artifact-carrying decisions: Retry-with-revision + Send-back (#139)
- ✅ Phase 4 — Cancel: targeted live-execution cancel + host stop (#140)
- ✅ Phase 5 — UI-driven mutation round trips in default CI (#141)

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
- **The supplementary-artifact worker role and output name are asked for, never inferred or
  defaulted** — `WorkerBinding.NonProcess` is constructed directly from these two strings (M12
  Phase 3's decision of record: never looked up in the bindings file), and no snapshot-declared field
  names an expected value for either, so `PausedStepViewModel.SupplementaryWorker`/
  `SupplementaryOutputName` are the same "ask, don't infer" discipline as the bindings/template file
  paths, just promoted into the MVVM layer Phase 2 introduced rather than a named code-behind control
  — a paused step is a dynamically-templated `ItemsControl` row, not a fixed named control (Phase 3).
- **`DecideDelegate` replaced the three-argument decide callback**, carrying `TargetStepId` and the
  supplementary-artifact triple (`RevisionFilePath`/`SupplementaryWorker`/`SupplementaryOutputName`,
  `null` together whenever no artifact rides the decision) alongside the original
  `StepId`/`ExecutionId`/`DecisionType`. `MainWindow`'s private `DecideAsync` is the one place that
  runs the `aer supply` → `aer decide` two-call round trip M12 Phase 3 established for the CLI: it
  mints/populates/settles the supplementary execution first (only when a revision file path is
  non-null) and passes the resulting `ExecutionId` to `DecideCommand` as `SupplementaryExecutionId`
  — both calls share one `IsMutationInFlight` window and one poller start, since together they are
  one user-facing action, not two (Phase 3).
- **Retry's supplementary artifact is optional, Send-back's is mandatory — enforced by each
  command's own `CanExecute`, never by letting an incomplete call reach the mutation interface.**
  `PausedStepViewModel.CanRetry` allows a blank revision file (Retry proceeds with no supplementary
  artifact) but requires the worker/output-name pair *together* with a non-blank one, so a half-filled
  triple can never reach `aer supply` with an empty string argument. Every `SendBackTargets` entry's
  `CanSendBack` requires all three fields unconditionally — §17.2 defines a `Supersede` without a
  `SupplementaryExecutionId` as itself invalid, so the UI never offers a submittable button until one
  is guaranteed (Phase 3).
- **"Send back to X" is a small child view model (`SendBackTargetViewModel`) per declared
  `PausePoint.SupersedeTargets` entry, not a single parameterized command on `PausedStepViewModel`.**
  One object per target keeps the `ItemsControl` binding simple (`Command="{Binding SendBackCommand}"`
  needs no `CommandParameter` threaded through a nested template); it reads the shared
  `RevisionFilePath`/`SupplementaryWorker`/`SupplementaryOutputName` directly off its owning
  `PausedStepViewModel` rather than duplicating them per target, since a paused step has exactly one
  supplementary artifact in flight regardless of which target eventually consumes it. An empty
  `SendBackTargets` list (no declared targets) renders no send-back option at all — never
  offered-then-failed at the mutation interface (Phase 3).
- **`SendBackTargetViewModel`'s `SendBackCommand` `CanExecute` re-evaluation is pushed manually, not
  via `NotifyCanExecuteChangedFor`** — that attribute only reaches commands generated on the *same*
  class, and each target is its own `ObservableObject`. `PausedStepViewModel` calls
  `NotifyCanExecuteChanged()` on every target's `SendBackCommand` from `On<Field>Changed` partial
  methods whenever `RevisionFilePath`/`SupplementaryWorker`/`SupplementaryOutputName`/`IsEnabled`
  changes (Phase 3).
- **`SupplyCommand`'s `FileNotFoundException` (a plain BCL exception, not `AerFlowException`) is
  caught alongside `AerFlowException` in `MainWindow.DecideAsync`** — a mistyped revision file path
  is exactly the kind of input error the in-window-message precedent (M14 Phase 1) exists for, and
  letting it propagate uncaught would crash the window, the one thing that precedent forbids (Phase 3).
- **`RunCommand.ExecuteAsync`/`DecideCommand.ExecuteAsync` gained an additive, optional
  `InFlightExecutionRegistry? inFlightExecutions = null` parameter**, forwarded unchanged to
  `MutationInterface.StartWorkflowAsync`/`RecordDecisionAsync` — the signature change the phase plan
  named as sitting on the critical path. `null` for every existing caller (the CLI included, which
  still lets `MutationInterface` default one internally); `Aer.Ui.MainWindow` is the first caller with
  a reason to retain one (Phase 4).
- **`MainWindow` retains a fresh `InFlightExecutionRegistry` and a host-stop `CancellationTokenSource`
  (linked to the caller's own token) per `RunAsync`/`DecideAsync` pump call**, cleared in that same
  call's `finally` — the same call-scoped lifetime the registry itself already has inside
  `MutationInterface`, just now reachable from outside it. `DecideAsync`'s `aer supply` half shares
  the same host-stop token as its `aer decide` half, since together they are one user-facing action
  (Phase 3's own precedent), even though only the decide call ever registers a process dispatch
  (Phase 4).
- **`MainWindowViewModel.RunningExecutions` (`RunningExecutionViewModel`) is the §7 targeted-Cancel
  surface**, rebuilt from `TaskProjection` on every load exactly like `PausedSteps` — one entry per
  step whose latest attempt is `StepStatus.Running`, plus one per pending step-less/human execution
  (`FlowState.StepLessExecutions`), both valid `RequestCancellationAsync` targets. Two-phase
  reflection (§7) reuses `FlowState.CancellationRequestedExecutionIds` directly rather than adding a
  new UI-owned field — the Flow layer already derives exactly that fact (Phase 4).
- **`IsLocallyHosted` is derived once, at render time, from whether `MainWindow`'s own retained
  `InFlightExecutionRegistry` is currently driving the exact task directory being rendered** — never
  a per-execution registry membership check (which would need `InFlightExecutionRegistry`'s internal
  `RegisteredExecutionIds()` visible outside `Aer.Flow`). Since only one mutation can be in flight
  from this process at a time (the shared `IsMutationInFlight` flag, Phase 2's decision of record),
  "this window's retained registry is non-null and its task directory matches" is unambiguous. A
  step-less/non-process execution is never locally hosted: it never registers with
  `InFlightExecutionRegistry` in the first place (Phase 1's `NonProcessCancellationDetector` finalizes
  it directly, in-round) (Phase 4).
- **Targeted Cancel delivery is a two-way split, `MainWindow.CancelExecutionAsync`'s own decision, not
  offered as a single always-available button**: a locally-hosted execution is signalled in-process via
  `InFlightExecutionRegistry.RequestCancellationAsync` — fast, idempotent, no new mutation call, since
  §15's guard is already held for that pump's entire duration (M10's decision of record). Anything
  else is the only remaining path: a brand-new `Aer.Cli.CancelCommand` mutation call, wrapped exactly
  like `RunAsync` wraps `RunCommand`, including a possible `WorkflowLockedException` from whatever
  process (or pump) currently holds the task's lock — rendered via the in-window-message precedent
  (M14 Phase 1), never a button that pretends to work (the phase's own named open question) (Phase 4).
- **`RunningExecutionViewModel`'s enabled state is the one deliberate exception to the shared
  `IsMutationInFlight` gate**: a locally-hosted execution's Cancel command stays enabled *exactly*
  while `IsMutationInFlight` is true, since that is the only time signalling it is meaningful at all;
  every other entry (not locally hosted) follows the same `!IsMutationInFlight` rule
  `PausedStepViewModel`/`RunButton` already do. `RunningExecutionViewModel.UpdateEnabled` encodes this;
  `MainWindowViewModel.OnIsMutationInFlightChanged` calls it for every entry, the same fan-out
  `PausedSteps` already gets (Phase 4).
- **`MainWindow.StopAsync` (bound to the new `StopButton`) only cancels the retained host-stop
  `CancellationTokenSource` — it does not itself await the pump.** `RunAsync`/`DecideAsync`'s own
  already-awaited pump task is what actually drives §9's intent-first record for every execution still
  in flight and clears `IsMutationInFlight` once `MutationInterface`'s existing host-stop machinery
  (M10 Phase 2) reaches its fixed point; `StopAsync` is fire-and-forget by design, mirroring
  `Aer.Cli.Program.cs`'s `Console.CancelKeyPress` handler, which is exactly as thin (Phase 4).
- **Window-close semantics: the first `Window.Closing` while a pump is in flight is deferred
  (`e.Cancel = true`), triggers the same host stop, and the window closes for real only once the
  retained pump task has settled.** A `_closeConfirmed` flag distinguishes that second, programmatic
  `Close()` from the first user-initiated one so the stop sequence never re-enters. This is the
  "Ctrl+C equivalent" applied to the one exit path a GUI has that a terminal's SIGINT handler doesn't:
  closing the window mid-pump is never a silent abandonment of a live execution (Phase 4).
- **No new gate mechanism was needed — the milestone's three named round trips already existed as
  each earlier phase's own end-to-end proof.** `MainWindowDecisionTests.Approve_resolves_the_pause_...`
  (Phase 2) *is* run → pause → Approve → terminal; `MainWindowRetryAndSendBackTests.Send_back_offers_
  only_declared_SupersedeTargets_...` (Phase 3) *is* pause → supply + Send-back → invalidation cascade
  → terminal; `MainWindowCancelAndStopTests.Targeted_cancel_of_a_locally_hosted_execution_...`
  (Phase 4) *is* running → targeted Cancel → cancelled — each already driving the real `MainWindow`
  through a deterministic shell-stub `IWorkerAdapter`, never a live vendor CLI. Writing a duplicate
  Phase-5-named test class over the same three scenarios would be ceremony, not coverage. The
  `ShellCommandWorkerAdapter`-placement question the phase plan named was likewise already settled,
  in Phase 1: `Aer.Ui.Tests` grew its own copy (`TestSupport/ShellCommandWorkerAdapter.cs`) rather
  than sharing `Aer.Cli.Tests`'s, because Phase 1's own `MainWindowRunTests` needed a stub registry
  immediately and this project's established convention (`ShellWorkerCommands`'s own remarks) is to
  own its minimal shell-stub set rather than reach into another test project's `TestSupport`.
- **"Wired into default CI" needed no new CI step, because `Aer.Ui.Tests` already is one.** It has
  been a leaf in `AerFlow.slnx` since M14 Phase 1, so `pixi run test`'s plain `dotnet test` already
  runs it — headless, offscreen, no display server — on all three of `ci.yml`'s matrix OSes
  (win/linux/mac) on every PR and every push to `main`, the same unattended placement M13 Phase 4
  and M14 Phase 5 established for gates that need no live vendor auth. Verified green end to end for
  this phase: `dotnet build -warnaserror` (lint), `dotnet format --verify-no-changes` (fmt-check),
  and the full `dotnet test` run — all four `AerFlow.slnx` test projects, 480 tests total, including
  every M14 Phase 5 golden-projection fact — pass with no changes to `Aer.Flow`, `Aer.Adapters`, or
  `Aer.Cli`: the control surface added mutation callers across Phases 1–4, never projection semantics
  (Phase 5).

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
- ~~**UI spec maturity (v0.9 vs. the flow spec's v1.0)**~~ — resolved at M16 completion, during M17 planning: promoted to v1.0 (`spec/aer-flow-ui-behavioral-spec-v1.0.md`), no behavioral changes, on the same terms the Flow spec reached v1.0 — projection, control surface, and authoring cover everything the spec names for those surfaces, and no known gap blocks a current capability. Post-1.0 gaps keep resolving by amendment (the Flow spec's own §11.1 was amended post-1.0 during M16 planning); this list stays the ledger.
- **The transcript artifact contract (UI spec §10 vs. the worker boundary)** — §10 requires every visible conversation step to correspond to durable artifacts, but no spec names the dialogue transcript's schema or its owner — and the Flow spec deliberately *can't* (nothing above the worker boundary may depend on worker internals, §18.2). M17 Phase 2 defines the schema as worker-owned documentation; whether the UI spec names that schema — and how the conversation view discovers that an execution has a transcript at all — is M18 planning's first gap to settle, via a spec PR before the view consumes it.
- ~~**Template version-increment ownership (Flow spec §11.1)**~~ — resolved, during M16 planning (before Phase 1/#150, which would otherwise have had to decide it mid-implementation): `WorkflowTemplateVersion` is ordinary template data Flow only copies into the snapshot at instantiation, never computes or enforces; incrementing it is the editor's (or a hand-editor's) responsibility, on every content-changing save, never on a no-op save, never at finer granularity than a save.
- ~~**The worker-bindings file vs. the UI write model (UI spec §4 vs. §9)**~~ — resolved, during M16 planning (before Phase 4/#153): §4's write list now names worker-bindings configuration files directly (UI spec v0.9), closing the gap against §9's pre-existing "edit worker bindings" grant.

## Notes for future work

- **A third worker adapter (`Aer.Adapters`)** — Claude shipped in M11 Phase 2 (#85), Gemini/`agy` in M12 Phase 1 (#95). Before adding another vendor, read closed spike [#21](https://github.com/aer-works/aer-flow/issues/21)'s recorded findings — stdin stalls, permission-flag vocabularies, and path-interpolation behavior differ per CLI and are exactly what the adapter seam exists to absorb. (M17 Phase 4's dialogue adapter is not this note's case: it spawns aer's own dialogue worker executable, not another vendor's CLI — the vendor quirks stay inside the worker, which inherits them from the two existing adapters' recorded findings.)
- **Whether MVVM spreads beyond the decision surface** — M15 Phase 2 (#138) deliberately scoped `CommunityToolkit.Mvvm` to the paused-step Approve/Reject buttons, the first *interactive, stateful* control surface (enabled state tied jointly to projected state and an in-flight mutation). The DAG/history/lineage/diff rendering stayed code-behind on purpose: it's one-directional (projection → controls, nothing to bind against), so a ViewModel there would be ceremony with no payoff. Phase 3 (Retry-with-revision, Send-back) and Phase 4 (Cancel) add more of the same interactive shape, so expect the ViewModel layer to grow phase over phase rather than needing a deliberate decision to introduce it again. Revisit whether the read-only surfaces are worth converting too only if M16 (Authoring) needs two-way binding there — not preemptively.
