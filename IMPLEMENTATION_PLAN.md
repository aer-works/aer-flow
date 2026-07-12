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

## M12: Full Control Surface — Phase Plan

**Goal:** make the runnable library *drivable*: a second vendor (Gemini's `agy`) behind the M11 protocol unchanged, and the mutation surface M9/M10 built — external decisions (§17) and cancellation (§9) — exposed as `aer decide` / `aer cancel` (§14; UI spec §7), proven by a live mixed-vendor paused run decided from the terminal (§18.1). M11 made the library runnable but left the control surface start-only: `Program.cs` dispatches exactly one subcommand, and even Ctrl+C reaches nothing — the pump gets a default cancellation token, so the in-flight cancellation surface M10 Phase 2 built is still un-wired to any CLI. After M12, every user action the UI spec enumerates (§7) exists as a CLI command; the eventual UI becomes a renderer over a control surface that already works.

Three facts shape the plan. First, **nothing here changes the engine.** `RecordDecisionAsync` and `RequestCancellationAsync` have existed and been tested at the `MutationInterface` layer since M9/M10, and the adapter seam was designed (M11 Phase 1) so a second vendor is one class plus a registry entry. M12 is edges: one adapter, the CLI wiring, and the pause-aware output a terminal user needs to know which `ExecutionId` to decide on. Second, **the second vendor's quirks are already recorded.** Spike #21: `agy` needs three accommodations — `--mode accept-edits` (its permission flag; no shared vocabulary with Claude's `--allowedTools`), `--add-dir` grants for every directory it touches (it ignores the invoking process's cwd entirely, working from its own scratch dir), and absolute paths in the prompt text. The third is already solved by the M11 mechanism (shell-expanded `$AER_INPUT_<n>`/`$AER_OUTPUT_DIR` references in the wrapped command); the second raises the milestone's one genuinely open mechanism question — input-directory grants (Phase 1). Third, **the cancellation surface splits in two by construction**: §15's guard is held for a mutation call's whole duration (M10's decision of record), so a separate `aer cancel` process can never reach an execution a live pump has in flight — live delivery is Ctrl+C on the pump's own host, while `aer cancel` serves idle tasks (crashed-pump orphans, pending non-process executions). The plan wires both paths instead of pretending one command covers them.

### Phase dependency table

| Phase | Requires output from |
|---|---|
| 1 — Gemini worker adapter (headless `agy` CLI) | — (implements M11 Phase 1's seam, unchanged) |
| 2 — `aer cancel` + Ctrl+C host-stop wiring | — (exposes M10's mutation surface; establishes the mutation-command CLI plumbing) |
| 3 — `aer decide` + supplementary artifact recording | 2 (the plumbing it extends) |
| 4 — Live mixed-vendor paused run (gated end-to-end) | 1 + 3 |

Phases 1 and 2 are deliberately independent — either can land first. The adapter goes first among equals because it holds the milestone's only external unknown; the CLI phases are wiring over an engine surface tested since M9/M10.

### Phase 1 — Gemini worker adapter (headless `agy` CLI) (#95)
The second real adapter — and the test of M11 Phase 1's claim that adding a vendor changes no engine or protocol code. `GeminiWorkerAdapter` builds the `CoreDispatchTarget` for a headless `agy` invocation honoring the facts spike #21 recorded (read them first): Gemini's scoped-permission flag is `--mode accept-edits` (coarser than Claude's per-tool `--allowedTools` — further proof `PermissionScope` must stay an opaque, adapter-interpreted string), and `agy` ignores the invoking process's cwd, so every directory it touches needs an `--add-dir` grant and every path must reach the prompt as an absolute path. The absolute-path half is already solved by the M11 shell-wrap mechanism: config-authored text escaped, live `$AER_INPUT_<n>`/`$AER_OUTPUT_DIR` references left for the shell to expand into real per-execution paths at spawn time, before `agy` ever sees the prompt. #21's other finding — `agy` returning exit 0 having written nothing — is not a special case: `ContractValidator` already reads a missing declared output as a retryable contract failure (§8 → §10), exactly as M11 Phase 2 framed it for Claude.

**Produces:** the constructed command / args / prompt asserted by unit tests mirroring `ClaudeWorkerAdapterTests`; the adapter registered in `WorkerAdapterRegistry.Default`. No live API call in CI (Phase 4's gate).
**Excludes:** the CLI mutation commands (Phases 2–3); live runs (Phase 4).
**Open questions resolved in this phase:**
- **How input directories are granted.** A step's inputs live in sibling `artifacts/execution_{N}/` directories of *upstream* executions, outside `$AER_OUTPUT_DIR`, so `--add-dir "$AER_OUTPUT_DIR"` alone cannot cover reads. Candidates: per-input `--add-dir` grants derived in the shell wrapper (`dirname`-style — needs a Windows answer), or a vendor-neutral per-dispatch environment variable for the task's artifacts root (an `ArtifactManager` addition — vendor-neutral, so no isolation violation). Whichever wins must stay per-dispatch-dynamic through the same env-expansion route, since `IWorkerAdapter.Resolve` runs once per binding, never per execution.
- **The registry key** (`"gemini"` — the vendor, as `"claude"` is — vs. the binary name `"agy"`), and whether `agy` also gets the stdin redirect (#21 recorded no stall for it; the shell wrapper is there either way).

### Phase 2 — `aer cancel` + Ctrl+C host-stop wiring (#96)
§9's cancellation surface exposed on the CLI, split along M10's decision of record. `aer cancel <task-dir> --execution <id> --bindings <config>` calls `MutationInterface.RequestCancellationAsync`: intent recorded first (`CancellationRequested` fsync'd before anything is signalled), and a request against an already-terminal execution reported as §9 step 4's recorded too-late no-op — success, not an error. The command is itself a pump: after recording, it drives project → resolve → dispatch → await to a fixed point like every mutation entry point (§21), which is why it requires bindings at all. And because §15's guard blocks a second process for a live pump's whole call, in-flight delivery is wired where M10 put it: `Console.CancelKeyPress` on the `aer run` host feeds the ambient token, whose firing records `CancellationRequested` for everything in flight before signalling any of it. This phase also establishes the mutation-command plumbing Phase 3 extends: `Program.cs` subcommand dispatch, positional task dir, snapshot loaded from the task dir's existing `snapshot.json` (typed error when absent — a mutation command never binds fresh, per §11.2), the same `CliArgumentException` parser conventions and single `AerFlowException` boundary as `aer run`.

**Produces:** `aer cancel` end-to-end against idle tasks, and Ctrl+C stopping a live run — both CI-safe through shell-stub adapters, mirroring `RunCommandEndToEndTests`; engine cancellation semantics stay proven at the `MutationInterface` layer (M10), not re-proven.
**Excludes:** `aer decide` (Phase 3); live vendors (Phase 4).
**Open questions resolved in this phase:**
- **What cancel-blocked-by-live-pump looks like.** `WorkflowLockedException` surfacing with a message that names the resolution (Ctrl+C on the running pump) rather than a bare "locked" — the split is by construction, so the CLI should teach it.
- **Exit-code vocabulary for mutation commands** — `aer run`'s 0/1/64 convention generalized to commands whose outcome is a recorded mutation plus a resulting state (including "recorded, but too late").

### Phase 3 — `aer decide` + supplementary artifact recording (#97)
UI spec §7's reference mapping made real on Phase 2's plumbing: `aer decide <task-dir> --execution <referenced-id> --type resume|reject|retry-with-revision|supersede [--target-step <step>] [--supplementary <execution-id>] --bindings <config>` → `MutationInterface.RecordDecisionAsync`. The vocabulary is §17.2's closed set, exposed exactly — no additions, no interpretation (UI spec §6 binds any client, a terminal included); every validity rule stays `ExternalDecisionValidator`'s. Recording a decision resumes the workflow — `ExternalDecisionRecorded` + `WorkflowResumed`, then the pump to a fixed point — so the command blocks like `aer run` and reports the same way. Alongside it, pause-aware reporting: `aer run`/`aer decide` output surfaces paused executions with their `ExecutionId`s, paused outcomes, and declared `SupersedeTargets`, without which a terminal user cannot know what to pass to `--execution`/`--target-step`.

**Produces:** all four decision types driveable from the terminal, CI-safe end-to-end (pause → decide → fixed point, including the supply → decide-`Supersede` round trip), mirroring `RunCommandEndToEndTests`; decision semantics stay proven at the `MutationInterface` layer (M9), not re-proven.
**Excludes:** live vendors (Phase 4).
**Open questions resolved in this phase:**
- **How a terminal user records the §17.3 supplementary artifact** that `Supersede` requires and `RetryWithRevision` optionally takes. `RecordSupplementaryExecutionAsync` has existed since M9 but is reachable from no CLI. The phase decides the surface — e.g. an `aer supply`-style subcommand that mints the step-less execution, reports (or directly populates) the assigned output path, and prints the `ExecutionId` to hand to `aer decide` — and how the non-process `human` binding is constructed: directly by the CLI, per M11's decision of record that worker-binding configs only ever produce `Process` bindings.
- **Whether `--execution` may be inferred** when exactly one pause is outstanding, or stays always-explicit (determinism and scriptability favor explicit; the pause-aware output makes explicit cheap).

### Phase 4 — Live mixed-vendor paused run (gated end-to-end) (#98)
The M12 completion gate, following M11 Phase 4's pattern exactly. A real draft (Claude) → review (Gemini/`agy`) workflow — §18.1's composition case, the original goal the project was built for — run live through `aer run`, pausing at a declared `PausePoint` on the review step, resumed by a real `aer decide` from a terminal, driving to terminal success with real artifacts from both vendors on disk. Lives in `Aer.Cli.SmokeTests` (outside `AerFlow.slnx` — default CI never sees it) behind a key-gated `pixi run` smoke task alongside `smoke-claude`, with the runbook extended to cover both vendors' prerequisites and triage. Asserts output existence and non-blankness only, never exact worker text.

**Produces:** M12 complete — the full control surface is real: two vendors behind one unchanged protocol, a paused live run decided from the terminal.
**Excludes:** distribution/packaging (M13); the UI (v0.7 spec, separate track).

---

## Current Milestone

**M12: Full Control Surface** — phase plan above. Progress:

- ⬜ Phase 1 — Gemini worker adapter (headless `agy` CLI) (#95)
- ⬜ Phase 2 — `aer cancel` + Ctrl+C host-stop wiring (#96)
- ⬜ Phase 3 — `aer decide` + supplementary artifact recording (#97)
- ⬜ Phase 4 — Live mixed-vendor paused run (gated end-to-end) (#98)

Decisions of record accrue here as phases land.

## Completed Milestones

Completed milestones keep only their phase checklist and any decisions of record later work
still leans on. The full phase plans — goals, boundaries, and the open questions each phase
resolved — live in this file's git history and in the linked issues.

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

## Notes for future work

- **Worker adapter implementation (`Aer.Adapters`)** — the **Claude** adapter shipped in M11 Phase 2 (#85); the **Gemini** adapter (`agy` — antigravity, Google Gemini's CLI) is M12 Phase 1 (#95), with the facts closed spike [#21](https://github.com/aer-works/aer-flow/issues/21) recorded folded into its phase plan above. Read #21's findings before starting it.
