# AER Flow — Implementation Plan

The behavioral spec (`spec/aer-flow-behavioral-spec-v1.0.md`) is authoritative for what the system must guarantee. This document is authoritative for how we are getting there: which subsystems exist, how they group into milestones, and — for the current milestone — what the phase breakdown is.

**Session prompt:** The behavioral spec is authoritative. `IMPLEMENTATION_PLAN.md` is authoritative for sequencing. Help implement the current phase only.

---

## Capability Map

What subsystems exist, derived from the spec. Not chronological — this is architecture, not a build order.

| #   | Subsystem                            | Spec reference                                                                                                 |
| --- | ------------------------------------ | -------------------------------------------------------------------------------------------------------------- |
| 1   | **Log Manager**                      | Atomic append to `flow.jsonl`; fsync write-before-dispatch ordering                                            | §5, §7   |
| 2   | **State Projector**                  | `Project(EventStore, Snapshot) → FlowState`; causal linking by `ExecutionId`                                   | §12, §13 |
| 3   | **Template Parser**                  | Load and validate `WorkflowDefinition` from file                                                               | §11.1    |
| 4   | **Snapshot Binder**                  | Freeze template into immutable `WorkflowDefinitionSnapshot` at task creation                                   | §11.2    |
| 5   | **Dependency Resolver**              | §11.3 readiness check: condition 1 (dependency succeeded) + condition 2 (staleness via `UpstreamExecutionIds`) | §11.3    |
| 6   | **Artifact Manager**                 | Pre-allocate `artifacts/execution_{N}/`; assign immutable input/output paths before dispatch                   | §16      |
| 7   | **Core Dispatcher**                  | Emit `ExecutionRequest` to aer-core M5 binding; receive `AerEvent` callbacks                                   | §3, §12  |
| 8   | **Outcome Classifier**               | Map Core exit reason + output existence to `ExecutionSucceeded/Failed/Cancelled`                               | §8       |
| 9   | **Contract Validator**               | Assert all `ProducedOutputs` exist on disk before classifying as succeeded                                     | §8       |
| 10  | **Retry Engine**                     | On `ExecutionFailed`, generate new `ExecutionRequest` with new `ExecutionId` per `RetryPolicy`                 | §10      |
| 11  | **Mutation Interface**               | Single entry point for all external state changes; no other mutation path exists                               | §14      |
| 12  | **Concurrency Guard**                | At most one writer per task namespace; file lock (not sentinel file)                                           | §15      |
| 13  | **Pause Engine**                     | `PausePoint` handling; emit `WorkflowPaused`; idle until decision arrives                                      | §17.1    |
| 14  | **External Decision Handler**        | `ExternalDecisionRecorded`; `Resume/Reject/RetryWithRevision/Supersede`                                        | §17.2    |
| 15  | **Supersede + Invalidation Cascade** | New execution for superseded step; staleness propagates forward via §11.3 condition 2 automatically            | §17.5    |
| 16  | **Human Worker Support**             | Non-process `ExecutionRequest`; completion detected by file existence, not Core exit                           | §17.3    |

