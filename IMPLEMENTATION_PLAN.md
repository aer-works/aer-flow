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

**Goal:** the product a non-expert can drive end to end, looking and feeling like a premium
product (the owner's stated bar at M18's completion): a task-first, plain-language UI where
every path runs through pickers, forms, and guided flows — never a typed path, a raw JSON
textarea, or a hand-edited config file — organized the way CyboFlow organizes attention: a
central view grouped by what needs the human next, the workflow graph as the primary
visualization, review surfaces one click away. "Premium" is planned, not vibes: Phase 1 defines
the design language (tokens, type, motion, and a reference bar), Phases 2–4 build with it from
day one so nothing ships default-themed, and Phase 5 is the polish pass whose completion
includes the owner's design sign-off — a human gate, like every live-run gate before it.
Everything the UI does today survives; what changes is organization, language, authoring
ergonomics, and finish.

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
disclosure — advanced detail exists but is never the entry path); the information architecture
(Home with task cards + the decision inbox, Task view, Author view); and the **design language**
— the premium bar made concrete rather than left as vibes: a reference set the owner signs off
on (the products this should feel like), design tokens (type scale on the bundled Inter, a
spacing grid, light + dark color tokens, radii/elevation), and motion principles (view
transitions, hover/pressed states, live status changes, loading states). Phase 2 materializes
the tokens as a shared theme resource; Phase 5 is judged against this document.

**Produces:** the UX principles + IA + design language doc; the audited requirements list.
**Excludes:** any code (Phases 2–5).

### Phase 2 — Navigation shell: Home, Task, and Author views (#187)
Replace the single stacked-sections window with the Phase 1 IA: a navigation shell whose Home
lists recent task directories as live status cards (rebuilt from durable contents per §3.1; the
recents list stays Local UI Configuration) with a decision inbox — everything across recent
tasks currently waiting on the human, grouped and one click from acting. A behavior-preserving
re-home of every existing surface: all headless round trips re-pointed and green.

**Constraint (the remote-ready seam, compiler-enforced):** the shell is built against a clean
read-model + mutation-interface seam — views render projections and raise intents; no new view
does file I/O or pump hosting in code-behind — and the seam is held by the type system, not
discipline: the presentation-agnostic layer moves to a new Avalonia-free project,
**`Aer.Ui.Core`** — the projection loaders/projectors (`TaskProjection`, `ExecutionHistory`,
`ArtifactLineage`, transcript, DAG layout, snapshot/template diff, bindings/template loaders)
and the task-session orchestration that today lives in `MainWindow` code-behind (open/refresh,
pump hosting with the retained `InFlightExecutionRegistry`, the mutation-interface calls) —
leaving `Aer.Ui` as the Avalonia skin over it. Not speculative generalization: the boundary is
already M14's decision of record (presentation-layer, deliberately not engine-owned); the split
only makes the compiler enforce it, and it lands *as part of* the re-home — each surface
migrates once, into its new home and behind the seam, never a separate refactor pass. The future
remote host (candidate M20) references `Aer.Ui.Core` and never `Aer.Ui`. This phase also
materializes Phase 1's design tokens as the shared theme resource every new surface consumes —
nothing ships default-themed even mid-milestone. Same process, same behavior throughout.

**Constraint (the re-home completes the MVVM migration; owner directive 2026-07-17):** M15
Phase 2 deliberately scoped MVVM to the decision surface and editors, leaving read-only
rendering as imperative code-behind and deferring the migration until a real need arrived —
this re-home is that need, and "each surface migrates once" forbids re-homing a surface
code-behind only to MVVM-ify it later. Each view gets a ViewModel in `Aer.Ui.Core`
(CommunityToolkit.Mvvm, the established stack); imperative `Children.Add` rendering becomes
observable item ViewModels rendered by XAML DataTemplates with compiled bindings; code-behind
shrinks to view wiring and the genuinely visual (the DAG canvas, until Phase 3/5 make it a
custom control). The seam gains from it directly: a remote client reuses the ViewModels, not
just the loaders. Deliberately *not* adopted: a DI container — constructor-injected seams
without a container remain the right size for this app.