**Product layer** — subsystems beyond the v1.0 engine, from §21 (the CLI is the pump), the adapter spike (#21), and the UI spec. These are what turn the engine library into a runnable product; introduced M11 onward.

| #   | Subsystem                           | Reference                                                                                                                                                                                                                                                                            |
| --- | ----------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| 17  | **Worker Adapter**                  | Canonical worker-invocation protocol; per-vendor CLI isolation (Claude, then Gemini/`agy`) behind `IWorkerAdapter` → `CoreDispatchTarget`                                                                                                                                            | CLAUDE.md rule #2; §3, §4; #21 |
| 18  | **CLI Pump**                        | `aer run`: load workflow + bindings, drive project → resolve → dispatch → await to a terminal state                                                                                                                                                                                  | §21                            |
| 19  | **CLI Mutation Commands**           | `aer decide` / `aer cancel` against a running or paused task                                                                                                                                                                                                                         | §14, §21; UI spec §7           |
| 20  | **Distribution**                    | `aer` as an installable `dotnet tool`; native-lib bundling                                                                                                                                                                                                                           | AER Overview §6                |
| 21  | **UI Projection**                   | Read model + views: deterministic reconstruction from bound snapshots, event stores, and artifact directories; DAG/timeline/lineage rendering                                                                                                                                        | UI spec §1, §3, §10–§12        |
| 22  | **UI Control Surface**              | The §7 user actions (approve/reject/retry-with-revision/send-back/cancel/start) mapped onto Flow's closed `DecisionType` set, exclusively via the mutation interface                                                                                                                 | UI spec §6, §7                 |
| 23  | **UI Authoring**                    | Template/DAG/worker-binding editing with structural validation (cycles, `SupersedeTargets` ancestry); never touches a bound snapshot                                                                                                                                                 | UI spec §5, §8, §9             |
| 24  | **Dialogue Worker**                 | The first Case 2 encapsulated multi-model worker: a bounded, multi-turn Claude ↔ Gemini exchange inside one `ExecutionRequest`, recorded as a durable transcript artifact; vendor CLIs invoked inside the worker boundary, subscriptions-only like the adapters                      | Flow spec §18.2; UI spec §10   |
| 25  | **Conversation View**               | Render a dialogue execution's durable transcript as a conversation-style projection                                                                                                                                                                                                  | UI spec §10                    |
| 26  | **Daemon Network API**              | Task read model + mutation interface exposed over REST/WebSocket (`Aer.Daemon`); loopback by default, `--remote` binds beyond it; pairing protocol mints long-lived paired-client tokens                                                                                             | M20 decisions of record        |
| 27  | **Permission Scope Model**          | Vendor-neutral structured worker permission grants (categories: `ReadFiles`/`WriteFiles`/`RunShellCommands` with a pattern allowlist/`NetworkAccess`) replacing the opaque `PermissionScope` string, translated per-vendor inside each adapter's `Resolve` (Adapter Isolation)       | CLAUDE.md rule #2              |
| 28  | **Mobile Remote Client**            | `Aer.Mobile` (Flutter/Android): pairing, a WebSocket-driven decision inbox, Approve/Reject/Cancel over the existing daemon REST API                                                                                                                                                  | —                              |
| 29  | **Zero-Config Tailscale Transport** | Embedded `tsnet` (a Go sidecar the daemon supervises) on desktop, `flutter_tsnet` on mobile — no separate Tailscale app install required by the end user, BYO free Tailscale account                                                                                                 | —                              |
| 30  | **Workflow Template Library**       | Built-in, pre-authored `WorkflowDefinition`+bindings pairs (Solo run, Review run) a user picks from instead of hand-authoring; daemon-side materialization so a client with no filesystem access (a phone) can start a task                                                          | —                              |
| 31  | **Artifact-Referenced Supply**      | Decide-by-artifact-reference (execution id + filename, resolved server-side via the Artifact Manager) as an alternative to `aer supply`'s raw local `SourceFilePath`, so a client with no filesystem access can send an already-produced artifact back as Supersede revision content | §17.2, §17.5; §16              |
| 32  | **Generic Dialogue Config & Loops** | The Dialogue Worker generalized from a fixed two-party Initiator/Responder exchange to an N-party, condition-stoppable, always-ceilinged loop, editable as a first-class Template Editor step instead of wizard-only                                                                | §18.2                          |
| 33  | **Project-Directory-Bound Tasks**   | A task's worker invocation can set a real working directory (`AerTask.WithCwd`) instead of only AER's scratch artifacts folder, so a vendor CLI can operate on an arbitrary existing project the way it does run raw in a terminal; bindings stay portable across machines via per-machine profile mapping | —                               |
| 34  | **Interactive Sessions (Chat)**     | A repeatedly-superseded single-step session driven by daemon-orchestrated `Supersede` decisions carrying human messages; native vendor session resume (not re-supplied transcript) carries context turn to turn, falling back to a synthesized context block only at a mid-session vendor handoff; live in-turn progress (Claude's `stream-json` partial messages/tool calls forwarded over the existing WebSocket push) surfaces while a turn runs, not just on completion — Gemini/`agy` shows a generic running indicator, no verified streaming equivalent exists for it | §17.2, §17.5                   |
| 35  | **Worker Capability Discovery & Compact** | Surfaces each vendor's actual skills/agents/models as selectable options (filesystem scan for Claude, subcommand shell-out for `agy`) instead of requiring prior knowledge of a vendor's command vocabulary; a uniform "compact this conversation" action needing no vendor-specific cooperation | —                               |
| 36  | **Codebase Sessions & Known Projects** | The Interactive Session primitive with a working directory bound (capability 33), plus a small registry of previously-used project directories so a client with no host filesystem access (a phone) can select one instead of typing a raw path                                                | —                               |
| 37  | **Unified Task Creation**           | Chat/Codebase Session/Two-Vendor Dialogue as the default, zero-authoring front door on both desktop and mobile, replacing the built-in template picker as the primary entry point; full DAG/template authoring demoted to an explicit "Advanced" path, not removed                                | UI spec §5, §8, §9             |

---

## Milestone Roadmap

Which milestone introduces which capabilities.

| Milestone                                               | Capabilities introduced                                                                                                                            | Blocked by                                                                          |
| ------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------- |
| **M7: Foundation**                                      | 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12                                                                                                                  | aer-core M5                                                                         |
| **M8: Reactive Scheduler**                              | 10 (Retry Engine); full fan-out/fan-in DAG testing; manifest cache if scale demands                                                                | M7                                                                                  |
| **M9: External Decisions**                              | 13, 14, 15, 16 (all pause/decision/supersede/human machinery)                                                                                      | M8                                                                                  |
| **M10: Cancellation & Edge Cases**                      | §9 cancellation flow; crash recovery hardening (§7 full robustness)                                                                                | M9                                                                                  |
| **M11: First Real Run**                                 | 17 (Worker Adapter — Claude only), 18 (CLI Pump)                                                                                                   | M10; live aer-core M5                                                               |
| **M12: Full Control Surface**                           | 17 (Gemini/`agy` adapter), 19 (`decide`/`cancel`); canonical protocol generalized across vendors                                                   | M11                                                                                 |
| **M13: Distribution**                                   | 20                                                                                                                                                 | M11                                                                                 |
| **M14: UI Projection**                                  | 21                                                                                                                                                 | M11                                                                                 |
| **M15: UI Control Surface**                             | 22                                                                                                                                                 | M14; M12 (`aer decide`/`cancel`/`supply` — the mutation-interface callers it wraps) |
| **M16: UI Authoring**                                   | 23                                                                                                                                                 | M14                                                                                 |
| **M17: Dialogue Worker**                                | 24 (first Case 2 worker); plus the real-use walkthrough doc                                                                                        | M12 (both vendor CLIs proven live)                                                  |
| **M18: Conversation View**                              | 25                                                                                                                                                 | M17 (a transcript to project); M14                                                  |
| **M19: Product UX**                                     | — product-UX overhaul: task-first IA + decision inbox, plain language, guided authoring (no hand-edited config files), then the visual design pass | M18                                                                                 |
| **M20: Daemonization & Remote Control**                 | 26 (Daemon Network API)                                                                                                                            | M19                                                                                 |
| **M21: Zero-Config Remote Control & Permission Scopes** | 27 (Permission Scope Model), 28 (Mobile Remote Client), 29 (Zero-Config Tailscale Transport)                                                       | M20                                                                                 |
| **M22: Workflow Template Library**                      | 30 (Workflow Template Library), 31 (Artifact-Referenced Supply)                                                                                    | M21                                                                                 |
| **M23: Generic Dialogue & Project Packaging**           | 32 (Generic Dialogue configuration schema & loops), 33 (Unified Project Package model with profile segregation)                                    | M22                                                                                 |
| **M24: Interactive Sessions & Unified Task Creation**   | 34 (Interactive Sessions/Chat), 35 (Worker Capability Discovery & Compact), 36 (Codebase Sessions & Known Projects), 37 (Unified Task Creation), 42 (Task & Session Lifecycle Management)    | M23                                                                                 |
| **M25: Final Polish**                                   | 38 (Curve-based Bezier DAG rendering, brand icons, status-change motion & skeleton loading), 39 (Rich markdown output previewer), 40 (Keyboard-first triage modal), 41 (Mobile Visual & UX Parity) | M24                                                                                 |

M7–M10 complete the **v1.0 engine** (the behavioral spec is authoritative for it, and every §5.1 flow event now has a producer). M11 onward turns that engine into a runnable product: the worker adapters and the CLI pump the specs assume (§21, CLAUDE.md rule #2) but no engine milestone built, then distribution and — separately — the v0.7 UI.

M14–M16 are that UI track, splitting the roadmap's original single "UI" row the same way the engine split into M7–M10: **projection first** (capability 21 — every other UI capability renders on top of the read model), then the **control surface** (22) and **authoring** (23) as independent tracks behind it — M15 and M16 don't depend on each other, only on M14. Conversation-style views and live Observation-Tier turn streaming (UI spec §10) were deliberately assigned to *no* milestone throughout that track: they depend on Case 2 encapsulated multi-model workers (Flow spec §18.2) that didn't exist yet, and Overview §6's rule is to build the concrete thing before generalizing for it.

M17–M19 are the post-UI-track sequence, planned at M16's completion by re-checking the original project goal against what had shipped. Half of that goal exists and is proven live: vendor-to-vendor task hand-off on subscriptions (M12's recorded mixed-vendor gate). The other half — letting the two models actually talk to each other — does not: today §17.5's supersede loop makes the *human* the relay for every round of the exchange. **M17** builds the first Case 2 worker (the dialogue worker — the concrete thing the conversation view has been waiting on), opening with the real-use walkthrough the project is also missing. **M18** renders M17's durable transcript as UI spec §10's conversation view — load-on-refresh first; live Observation-Tier turn streaming stays unassigned until a concrete need names it. **M19** was originally scoped as a visual/UX design pass alone ("no new capability"), sequenced last so it styles the UI's final shape. At M18's completion the owner raised the bar: a user should not have to be an AI expert to use the product, and should never have to hand-edit a config file — with CyboFlow's human-first structure (a central view organized by what needs the human next: permission, decision, or action) the named inspiration. M19 is therefore redefined as a product-UX milestone; the original design pass survives as its next-to-last phase, still styling the final shape. M19's phase-by-phase plan lived here through its six phases; see this file's git history for the full text and `docs/decisions-of-record.md` for what it left behind.

M20 shipped the daemon scaffold: the scheduling pump extracted into `Aer.Daemon`, a loopback-only REST/WebSocket host API with token auth, and — ahead of any actual remote client existing — a pairing protocol (`--remote` binds beyond loopback; transient codes mint long-lived paired-client tokens) so the trust boundary exists before a client needs to cross it. M20's deferred token management (paired-token revocation and 60-second pairing code expiration) was fully resolved and closed by M21 Phase 6.

**M21** shipped zero-config remote control and structured permission scopes: vendor-neutral `PermissionGrant` models translated inside each adapter (`ReadFiles`, `WriteFiles`, `RunShellCommands`, `NetworkAccess`), `Aer.Mobile` (Flutter/Android) with QR-code pairing and live task streaming over WebSockets, zero-config Tailscale transport (`tsnet` sidecar on desktop, embedded `tsnet` via Go CGO / `tcp.dial` in `Aer.Mobile`), and M20's deferred token management (interactive revocation, 60s pairing code countdown).

**M22** shipped the built-in workflow template library: pre-authored Solo Run and Review Run templates materialized against installed vendor CLIs via `BuiltInWorkflowTemplates`, daemon template catalog and execution REST endpoints (`GET /api/templates`, `POST /api/templates/run`), desktop `TemplatePickerWindow`, mobile Flutter template picker screen, and artifact-referenced supply allowing remote mobile send-back (Supersede) without host filesystem access.

---

## Current Milestone

**M24: Interactive Sessions & Unified Task Creation** — progress:

- [x] Phase 1 — Interactive Sessions (Chat)
- [x] Phase 2 — Worker Capability Discovery & In-Session Compact
- [x] Phase 3 — Codebase Sessions & Known Projects
- [x] Phase 4 — Unified Task Creation (Desktop + Mobile)
- [ ] Phase 5 — Task & Session Lifecycle Management

---

## M23: Generic Dialogue & Project Packaging — Phase Plan

**Goal:** Generalize the Dialogue Worker beyond a fixed two-party exchange, and let a task's worker invocation point at a real project directory — the actual founding gap behind capability 33 (below) — instead of only AER's own scratch artifacts folder, while keeping the resulting bindings portable across machines.

This plan replaces an earlier draft (see git history) whose Phase 1 premise didn't match the code — `DialogueWorkerConfig` was already a fully data-driven record, not a hardcoded C# worker — and whose Phases 3/4 (Visual Diff Viewer, Multi-Agent Teamwork Preview) mapped to no capability this milestone was ever assigned. Revised in issue #252.

### Phase 1: Generalize the Dialogue Worker (Capability 32)
- **Goal**: Replace the fixed two-party Initiator/Responder shape with an N-party, condition-stoppable loop, safe by default (a hard ceiling always enforced regardless of configured `TurnBudget`), and a first-class Template Editor step type instead of wizard-only authoring.
- **Verification**: Unit tests on the generalized turn loop (N=2 regression, N=3+ round-robin, `StopSentinel` early exit, ceiling enforcement); a Template Editor round trip with no hand-edited JSON. Acceptance: one real multi-turn correspondence run end-to-end.

### Phase 2: Supersede-Chain Hardening
- **Goal**: Resolve, with an explicit test-backed decision, whether an already-superseded step's target can legally be named in a second Supersede — currently neither forbidden nor tested. This is the load-bearing prerequisite for M24's chat primitive (repeatedly superseding one step), not just hygiene.
- **Verification**: New chained-supersede tests pass; no regressions in the existing supersede end-to-end tests. Folds in issue #159 (xunit v3 migration) as groundwork here, since this phase is otherwise the most test-writing-heavy phase left before M24's own much heavier verification load (a ~50-100-turn stress test) lands on the same test projects.

### Phase 3: Project-Directory-Bound Tasks & Portable Bindings (Capability 33)
- **Goal**: Let a task's worker invocation set a real working directory — wiring `AerTask.WithCwd` (already a working native primitive, currently uncalled) through `WorkerInvocation` → the adapters → `CoreDispatchTarget` → `CoreDispatcher.DispatchAsync` — so Claude/Gemini can operate on an arbitrary existing project the way they do run raw in a terminal. No git-repo requirement; completion stays process-exit, same as today. Bundles the existing `WorkerBindingConfigEntry.PromptTemplate` absolute-path portability bug (breaks a dialogue step's bindings the moment a task moves to another machine) and a per-machine profile mapping (`%USERPROFILE%\.aer\profiles.json`) into the same phase.
- **Verification**: An integration test asserting a spawned worker's actual cwd matches a configured `WorkingDirectory`; a manual run pointing a task at a real directory (tried against both a git repo and a plain folder) confirming file access works; a `bindings.json` + dialogue sidecar copied to a different simulated profile root still resolves and runs.

---

## M24: Interactive Sessions & Unified Task Creation — Phase Plan

**Goal:** Make AER a full replacement for Claude Code and Antigravity — one-on-one chat with a single vendor, running a vendor agent against a real project directory, and two vendors working together — "streaming tasks to and from them, but not interacting with them directly," on both desktop and mobile, as simple and easy to use as possible. Planned in issue #258.

**The key architectural insight:** a chat "session" is a one-step workflow (no DAG, no cycle) that gets `Supersede`d once per human turn — reusing M9's Human Worker Support and M23 Phase 2's hardened supersede chains — requiring zero new `DecisionType` or `FlowEvent` fields. Conversation context carries forward via each vendor's own native session resumption (Claude's `--session-id`/`--resume`, `agy`'s `--continue`/`--conversation <id>`, both empirically verified working across separate process invocations) rather than a re-supplied transcript on every turn, with a synthesized context block used only at a mid-session vendor handoff. A concrete motivator: the roast-battle bug (issue #255, fixed in PR #256) showed the built-in templates fighting intent instead of serving it — this milestone replaces "fixed topology with baked-in language" with primitives shaped around what the user actually wants to do.

### Phase 1: Interactive Sessions (Chat) (Capability 34)
- **Goal**: A daemon-orchestrated, repeatedly-superseded single-step session driven by typed messages — the chat primitive everything else builds on. `POST /api/sessions/start` and `POST /api/sessions/send` are the only surface a client touches; each turn resumes the vendor's own native session id (captured from `agy`'s `--log-file` "Print mode: conversation=<id>" line on first turn for Gemini), dispatched via each vendor's minimal-overhead mode (Claude's `--bare`) by default. A per-session safety ceiling bounds both runaway quota burn and worst-case event-log growth; hitting it starts a fresh task/session, carrying context forward via the same summarization mechanism as a vendor handoff.
- **Live in-turn progress feedback**: both vendor CLIs currently run headless-and-silent from AER's point of view — a subprocess is spawned and its final artifact read only on exit, so a turn with tool calls can run 30+ seconds with no indication anything is happening, a real regression from what either vendor's own terminal already gives the owner. Claude's `--print` mode supports `--output-format stream-json` plus `--include-partial-messages`, emitting partial messages/tool-call/thinking events as real-time JSONL even headless — the adapter parses this stream during the run and forwards it through the existing WebSocket push (`BroadcastStateAsync`), extended below step-level state transitions to turn-level progress events, rendered as a live trace in the chat UI on both desktop and mobile. `agy` has no equivalent surfaced in `agy --help` (no `--output-format`, no verbose/streaming flag) — its `--print` mode is treated as a black box until exit, so a Gemini turn shows a generic "running" indicator instead of a fabricated live trace. Tracked in issue #260.
- **Verification**: a multi-turn session confirms native session resumption (not re-sent context) on same-vendor turns; a stress test drives ~50-100 programmatic turns on one step to confirm the repeated-supersede pattern holds at real chat frequency (projection cost, artifact directory naming, replay time); a vendor-handoff test where turn 2 goes to a different adapter than turn 1 and still has turn 1's context; a latency comparison with/without minimal-overhead dispatch; ceiling enforcement test; a Claude turn with at least one tool call shows partial messages/tool-call events arriving incrementally in the UI during the run, not only after exit; a Gemini turn confirms the generic running indicator appears and clears correctly with no fabricated detail.

### Phase 2: Worker Capability Discovery & In-Session Compact (Capability 35)
- **Goal**: Let the user see and invoke what each vendor CLI actually supports (skills, agents, models) instead of only typing plain prose, plus a uniform "compact this conversation" action. `IWorkerAdapter` gains an optional discovery method (filesystem scan of `.claude/skills/*/SKILL.md` and `.claude/commands/*.md` for Claude; `agy agent`/`agy models`/`agy plugin list` shell-outs for Gemini), surfaced via `GET /api/sessions/{id}/commands`. Compact is an ordinary message asking the resumed session to summarize itself, then the daemon starts a fresh native session seeded with that summary.
- **Verification**: discovery against this repo's own `.claude/` and `agy`'s real subcommands returns a non-empty, correctly-shaped list; a compact round-trip shrinks the stored transcript while preserving enough context that the next turn still makes sense.

### Phase 3: Codebase Sessions & Known Projects (Capability 36)
- **Goal**: The same session primitive pointed at a real project directory (composing Phase 1 with M23 Phase 3's `WorkingDirectory` wiring) instead of a scratch folder, plus a Known Projects registry (`{friendly name, path}`, modeled on `LocalUiConfigurationStore`'s capped recent-task-directory list) exposed via `GET /api/projects` so mobile — which can't browse the host filesystem — selects from it instead of typing a raw path. Gets its own conservative default `PermissionGrant` (M21) — e.g. `WriteFiles` on, `RunShellCommands` off by default — rather than inheriting a pipeline step's defaults, since repeated less-reviewed-per-turn dispatch is a bigger blast radius.
- **Verification**: a codebase session started against this repo confirms file reads/writes land in the real directory; a known-project round trip (register on desktop, select from mobile); confirming the conservative default blocks an unrequested shell command until broadened.