**Produces:** the shell; `Aer.Ui.Core`; every M14–M18 surface reachable in its new home,
rendered through the seam with Phase 1's tokens.
**Excludes:** surface redesigns (Phase 3), authoring changes (Phase 4), polish beyond the base
tokens (Phase 5); any network/remote capability (the seam is in-process — remote is a future
milestone's question).
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
The premium pass, styling the final shape against Phase 1's design language: a custom control
theme over default Fluent (nothing ships looking like a stock developer tool), the DAG rendered
as a first-class visual (smooth edges, real node styling, status carried by the one color/icon
system used everywhere), motion (view transitions, hover/pressed states, animated live-status
changes, loading states), dark + light themes from the same tokens, empty states that say what
to do next, window chrome and app icon, high-DPI crispness.

**Produces:** the finished look; every surface visually consistent and to the bar.
**Excludes:** any behavior change.
**Gate:** behavior tests stay green as always, but look-and-feel is not CI-assertable — this
phase completes only on the owner's design review against Phase 1's reference set, recorded as
a human gate exactly like every live-vendor gate before it.

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

- ✅ Phase 1 — UX baseline, principles, and information architecture (#186)
- ✅ Phase 2 — Navigation shell: Home, Task, and Author views (#187)
- ✅ Phase 3 — Task view, human-first (#188)
- ✅ Phase 4 — Guided authoring: no hand-edited config files (#189)
- ✅ Phase 5 — Visual design pass (#190) — implementation complete; the phase's human gate (the
  owner's design review against the Phase 1 reference set) is folded into the owner's
  post-milestone overall review, per the owner's 2026-07-17 direction.
- ✅ Phase 6 — Gate: the non-expert path in default CI (#191)

Per this document's session prompt: help implement the current phase only.

**Decisions of record (Phase 1):**

- **The reference set is the owner's, adopted verbatim (2026-07-17)** — Linear (inbox), GitLab
  (to-dos), Dagster (Launchpad), Stately.ai (visual↔config sync), GitButler and Neovim/Neovide
  (polished skin over presentation-agnostic core), n8n (DAG rendering), Raycast (chrome/tokens/
  keyboard gold standard), each mapped to the phase it informs in `docs/ux/design-language.md`.
  Changing the set is an owner decision, not an implementation one; Phase 5's human gate is
  judged against it.
- **One product, not a collage (owner directive, 2026-07-17)** — the references calibrate the
  bar, they never supply the look: AER Flow has one identity of its own, and stitching together
  surfaces that each resemble their reference is the named failure mode. The identity is carried
  by the token system + status system + motion rules + vocabulary — defined once in
  `design-language.md` and implemented by every client (desktop now, remote/web later), so all
  surfaces read as the same product wearing different windows. Phase 5's review question is
  two-sided: holds up beside the reference *and* unmistakably the same product throughout.
  Corollaries: fit over fidelity (skip what doesn't suit the domain, without apology), and mine
  the references for capabilities, not just polish — a standout affordance we lack is adapted
  to our identity and folded into its natural phase, or surfaced to the owner if it would grow
  scope.
- **The vocabulary map is total for primary text and never renames semantics** — a spec term in
  a label/button/status line is a defect (Phase 6 checks for it), and a plain word that would
  imply behavior the engine doesn't have is wrong, not the engine ("send back" *is* supersede,
  mandatory feedback artifact included). Spec terms survive in tooltips/disclosure for §12
  traceability.
- **The non-expert audit generated zero engine requirements** — all twelve rows
  (`docs/ux/non-expert-audit.md`) are organization, language, or authoring ergonomics,
  confirming the plan's first shaping fact. Two audit findings bind later phases beyond the
  plan's text: Phase 4 owes a **vendor-readiness surface** (read-only presence check, "Claude:
  available / Gemini: not found", never credential handling), and Phase 3's bindings pre-fill
  is recorded as **convenience, never remembered authority** (a visible, swappable picker
  default — §4's input-not-authority stance preserved).
- **Tokens are a system, not values** — `design-language.md` fixes the token names, scales, and
  rules (semantic color only, 4px grid, two radii, three motion durations, status always
  color+icon+word); exact values are fixed when Phase 2 materializes the theme resource. A
  surface using a raw hex/size/pixel literal after Phase 2 is a defect.

**Decisions of record (Phase 2):**

- **The MVVM-completion constraint executed as written** — every re-homed surface's logic lives
  in an `Aer.Ui.Core` ViewModel (CommunityToolkit.Mvvm source generators, compiled bindings);
  `MainWindow` code-behind shrank to shell wiring plus a **transitional facade** of delegating
  control properties, kept deliberately so the not-yet-rebuilt rendering paths and every
  existing headless test compile unchanged — Phases 3–4 retire facade entries as they rebuild
  each surface properly, and a facade entry surviving past its surface's rebuild is a defect.
  No DI container, as recorded in the constraint.
- **The inbox scans all recent task directories** (the phase's named open question) — Home
  exists precisely for the moment no task is open yet; an inbox that only knew the open task
  would be empty exactly when it matters most. Bounded by the store-capped recents list;
  refreshes on Home activation plus the open task's poller tick — never its own timer.
- **§3's stale-recents rule renders as the greyed `Unavailable` card** — a recent that no
  longer loads stays visible ("Not available — moved, deleted, or not a task") with no inbox
  items and no live status: reflected, never an error, never silently pruned — the user
  recorded that history, and hiding it would misreport it.

**Decisions of record (Phase 3):**

- **The drill-in is a pure re-slice; the full record stays one disclosure away** — the per-step
  tabs (attempts / outputs / conversation / decisions) assert nothing the task-level panels
  don't already render; those precise, spec-vocabulary panels moved intact into a single
  collapsed **Details** expander (§12 traceability; ux-principles' progressive disclosure), not
  deleted. Their facade entries therefore stay until the phase that actually rebuilds them away
  — the retirement rule binds per surface, and these surfaces became the disclosure layer.
- **One decision authority, inline** — the needs-you-first decision cards bind the *same*
  `PausedStepViewModel` instances M15's decision surface rebuilds; the drill-in's paused step
  holds a reference, never a copy. Plain words on the buttons (Approve / Retry this step /
  Reject / Send back), spec terms in the tooltips.
- **The feedback-file picker is convenience over the same visible property** — the OS file
  dialog writes `RevisionFilePath`, the property the text box still binds and headless tests
  still set; a picker cannot be driven headlessly, and the path stays visible and swappable
  (the audit's convenience-never-authority stance, applied to files).
- **Live-follow needed no new mechanism** — the M15 2-second poller already refreshes while
  the open task is non-terminal, and M18's conversation selection re-renders on every load;
  "on by default" was already the recorded behavior, now confirmed as the phase requirement.

**Decisions of record (Phase 4):**

- **Authored files live in a UI-managed default workspace** (the phase's first named open
  question) — `Documents/AER Flow/<workflow-name>`, visible in the flow and swappable via a
  folder picker, never required; task directories are created inside it per run
  (`task-<timestamp>`). Explicit-everywhere would put a path decision back at the start of the
  non-expert's very first action — the exact failure the audit's walkthrough recorded.
- **Vendor invocation knowledge lives with its owner, never in the UI** (the second named open
  question) — dialogue participants' command shapes moved from per-smoke-test duplication into
  `DialogueParticipantPresets` (Aer.Workers.Dialogue: the worker that invokes them), the
  read-only PATH probe lives in `VendorCliPresence` (Aer.Adapters: the layer that owns binary
  names), and the guided flow's presets pass **no explicit PermissionScope** — the adapter's own
  default governs, explained in plain words. The UI re-encodes nothing spike #21 isolated.
- **The readiness surface is informational, never a gate** — "Claude: available / Gemini: not
  found", refreshed on Author activation; nothing in the save/run path reads it (the audit's
  vendor-readiness finding, delivered read-only as recorded).
- **Guided save goes through the editors' own writers** (`WorkflowDefinitionWriter`,
  `WorkerBindingConfigWriter`) — guided output and hand-authored files can never diverge in
  format, and the M16 editors remain in the Author view as the advanced disclosure (the Phase 3
  Details pattern, applied to authoring).

**Decisions of record (Phase 5):**

- **Property-level restyling over Fluent's templates, not template replacement** — the custom
  control theme re-skins every stock control through token-valued property setters (color,
  radius, padding, focus, per-state brushes with Motion.Fast transitions); Fluent's control
  *shapes* are kept. Full re-templating buys nothing the identity needs and re-owns behavior
  (focus, accessibility) the framework already gets right.
- **Status renders as border + tint, never fill-only** — the `Status.*Bg` tint tokens joined
  both theme variants; the DAG node carries its status as a colored border over a tinted
  surface with the status word in the label (color+icon+word discipline, DAG tests re-pointed
  from named framework colors to token lookups — the only test change the phase needed).
- **One accent button per surface** — Run, Approve, Save and run, Review, Create a workflow;
  everything else stays quiet. The nav rail marks the active section in accent.
- **The app icon is the mark, generated, committed** — a mini-DAG on the accent teal,
  multi-size PNG-in-ICO, produced by a scripted render (no design-tool dependency); regenerate
  by re-running the script if the accent ever changes.

**Decisions of record (Phase 6):**

- **The dialogue step's stub is a script at the adapter-registry boundary, not a fake vendor
  CLI** — `NonExpertPathGateTests` registers a `StubDialogueScriptAdapter` under the `"dialogue"`
  key that dispatches a local PowerShell/sh script writing a schema-valid transcript and the
  declared output directly, bypassing `DialogueWorkerAdapter`/`DialogueRunner` entirely.
  `DialogueParticipantPresets` hardcodes real `claude`/`agy` command shapes with no swap point
  reachable from the guided-authoring UI, so stubbing at that level isn't available to this test
  the way `ShellCommandWorkerAdapter` stubs the single-vendor step. This is judged acceptable
  because the live dialogue exchange itself is already M18's proven gate (`smoke-dialogue`); this
  gate's job is the UI/session/projection path around it — authoring, running, pausing, reading
  the conversation, sending back, finishing — which the stub exercises completely.
- **One headless test drives the entire non-expert path, not a suite of smaller ones** — author
  (zero hand-authored files, both a single-vendor and a dialogue step) → run → pause at the
  review gate → read the conversation through the Phase 3 drill-in → send back with a feedback
  file → resume to terminal, all through the real `MainWindow`/`NewWorkflowViewModel` controls.
  Splitting it would lose the property the gate exists to prove: that the whole path composes,
  not just its parts in isolation.
- **The walkthrough's CLI sections (§1–5) are untouched; only its UI section is rewritten** — the
  CLI surface and the durable file formats didn't change in M19, so §1–5's commands and JSON stay
  exactly as a CLI user would need them. §6 is rebuilt against Home/Task/Author and the guided
  authoring flow, split into "author it" and "watch it run, decide" — the walkthrough was the
  Phase 1 audit's source document, so keeping it accurate is the milestone closing its own loop.

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
- **Remote access (candidate M20)** — the owner's stated eventual goal: drive AER from a phone
  the way Claude Code's app remote-controls a desktop session. The architecture already divides
  the right way — the desktop must stay the host regardless (the pump is in-process with the §15
  lock per M15's decision of record, and the vendor CLIs are subscription-authenticated there,
  exactly the property to keep), while everything a thin client needs is already guaranteed:
  durable-file truth, deterministic projection (§11), one mutation interface (§14), replaceable
  surfaces (§13). The likely shape: a host process exposing the read model + mutation interface,
  and a browser/PWA client (decision inbox first — approve/send-back from anywhere is the
  value; authoring can stay desktop). Two questions gate it, both deliberate spec work, not
  code: reopening §20's no-daemon stance (a remote host is a daemon — the standing Event Store
  performance ledger entry is already waiting on the same revisit), and the new trust boundary
  (client auth for a network API; vendor credentials never leave the desktop). M19 Phase 2's
  remote-ready seam — and its `Aer.Ui.Core` split, which the host process would reference
  without ever touching `Aer.Ui` — is the enabling investment; plan the rest only when it
  becomes current.
- **Whether MVVM spreads beyond the decision surface** — M15 Phase 2 (#138) deliberately scoped `CommunityToolkit.Mvvm` to the paused-step Approve/Reject buttons, the first *interactive, stateful* control surface (enabled state tied jointly to projected state and an in-flight mutation). The DAG/history/lineage/diff rendering stayed code-behind on purpose: it's one-directional (projection → controls, nothing to bind against), so a ViewModel there would be ceremony with no payoff. Phase 3 (Retry-with-revision, Send-back) and Phase 4 (Cancel) add more of the same interactive shape, so expect the ViewModel layer to grow phase over phase rather than needing a deliberate decision to introduce it again. Revisit whether the read-only surfaces are worth converting too only if M16 (Authoring) needs two-way binding there — not preemptively.