### Phase 4: Unified Task Creation (Desktop + Mobile) (Capability 37)
- **Goal**: Replace the 2-canned-template picker with Chat, Codebase Session, and Two-Vendor Dialogue as the default, zero-authoring front door on both `Aer.Ui` and `Aer.Mobile` (extending `inbox_screen.dart`'s existing imperative-navigation pattern), each asking only for what it needs (vendor(s), optional directory, optional opening message). Extends M18's Conversation View to render a repeatedly-superseded single step's execution history, reusing the existing WebSocket push. The existing Solo Run / Review Run templates and full DAG authoring remain available under an explicit "Advanced" path — nothing removed, just no longer the front door.
- **Verification**: starting and running a full chat, a codebase session, and a two-vendor dialogue from mobile with zero file-path input anywhere in the flow; the same from desktop.
- **Post-review fix (PR #276)**: the plan above assumed reusing M18's Conversation View, but that view reads a dialogue worker's `transcript.jsonl` — a chat/codebase session never writes one (its durable state is `SessionMetadata.Turns` plus each turn's `response.md`), so the picker's "Start" originally routed straight to the generic DAG `TaskView`, rendering a paused single-step workflow instead of a conversation. Desktop now has a dedicated `ChatView`/`ChatViewModel` (`Aer.Ui`/`Aer.Ui.Core`) that a materialized session routes to instead, showing turn history and live in-turn streaming (the `/api/ws/progress` socket Phase 1 already broadcast on, previously with no client-side consumer at all). Mobile's equivalent (below) is now built too.
- **Post-review fix (PR #276), independent viewing across clients**: building the mobile chat UI surfaced a pre-existing defect (predates M24; filed as issue #279, same treatment as #277's collision bug) in `/api/ws`'s broadcast — every client deserialized every incoming push unconditionally, so one client (desktop or another phone) opening a different task silently overwrote what every other connected client displayed. Both `Aer.Ui.Core/TaskSession.cs` (`ShouldApplyProjectionPush`) and `Aer.Mobile/lib/inbox_screen.dart` (`_openDirectoryPath`) now filter incoming pushes against the directory *this* client itself has open, seeded from the first push a fresh client sees. Fixed client-side; `Aer.Daemon`'s own single-current-task/broadcast-to-everyone model is unchanged (still fine given the filtering) and out of scope here.
- **Post-review fix (PR #276), mobile chat UI**: `Aer.Mobile` now has a dedicated `ChatScreen` (`lib/chat_screen.dart`), the Flutter counterpart of desktop's `ChatView` — turn history via `GET /api/sessions/{sessionId}` (`DaemonClient.getSession`, new) and live in-turn streaming via a new `watchProgress()` client method against `/api/ws/progress` (previously zero mobile consumption). Since this phone has no filesystem access to the daemon host (unlike desktop's 2-second `.aer/session.json` poll), it re-fetches session state off the same filtered `/api/ws` push `InboxScreen` already uses, rather than polling on a timer, and applies a 5-minute client-side send timeout given `/api/sessions/send`'s fire-and-forget completion never reports failure to any client. `Aer.Daemon` gained a `SessionId` sibling on the WS payload (`SendStateAsync`, same additive pattern as `DirectoryPath`/`WorkerAdapters`) so a phone that didn't start the session itself (seeded from another client's push, or picked from `/api/tasks/recent`) can still discover it's looking at an interactive session and which id to fetch turns for. Navigation into `ChatScreen` only ever happens from an explicit local action (starting a session here, or tapping an inbox card for a session this phone already has open) — never automatically off an incoming WS push, per the independent-per-client-viewing fix above. Two new `DaemonIntegrationTests` (`WebSocketSnapshot_IncludesSessionId_ForASessionDirectory`, `WebSocketSnapshot_OmitsSessionId_ForAnOrdinaryTaskDirectory`) cover the new sibling directly; a fresh session with no initial message was found to never actually run (no `snapshot.json` until a turn completes), so it's invisible over `/api/ws` until then — a pre-existing daemon quirk, not a regression, worked around in the test by constructing a completed-turn fixture directly rather than depending on a live vendor CLI.

### Phase 5: Task & Session Lifecycle Management (Capability 42)
- **Goal**: Give the user real lifecycle control over every task/session (chat, codebase session, dialogue, solo/review run) instead of the current one-way "materialize and accumulate forever." A task/session created since Phase 4 replaced hand-authoring as the front door has no way to be archived, unarchived, or deleted from either app — `~/.aer/tasks`/`~/.aer/sessions` only ever grows, and reclaiming a name already in use (rejected rather than silently clobbered as of issue #277) means finding and deleting the folder on disk by hand. Archive/unarchive is a directory-native marker (e.g. a `.aer/archived` sentinel) rather than a schema field, so it applies uniformly to DAG tasks and interactive sessions without every task type needing a metadata record; archived items drop out of the default list but stay on disk and fully loadable until unarchived. Delete is real, permanent directory removal, gated behind an explicit confirmation in the UI given it can't be undone. Ships on both `Aer.Ui` and `Aer.Mobile`, matching Phase 4's own desktop+mobile bar.
- **Verification**: archiving a task/session removes it from the default list view (desktop and mobile) but it is still directly loadable and reappears once unarchived; deleting removes the directory and it no longer appears in any listing; a regression test confirms a new task/session can reuse a name freed by deleting the earlier one; no change to how an unarchived, non-deleted task/session behaves today.

---

## M25: Final Polish — Phase Plan

**Goal:** Transform the visual layout of AER Flow into a premium product matching reference-caliber tools (Linear, Raycast) — on desktop *and* mobile, as the final milestone. Renumbered from the original "M24: UI Visual Overhaul" during M24/M25 replanning (issue #258) to make room for Interactive Sessions & Unified Task Creation as the new M24.

**Also carries a real future direction, not yet phased:** full remote-control parity from
`Aer.Mobile` — DAG/history/lineage viewing, `RetryWithRevision`, and general (non-Review-run)
Supersede, all currently desktop-only. M22 deliberately scoped mobile to two built-in templates
plus Review-run send-back rather than building this; the owner's own framing during M22 planning —
"I do eventually want to have more control than this remotely, but this is a good start" — is the
reason it's tracked here instead of dropped. No phase commitment yet; revisit scoping this
milestone's phase list against whichever of these the owner actually wants once M24 has shipped and
Unified Task Creation has real usage to learn from.

**Also carries a second future direction, not yet phased: an animated brand mark.** Settled during
icon-refresh discussion alongside M22 planning: the approved mark (a two-source fan-in "Y", tested
down to 16×16 — see `docs/decisions-of-record.md`) rotates 180° into a fan-out "Λ", a crossbar
strokes in to complete a capital "A", then "ER Flow" reveals to finish the "AER Flow" wordmark —
the mark doing double duty as logo and lettermark. Deliberately **one static mark, not two**: the
fan-in "Y" is the only static asset anywhere (app icon, GitHub avatar, docs header, everything) —
fan-out never ships as a standalone image, only as a mid-transition animation frame, which is also
why fan-out's small-size legibility doesn't need chasing (hand-tuned proportions, optical
correction) the way a real second static mark would: it's never shown at favicon/avatar scale, only
at splash/loading size where the direct-downscale test that failed it as an icon doesn't apply. Not
scoped to one surface — use it wherever a brand moment already exists or makes sense, desktop or
mobile alike (candidates: `Aer.Ui` startup/splash, `Aer.Mobile` cold-start splash, a loading state
either app already shows) rather than building one bespoke implementation and calling the note
closed. Real engineering per platform (Avalonia keyframe/transform + a stroke-draw animation for the
crossbar on desktop; Flutter's own equivalent on mobile — no shared animation asset between them) —
no phase commitment yet, phase this against the rest of M25's plan once M24 has shipped.

**Also carries a third future direction, not yet phased: a Visual Diff Viewer for step revisions.**
Drafted as an M23 phase, then dropped during M23 replanning (issue #252) because it mapped to no
capability M23 was ever assigned, and it overlaps this milestone's own capability 39 (Rich markdown
output previewer) closely enough that it belongs here instead — a side-by-side file-revision diff
panel for a step's "Send back" feedback loop, scoped against M25's actual phase list once this
milestone starts rather than assumed here.

### Phase 1: Curved Bezier DAG Canvas & Motion (Capability 38)
- **Goal**: Refactor the DAG canvas to render connection paths as smooth Bezier curves. Implement dynamic line highlighting on hover to trace dependency chains. Add brand-specific icons (Claude, Gemini, human) directly to the node templates. Folds in issue #208 (status-change animation and skeleton loading states were never implemented): step-status transitions animate instead of snapping, and pending/loading UI shows skeleton placeholders instead of blank space.
- **Verification**: Seamless rendering, hover states, and smooth drag-and-drop interactions; a status-change transition (e.g. Running → Paused) visibly animates; a loading view shows a skeleton state rather than a blank pane.

### Phase 2: Rich Markdown Output Previewer (Capability 39)
- **Goal**: Integrate a markdown engine (e.g., `Markdown.Avalonia`) to render step artifact output previews as rich formatted text, headers, checklists, and tables, replacing the raw TextBox.
- **Verification**: Standard markdown output files are rendered with high-fidelity typography, colors, and table structures.

### Phase 3: Keyboard-First Triage Mode (Capability 40)
- **Goal**: Add keyboard-first navigation to the Home triage screen. When a step pauses for review, enable quick keys (`A` to approve, `S` to send back) with highly visible keyboard badges, maximizing non-expert efficiency.
- **Verification**: Complete task reviews and decisions purely from the keyboard.

### Phase 4: Mobile Visual & UX Parity (Capability 41)
- **Goal**: A mobile-native design pass for `Aer.Mobile` — not a port of Phases 1-3's desktop-specific Bezier-curve/markdown-renderer work, but the same bar M19 already holds `Aer.Ui` to (token-driven styling, a considered visual identity, no rough-placeholder screens) — covering the new Chat/Codebase-session/Dialogue screens from M24 Phase 4 as well as the existing pairing/inbox screens.
- **Verification**: scoped in detail once M24 has shipped and those screens exist to design against.

---

## Completed Milestones

Completed milestones keep only a one-paragraph summary here. Their phase checklists live in the
closed GitHub milestones; their decisions of record — the constraints and precedents later work
still leans on — in `docs/decisions-of-record.md`; and the full phase plans — goals, boundaries,
and the open questions each phase resolved — in this file's git history and the linked issues.

**M22: Workflow Template Library** — shipped a built-in template catalog (`solo-run` and `review-run`) materialized against PATH-probed vendor CLIs (`claude`, `gemini`), daemon template endpoints (`GET /api/templates`, `POST /api/templates/run`), desktop (`TemplatePickerWindow`) and mobile (`inbox_screen.dart`) template picker UIs allowing zero-path task creation, and artifact-referenced supply enabling remote mobile send-back (Supersede) with no host filesystem access.

**M21: Zero-Config Remote Control & Permission Scopes** — shipped structured vendor-neutral permission scope models (`PermissionGrant` for file and command access), `Aer.Mobile` (Flutter remote client) with QR code pairing and live task streaming over WebSockets, zero-config embedded Tailscale transport (`aer-sidecar` Go node on desktop, embedded `tsnet` via CGO and RFC 6455 `ws_codec.dart`/`ws_client.dart` over `tcp.dial` on mobile), and closed M20's deferred token management (60s pairing code countdown and interactive paired client revocation).

**M20: Daemonization & Remote Control** — extracted the scheduling pump out of the UI process into a lightweight background runner (daemon) that runs continuously. Exposed task read models, mutation interfaces, and WebSocket real-time updates over a loopback and remote-accessible API. Added single-instance mutex enforcement, constant-time authentication token validation, and process lifecycle supervision. Hardened tool invocation security by replacing command shell spawners (`cmd.exe /c` / `sh -c`) with shell-less direct binary execution and C#-side environment variable expansion. Designed and implemented the client-pairing gateway protocol allowing secure LAN/Wi-Fi remote clients to connect and authenticate against the daemon.

**M19: Product UX** — the product-UX overhaul the owner scoped at M18's completion: a
navigation shell (Home's decision inbox + task cards, Task, Author) built behind a new
Avalonia-free `Aer.Ui.Core` seam that completes M15's deferred MVVM migration; the Task view
rebuilt needs-you-first with plain-language drill-in tabs and the precise engine record one
disclosure away; guided authoring that writes durable workflow/binding files with zero
hand-edited config, vendor CLI knowledge owned by its adapters/workers; a token-driven visual
design pass (property-level restyling, status as border+tint, one accent per surface, a
generated app icon) judged against an owner-adopted reference set (Linear, GitLab, Dagster,
Stately.ai, GitButler/Neovide, n8n, Raycast) under a one-product-not-a-collage directive; and a
headless completion gate driving the entire non-expert path — author, run, review, send back,
finish — through the real UI controls over stub CLIs, in default CI on all three OSes.
`docs/walkthroughs/first-real-workflow.md`'s UI section rewritten to match.

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
- **True zero-signup multi-user remote control (post-M21)** — M21 Phases 5–6's "zero-config" only
  removes the separate Tailscale app install; every user (including future ones, not just the
  owner) still does a one-time Tailscale OAuth sign-in ("BYO free Tailscale account", per Phase
  4/5's own goal text) via the link `tsnet`'s embedded node shows on first run. If the bar is ever
  "a stranger installs only the Aer app, no third-party identity step at all," that requires
  operating your own coordination/relay infrastructure instead of Tailscale's — a materially bigger
  commitment (you become the operator of real network infrastructure relaying user traffic:
  security surface, uptime, cost, abuse potential), not a Phase 5/6 refinement. Two candidate
  shapes, not yet decided between: (1) self-hosted Headscale (Tailscale's open-source coordination
  server) — reuses Phase 5's `tsnet` embedding almost unchanged, but hands every paired device a
  real virtual-network interface and means operating/patching a general-purpose mesh-VPN control
  plane that's a clean-room reimplementation of Tailscale's own protocol; (2) a purpose-built Aer
  relay — a small server proxying only `Aer.Daemon`'s existing REST+WS API between an already-paired
  phone and desktop, no general network access, nothing to keep in sync with an upstream project.
  For a single-developer product, (2) is the smaller commitment for the same user-facing outcome
  unless generic device-to-device networking beyond Aer's own use is actually wanted. Revisit this
  only if/when real multi-user demand shows up — not a preemptive build.
- ~~**Whether MVVM spreads beyond the decision surface**~~ — resolved in M19 Phase 2 (#187): `Aer.Ui.Core` completed the full MVVM migration across all surfaces (Home, Task, Author, Remote) using `CommunityToolkit.Mvvm` ViewModels with compiled bindings, leaving `MainWindow` code-behind as a pure presentation shell.
