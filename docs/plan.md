# The plan

The living plan for AER — versioned with the code, reviewed in PRs, and **gated so it can't rot**.
Its predecessor was a GitHub issue (#283) that went stale in five places while nothing caught it;
that is the exact failure the M25 re-architecture existed to kill, so the plan now lives where the
discipline does.

## The bar

AER replaces Claude Code (terminal + mobile) and Antigravity (desktop) **entirely** — full parity
between desktop and mobile, talking to either vendor from either surface, staying as easy to
understand and orchestrate as either standalone product. Any work is judged against that goal
directly, not as an isolated screen.

**"Full parity" means no capability is ever surface-exclusive by design — it does not mean an
identical feature set ships on both surfaces on day one.** A considered, reasoned sequencing
difference (the phone launches pairing-first with no folders of its own, per
`docs/design/02-screens.md`, before any capability needing one exists to offer) is compatible with
the bar — a milestone not having shipped yet is not the same claim as a capability being permanently
surface-exclusive. An unexamined gap — something missing from mobile because nobody built it there
yet, with no reasoning behind the absence, and no journey requiring it eventually close — is not. The
distinction is whether the gap was decided and has a closing condition, or merely happened.

## How this plan stays honest

This document owns **durable structure** — the bar, the milestones and what each one demonstrates,
the dependency order, and the decisions in force. It does **not restate status**, because restated status is what rots. Status is deferred
to the sources that already keep it, each with its own gate:

| For… | Look at | Kept honest by |
|---|---|---|
| *why* we chose something | [`docs/decisions/`](decisions/) | numbered, immutable — superseded, never edited |
| what the product *promises*, and whether it's met | [`spec/journeys.md`](../spec/journeys.md) | the journey tests (#313) + the reconcile gate (#314) |
| what the *engine* does | [`spec/`](../spec/) behavioural specs | the test suite |
| an issue's live state | the **[milestones](https://github.com/aer-works/aer-flow/milestones)** (M26–M30) / project board | GitHub |
| what a *past* milestone shipped | [`docs/milestone-history.md`](milestone-history.md) | append-only; provenance, never authority |
| whether a specific vendor fact is still measured/current | [`docs/vendor-capabilities.md`](vendor-capabilities.md) / `docs/vendor-*.md` | `pixi run vendor-verify` sentinels, re-runnable on demand |

**The gate.** `tests/Aer.Plan.Tests` runs in default CI and fails the build if this file drifts from
those sources — every decision it names must exist in `docs/decisions/` and match the index, and
every journey it references must exist in `spec/journeys.md`. A plan that can lie about a decision or
a promise is a plan that rots; this is the check that stops it, the same way #314 stops the
journeys' statuses from rotting.

**The last row has no test behind it, deliberately.** "Is this vendor fact still true" is a claim
about prose, not a reference that resolves or fails to — the same reason this project doesn't build
a checker for the gates themselves (`CLAUDE.md`, "Cost and reversibility are the operator's call").
A decision-table row or open question that restates a *measured* status (rather than citing the
`vendor-verify` sentinel or doc section that carries it) is exactly how `#583` went stale: 0023's row
said "the mapping itself is unmeasured and gated on a probe" long after `#572`/`#573` shipped it,
and nothing caught that because nothing here transcribes a fact that could rot — it should have
pointed at the sentinel instead. Write every row in this file the second way, not the first.

## Decisions in force

Recorded in [`docs/decisions/`](decisions/) (#316), never edited to change meaning — superseded.

| # | Decision |
|---|---|
| [0001](decisions/0001-two-nouns-workflow-and-session.md) | Two nouns: **workflow** and **session** — "task" is deleted from the product. **A session is a *room*** (amended): a multi-participant conversation that spawns child sessions into a tree. The user-facing noun is now **room** (amended by 0013); "session" narrows to the vendor's resumable thread. |
| [0002](decisions/0002-one-vocabulary.md) | One vocabulary — retire the translation map, enforce by lint (#315). |
| [0003](decisions/0003-templates-collapse-to-three-shapes.md) | Templates collapse to **three shapes with presets**. |
| [0004](decisions/0004-permission-scopes.md) | Permissions scope by **project ∩ room ∩ step**, failing closed. |
| [0005](decisions/0005-seam-milestones.md) | Capability milestones alternate with **seam milestones**. |
| [0006](decisions/0006-visual-direction-quiet.md) | Visual direction is **Quiet** — status colour is a ramp separate from the brand accent. |
| [0007](decisions/0007-background-work-inline-and-dedicated.md) | Background work surfaces at **three levels**: glance inline, expand in place, dedicated surface for depth (#360). |
| [0008](decisions/0008-runtime-streaming-over-append-log.md) | Runtime is **live streaming over a durable append log** — worker lifetime is a swappable policy (default cold; scoped warmth is #368). Per-turn cost is intrinsic, so a cross-vendor usage view is the real cost lever (J9). |
| [0009](decisions/0009-session-lifecycle-and-retention.md) | **Count the top of the tree, not the tree** — children ephemeral by default, worker spawning bounded by a depth/count ceiling that doubles as J6's safety rail. |
| [0010](decisions/0010-skills-and-advisor.md) | **Worker capabilities are skills** — app-level canonical, realized per-vendor by the adapter (native where possible, prompt-injection floor); native skills pass-through; participant behaviour is a role/skill binding. The advisor is the first one (M26). |
| [0011](decisions/0011-token-based-context-management.md) | **Context management is token-based, per worker** — each participant tracks its own vendor-reported usage against its own model's window; approaching a threshold offers a choice (summarise now / start a fresh room / leave it), with automatic compaction surviving only as a disclosed backstop, never the default trigger. The turn ceiling (`SafetyCeiling`) remains a backstop only (M26). Mechanics in 0027. |
| [0012](decisions/0012-what-aer-flow-is.md) | **What Baton is** — a drop-in Claude Code replacement that puts more than one model in the room and lets you leave without losing it; multi-model is an escalation, never a tax on the simple case. Retires capability-shaped milestones for anything user-facing (#465–#469 were all missing specs, not wrong code). |
| [0013](decisions/0013-room-is-the-user-facing-noun.md) | **Room is the user-facing noun**; "session" narrows to the vendor CLI's resumable thread — amends 0001, renames without remodelling. One room, one directory (may hold several repos); disjoint folders deferred (#443). |
| [0014](decisions/0014-shapes-are-a-list-not-a-canvas.md) | **A shape is an ordered list rendered as a graph**, not a freeform canvas — keyboard- and phone-native, diffs like source; a step names its blocker, GitHub-style, default blocked-by-previous, so fan-out is a list operation and parallelism is emergent from the engine's existing scheduler. Retired the canvas polish in #266 — closed, with the brand marks and motion carried to #476. A step's contents are 0025. |
| [0015](decisions/0015-three-kinds-of-needs-you.md) | **A pause asks for one of three things — permission / decision / approval**: decision→`NeedsInput`, approval→`ReadyForReview`, permission genuinely new. **Accepted** — the probe it was blocked on ran (#472) and found a blocking MCP tool holds a turn open on *both* vendors, so all three kinds have verified mechanism. |
| [0016](decisions/0016-memory-is-room-owned.md) | **Memory belongs to the room, not the worker** — shared across vendors, a visible and versioned working document; workers propose additions, the product never infers them (#442). |
| [0017](decisions/0017-vendor-model-effort-are-three-choices.md) | **Vendor, model and effort are three separate choices** on the worker chip — vendor is the tool (`claude`/`agy`); effort is named by behaviour (quick/standard/careful/exhaustive) and model by purpose (deep/balanced/fast), never a vendor's own flag value. The mapping lives in the adapter and is measured and shipped (`#572`/`#573`). Reasoning in 0023. |
| [0018](decisions/0018-attention-is-the-primary-signal.md) | **Attention orders the list; notifications never decide** — rooms sort by state (needs-you → working → idle → quiet) then recency, surviving a hundred rooms; a notification informs and links into the room, never carries the verdict (#282). |
| [0019](decisions/0019-consulting-is-not-deciding.md) | **Consulting is not deciding** — put a question to anyone, including a worker not yet in the room, and the gate stays open until *you* answer it; the consulted worker gets the room summary plus the raising turn verbatim, disclosed and editable before sending, and the responder is always chosen, never inferred (Rule 1). The corpus's centrepiece, absent from the repo until #474. |
| [0020](decisions/0020-one-state-machine.md) | **One state machine** — every surface renders the room's state, none derives its own, which makes #467/#468 impossible rather than merely fixed. Absence is not a state; a failure's reason is content in the room, not a status word with a drill-in. |
| [0021](decisions/0021-artifacts-are-files.md) | **Artifacts are files** — vendor-neutral, versioned, attributed, explicitly attached. One file list; the only distinction is "in your project" or not, and execution directories are never surfaced. Saving into a project is diff-and-choose, never a default overwrite. |
| [0022](decisions/0022-permission-ladder-and-denial-is-an-answer.md) | **The permission ladder is offered where the question is asked**, never only in settings, and **a denial is a real answer** the worker is told about and continues from. `y`/`n` never on `Enter`; a pending permission dies with its turn everywhere at once. Amends 0004. |
| [0023](decisions/0023-effort-and-models-are-named-by-behaviour.md) | **The reasoning behind 0017's naming rule** — Adapter Isolation (Rule 2) and the empirical vendor-scale comparison. The mapping is measured and shipped (`#572`/`#573`, `docs/vendor-capabilities.md`'s canonical effort mapping section, guarded by `vendor-verify` sentinels) — `#498` is the remaining UI/adapter work that consumes it. |
| [0024](decisions/0024-commands-are-namespaced.md) | **Commands are namespaced by owner** — Room, then each vendor, plus `/ask-all` for everyone; canonical skills under Room and native ones under their vendor, both marked. No slash palette on a phone: the same set becomes an Actions sheet. Amends 0010. |
| [0025](decisions/0025-a-step-is-an-instruction-with-a-gate-toggle.md) | **A step's instruction is its body**, previous output flows in implicitly and there is **no template language**; **"ask me first" is a property of a step**, not a node type. Amends 0014. |
| [0026](decisions/0026-running-out-of-plan-is-a-state-not-a-failure.md) | **Running out of plan is a state with a reset time**, not a generic failure — a third `FailureClassification` value carrying the reset instant, per vendor, spending no retry budget. The dominant real failure for a subscription user, undecided since #18 closed silently and the types froze on the interim behaviour. Amends 0018's band for a rate-limited vendor. |
| [0027](decisions/0027-context-is-per-worker.md) | **The mechanics behind 0011** — why the unit must be the worker (a room-wide sum would compact one worker while another has barely used any context) and the trigger an announced choice, not silent compaction; the `SessionMetadata` object-model gap this needs (`#493`). |
| [0028](decisions/0028-no-permissive-control-is-the-default.md) | **Visual rank is a decision** — no permissive control is ever the visual default, and a genuine either/or carries equal weight. Written because the corpus's own permission mockup drew `Allow once` as the accent-filled primary, training the reflex 0022 exists to prevent. Amends 0006. |
| [0029](decisions/0029-the-gate-is-three-mechanisms.md) | **The gate is three mechanisms with three populations** — a `PreToolUse` hook covers *vendor* tools and is mandatory on every worker (an MCP gate bounds nothing the model can reach through `Bash`, #529); a blocking `tools/call` is the only mechanism that *holds* rather than refuses; `elicitation` is an uncircumventable refusal measured on **both** vendors. A hook's `ask` survives `auto` mode, so an operator's `auto` no longer erases AER's permission surface. Amends 0015's mechanism guidance. |
| [0030](decisions/0030-aer-is-its-own-notifier.md) | **AER is its own notifier** — `PermissionRequest` and `Notification` are both silent under `-p`, so no vendor event announces a pause. AER hosts the gate, therefore already holds the pause at ask-time and notifies from that act; the notification path is vendor-independent by construction. Supplies the signal source 0018 assumed. |
| [0031](decisions/0031-skills-are-account-wide.md) | **Skills are account-wide** — one library per person, available in every room regardless of which directory or repo that room's worker is pointed at. No project-level skill store in M27. Resolves an open question 0010 explicitly left for M26. |
| [0032](decisions/0032-room-orchestrator-is-mandatory.md) | **A room always has exactly one orchestrator** — the first worker added becomes it by default, and removing the current holder is refused until the role is reassigned first. Authority is granted by an auto-bound Skill, the same mechanism 0033 uses for every other worker capability; that Skill grants a default addressing/attribution role, never a decision authority — it does not call `aer decide` on anyone's behalf ([0038](decisions/0038-a-reviewer-verdict-never-calls-aer-decide.md) forecloses that for every worker). |
| [0033](decisions/0033-skills-attach-directly-no-persona.md) | **Skills attach directly to a worker; there is no Persona object** — zero, one, or several at once, with nothing to name, save, or diverge from. Replaces the M27 design pass's Persona proposal, which layered a second object on top of Skill for no capability Skill didn't already provide. |
| [0034](decisions/0034-project-permission-ceiling-lives-in-aers-own-config.md) | **A project's permission ceiling lives in AER's own app-level config, keyed by project path** — never a file inside the project's own directory. First presented as a trust prompt on first use of a folder; reachable afterward from a global Settings "Projects" list. Resolves an obligation 0004 explicitly left open. |
| [0035](decisions/0035-aer-yield-is-a-structured-mcp-tool-not-a-sentinel.md) | **`aer yield` is a structured MCP tool call, not a text sentinel** — replaces `Aer.Workers.Dialogue`'s current substring-match stop condition. Reuses 0029's measured-but-unbuilt MCP mechanism; needs none of 0029's held-open complexity, since nothing here waits on a human. Real, unbuilt infrastructure (`#585`), and the Dialogue shape blocks on it. |
| [0036](decisions/0036-shape-is-rendering-not-a-second-state-machine.md) | **A room's shape is Flow's existing state, rendered differently — not a second state machine.** "No step running" already is Conversation; a Dialogue collapsing on interruption is the existing `CancellationRequested` mechanism; the one new primitive (a mid-execution gate request) rides on 0035's MCP server, not a separate mechanism. |
| [0037](decisions/0037-permission-answers-never-share-the-turn-lock.md) | **A permission answer must never share the per-session turn lock a pending turn already holds** — the existing `/api/tasks/decide` pattern (correlate by id, bypass the turn lock entirely) already shows how; resolves #393↔#445 as a design constraint, since the component in question isn't built yet to measure. |
| [0038](decisions/0038-a-reviewer-verdict-never-calls-aer-decide.md) | **A reviewer's verdict is evidence for a human decision, never the decision itself** — [0019](decisions/0019-consulting-is-not-deciding.md) already forecloses an auto-deciding implementer/reviewer loop; the workflow shape (implement → review, `PausePoint(ReadyForReview)`) needs no new primitive, but the terminal `aer decide` call stays human, always. Corrects a session memory that had concluded otherwise. |
| [0039](decisions/0039-dialogue-turns-use-vendor-session-continuation-not-full-history-resend.md) | **A dialogue turn resumes the vendor's own session; it does not resend the transcript** — `#581`'s live failure and `#582`'s cost/latency problem trace to the same design (full-history resend forced a file-read instruction both vendors break on). Adopting `Aer.Adapters`' own session-continuation mechanism fixes both and retires the `{PROMPT_FILE}` workaround `#580` shipped. |
| [0040](decisions/0040-needs-you-groups-by-kind-and-actions-alone-defer.md) | **Within "needs you," gates group by kind and each gets its own affordance; only an action may say "Later"** — a permission blocks and sits first, a decision offers "ask someone", an action alone can defer without reading as urgent. Push behaviour (0030) is unaffected. Promotes already-drawn design corpus content to a record. |
| [0041](decisions/0041-phone-authoring-lands-with-shapes-not-after.md) | **Phone template authoring ships with the shapes milestone (M29/J17), not deferred past it** — `02-screens.md`'s "out of scope for the phone's first version" describes the pre-shapes phone (M26–M28), not a standing exclusion. The single place this fact lives; M29's criterion and the Bar's parity note both reference it rather than restating it. |
| [0042](decisions/0042-retry-backoff-is-a-derived-obligation-with-steady-default.md) | **Retry backoff is a derived obligation event, and an unspecified backoff means steady, not immediate** — pacing is recorded in the log (`StepRetryScheduled`), computed by the engine loop's clock, consumed by readiness; replay stays clock-free. Mechanics in spec §10.2; this record holds the rejected shapes and why the steady default deliberately changes every definition that omitted `Backoff`. |
| [0043](decisions/0043-structured-verdict-is-shape-checked-evidence.md) | **A review verdict is a schema'd contract output, shape-checked by the engine and never read by it** — `ProducedOutput` may declare an `OutputSchema` from a closed set, validated at outcome classification like a condition (spec §4.2); validation is parse-only, applying 0038's evidence-never-decision boundary to structured findings. This record holds the rejected shapes, including the tempting route-on-severity half-step. |
| [0044](decisions/0044-memory-belongs-to-the-room-and-changes-only-by-decision.md) | **Memory belongs to the room, and changes only by decision** — memory's lifetime is coupled to the room directory, never to any conversation (both room models stay open; never reconstructed from transcripts); its form is a `memory/` directory of small fact files with an index; edits arrive as structured MCP proposals that escalate as held work. Promotes the #672 minimal-form conversation record into the register. |
| [0045](decisions/0045-the-product-is-baton-the-journal-is-the-ledger.md) | **The product is Baton; the journal is the ledger; the CLI token stays `aer`** — dictation round-trip was the deciding criterion ("AER Flow" spoken is "Airflow"); the deliberation report is archived on #823, and the rename's execution (inventory first, public surfaces last, if at all) is that issue's plan. |

## The completion bar: journeys

A milestone is done when its **[journeys](../spec/journeys.md)** pass — a promise driven against the
*real* surface a person uses, not an isolated screen. **Eighteen are defined** — J1–J9 from the M25
evaluation, J10–J18 from the design corpus's nine claims — and their statuses are machine-kept, so
this document links them rather than repeating them. Journey tests are the answer to M25's sharpest
finding: *not one completion gate touched a UI, so a product could pass every gate it had with no
working client — and very nearly did.*

**A decision with no journey is orphaned** — recorded, citable, looks done, and nothing will ever
catch its absence. Journeys are the only artifact here with teeth (a test, `ReconcileTests`, and #314
enforcing declared status against reality), so *"did this decision land?"* means *"is there a journey
that would fail if we violated it?"*. That traceability is not yet complete in either direction and
is part of #474's audit.

## The work, by milestone

**Ordered by what a person can do, not by capability.** Decision
[0012](decisions/0012-what-aer-flow-is.md) retires capability-shaped milestones for anything
user-facing, because #465–#469 were all *missing specifications* rather than wrong code — a milestone
that ships a capability can be complete while the thing a person does with it does not work. Each
milestone below therefore ends on a **demonstration**, not a checklist. Per-issue state lives on the
board; this is the structure and the reasoning, which change rarely.

**This plan covers everything a milestone's own journeys need, not only the UI.** The 2026-07-24
design pass found that the five manual-run defects it was scoped to explain were all missing UI
specifications, not engine defects — true as far as it went, but this plan is no longer only that UI
overhaul: it is the plan for finishing the product, gated on the journeys above. `Aer.Ui`,
`Aer.Ui.Core` and `Aer.Mobile` are rebuilt against the decisions above; `Aer.Flow`, `Aer.Adapters`,
`Aer.Daemon` and the wire protocol are touched wherever a milestone's own journeys require it (J6's
`PreToolUse` hook and J4/J7's pairing/reconnection work are exactly this — engine and daemon work a UI
rebuild alone could never satisfy). No layer is out of bounds by default; a milestone's demonstration
criteria decide what it touches, not a boundary drawn here in advance.

### M26 — The room works

The daily driver, excellent: one room, one worker, one folder, nothing in the way. **This is the
milestone the product is judged on** — [0012](decisions/0012-what-aer-flow-is.md) commits to
multi-model as an escalation and never a tax on the simple case, so if M26 is not good, nothing after
it matters.

**Demonstrated when** a person can talk to one agent about one folder with nothing in the way; every
surface renders the room's own state for **every kind of work, not just chat**
([0020](decisions/0020-one-state-machine.md), [J5](../spec/journeys.md)), so "no room open" while
running is impossible rather than merely fixed; a failure shows what broke *in the room*, with the
worker that failed there to be asked; first run gives each surface a real first action — open a
folder or start work on desktop, pair to a machine on phone, not an empty list
([J8](../spec/journeys.md)); a fresh phone on an ordinary LAN (not a tailnet) pairs and completes a
first authenticated round-trip, and a daemon port change doesn't strand it ([J4](../spec/journeys.md));
a dropped connection shows a truthful state and a recovery action that actually restores service,
never a raw exception ([J7](../spec/journeys.md)); and a tool withheld from a worker is refused **by
any route it might reach the same effect through**, not just the one flag that names it
([0029](decisions/0029-the-gate-is-three-mechanisms.md), [J6](../spec/journeys.md) — journeys.md's own
framing: first-class, not an edge-condition inside the happy path).

**The demo bar (operator decision, 2026-07-30, #806):** the milestone is demonstrated as
[J19](../spec/journeys.md) — a multi-lane room **operated from the phone**: lane-terminal and
needs-you moments delivered to a paired phone by AER's own notifier, decisions answered there
advancing the room, the desktop untouched after initiation. This pulls the *delivery slice* of
M28's needs-you surface into M26 — the delivery pipeline only, not the permission/decision/action
taxonomy, which stays M28. The reason is measured, not aesthetic: the operator runs ~90% of this
project's construction from a phone (#806), so a room demonstrated only at a desk is demonstrated
where its operator isn't.

**Depends on** nothing but the seam work M25 already landed. It is first because every other milestone
renders inside it — including, now, whether the room can be trusted and reached at all.

### M27 — More than one model in the room

The reason the product exists ([0012](decisions/0012-what-aer-flow-is.md)), and the half no
single-vendor tool can copy.

**Demonstrated when** two subscriptions act in one room on plan auth with no key configured anywhere;
two workers of the *same* vendor run at different models and efforts, in AER's own vocabulary
([0023](decisions/0023-effort-and-models-are-named-by-behaviour.md)); two same-vendor workers are each
addressed unambiguously via a sticky per-vendor instance handle (`@agy-1`/`@agy-2`); a worker attaches
a skill and behaves accordingly, and the room's one orchestrator can be reassigned but never removed
without a successor already holding the role
([0031](decisions/0031-skills-are-account-wide.md)/[0032](decisions/0032-room-orchestrator-is-mandatory.md)/[0033](decisions/0033-skills-attach-directly-no-persona.md));
a fact one vendor established is used by another later in the same room
([0016](decisions/0016-memory-is-room-owned.md)); a document authored by one vendor and edited by
another carries a diff between their versions ([0021](decisions/0021-artifacts-are-files.md)); one
question put to every worker returns answers side by side
([0024](decisions/0024-commands-are-namespaced.md)); from a live chat a person spins off a
clearly-marked child (draft→review→gate) that reports its result back into the chat, which stays live
throughout ([J2](../spec/journeys.md), decisions 0001/0008/0009); a single view shows usage across
every vendor a room is spending against, best-effort per what each CLI exposes
([J9](../spec/journeys.md), decision 0008 — only meaningful once this milestone puts more than one
vendor in the room at all); and two workers debate to a bounded conclusion
with no human turn in between (0003's Dialogue shape) — this last one is **blocked**, direction now
decided for all three named prerequisites but none yet built: `#581`/`#582`'s mechanism redesign
([0039](decisions/0039-dialogue-turns-use-vendor-session-continuation-not-full-history-resend.md),
session continuation replacing full-history resend) and
[0035](decisions/0035-aer-yield-is-a-structured-mcp-tool-not-a-sentinel.md)'s `aer yield` (`#585`).
No Dialogue-shape UI ships before all three land.

**Depends on** M26 — a second worker is an escalation from a room that already works.

### M28 — Needs you

Attention, permission, and answering from anywhere. The milestone that makes leaving the room safe.

**Demonstrated when** on reopening either surface, work is legibly separated into waiting-on-you /
running / finished, waiting-on-you first, and a failed piece of work reads as failed, never as
"finished" ([J3](../spec/journeys.md), decision 0018 — the room-list promise this milestone is named
for, and which nothing above actually tested until now); a desk-started run that pauses at a
**decision** gate (not just a permission) appears on the paired phone, and approving it there
**advances the run to completion** with the desktop reflecting the new state, no manual reload on
either side — [J1's own "Passes when," in `spec/journeys.md`](../spec/journeys.md), not restated in
full here; at a live gate a
person asks a worker not previously in the room, gets a contradicting answer, and **the gate is still
open** ([0019](decisions/0019-consulting-is-not-deciding.md)); granting "allow in this room" means not
being asked again, and the grant can be found and revoked in settings
([0022](decisions/0022-permission-ladder-and-denial-is-an-answer.md)); quitting the desktop app
mid-run, answering the permission on the phone, and reopening finds it continued; and with a
permission, a decision, and an action all pending at once, the needs-you list groups them by kind —
only the action offers "Later" ([0040](decisions/0040-needs-you-groups-by-kind-and-actions-alone-defer.md)).

**Depends on** M26 for the surface and M27 for a second worker to consult, and on **#445** for the
mechanism a permission is raised through — see the note below, which corrects what this plan
previously said about it.

### M29 — Shapes

Repeatable work, authored as an ordered list that renders as a graph
([0014](decisions/0014-shapes-are-a-list-not-a-canvas.md),
[0025](decisions/0025-a-step-is-an-instruction-with-a-gate-toggle.md)).

**Demonstrated when** — [J17's own "Passes when," in `spec/journeys.md`](../spec/journeys.md), not
restated here so this line cannot drift from it the way it already has once
([0041](decisions/0041-phone-authoring-lands-with-shapes-not-after.md) — read that record for the
phone-authoring timing question specifically; read J17 itself for what "demonstrated" requires).

**Depends on** M26. It is deliberately late: shapes are the leverage, not the day job, and a shape
editor built before the room works would be a canvas with better marketing.

### M30 — Visual polish

Presentational work that depends on the rebuilt surfaces existing first. **Deliberately has no
runtime demonstration criterion** — it introduces no new capability for a person to exercise, unlike
M26–M29. Its completion gate is a design review against
[0006](decisions/0006-visual-direction-quiet.md) (Quiet) and the motion question `docs/plan.md`'s
open questions already name: does every surface read as *confirming*, never *performing*, with no
regression in what M26–M29 already demonstrated.

### What an exhaustive journey audit found and closed (2026-07-26)

An earlier pass checked design/plan/decisions against each other but never against
[`spec/journeys.md`](../spec/journeys.md) — the actual promises, not just internal consistency. A
full journey-by-journey check (walking each journey's own `Serves` list against the milestone meant
to demonstrate it, not sampling) found that **J10–J18 (added with the M27 design pass) all matched
their milestone's criteria correctly, but roughly half of J1–J9 (the original journeys) had no
milestone criterion covering them at all** — including J6, which `spec/journeys.md` itself calls
"first-class, not an edge-condition." The milestone criteria above now fold in J1, J3, J4, J5, J6, J7,
J8, J9 and the missing half of J2, each attributed to whichever milestone's own stated identity fits
it (M26 for the foundational/safety ones, M27 for the multi-vendor ones, M28 for the ones about
attention and cross-surface gates). This is why M26 in particular reads larger than its original
"one room, one worker, nothing in the way" framing suggested — a room that can't be reached, trusted,
or safely denied isn't actually working, so those bars belong in the milestone everything else
renders inside of, not deferred past it. No new milestone was needed; every gap had a defensible home
in the existing five.

### What the vendor audit (#527) changes about this sequence

The milestone *order* survives — it is ordered by what a person can do, and nothing measured changes
what a person does. Four things change **inside** milestones, and they are recorded here because
each moves work earlier than the sequence above implies.

**M26 acquires the gate, and that is the real change.** [0029](decisions/0029-the-gate-is-three-mechanisms.md)
makes a `PreToolUse` hook **mandatory on every worker AER spawns**, not only on workers whose flow
declares a gate — because [#529](https://github.com/aer-works/aer-flow/issues/529) measured that an
MCP gate bounds nothing the model can reach through `Bash`. So the hook, and the startup self-check
that proves it fires, belong to "one room, one worker" rather than to M28. This is the audit's
largest scheduling consequence: **M26 is no longer the milestone with no permission work in it.**

**M26 also acquires a launch constraint.** Hooks load only from the process's own cwd `.claude/`,
with no parent fallback, and `--add-dir` loads no configuration. AER must control the worker's
working directory or pass `--settings` — a spawn-path requirement, not a UI one, and cheaper to
satisfy before three surfaces render against it.

**M27 must set the fan-out depth cap explicitly.** One level of subagent nesting runs with nothing
configured (the vendor documents the opposite), and a subagent inherits its parent's permission mode
and cannot be given a stricter one. A second worker in the room is therefore a *tree* of unknown
depth unless `CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH` is set. Concurrency, cost attribution and the
gate all have to hold for that tree.

**M28's dependency is narrower than "#445" and sharper.** The durable gate is the blocking
`tools/call` and only that — `elicitation` and `requiresUserInteraction` refuse rather than ask, so
neither can hold a pause while somebody is away. The blocking call is measured to survive **200 s**;
its upper bound is unknown, and it is reaped without a `timeout` floor or progress notifications.
Answering a permission on the phone after quitting the desktop app — M28's own demonstration — takes
longer than 200 s in the ordinary case, so **M28 must persist the gate and release the call rather
than hold it open**, which is [0015](decisions/0015-three-kinds-of-needs-you.md)'s ask-time
persistence doing real work rather than being a crash safeguard.

The open interaction the section below names — whether AER's per-session turn lock (#393) tolerates
a held-open turn — is *reduced* by that, not resolved: releasing the call is what makes it
tractable, and it still has to be settled before #445 is built.

### The permission mechanism — what this plan used to say, and what was measured

> **Amended 2026-07-25 by [0029](decisions/0029-the-gate-is-three-mechanisms.md).** What follows is
> accurate and remains the record of what #472 measured. It is no longer complete: it describes the
> blocking MCP tool as *the* mechanism, and the gate turned out to be three mechanisms covering three
> different populations of tools. Read 0029 for the current shape.


This section previously asserted that *"`claude -p` surfaces MCP tools and auto-approves them, so
there is nothing to intercept"*, and that the mechanism *"must end the turn rather than block inside a
tool call, or it deadlocks."* **`#472` measured the opposite of both**, and
`docs/vendor-capabilities.md` records the runs:

- **Both CLIs fail closed headless.** `claude -p` with a clean environment denied the write and
  reported `permissionMode: default`. `--permission-mode manual` is a **no-op** — still `default`, no
  prompt ever issued.
- **A blocking MCP tool holds a vendor turn open on both.** A watcher minted a token *after* observing
  the call start, so the correct answer proves the turn genuinely waited: `claude` 10.9 s, `agy`
  10.3 s. MCP is not Claude-only — `agy` loads servers from `~/.gemini/config/mcp_config.json`.

**The limit of the correction.** The probe disproved the *vendor* half. It did **not** test whether
AER's own per-session turn lock (#393) tolerates a turn held open while a human answers — that
interaction remains genuinely open and must be settled before #445 is built. Correcting one confident
wrong claim into another is the same failure.

Two implementation constraints fall out and belong here rather than in an issue body: `claude` **spawns
the server twice** (once to enumerate tools, then again for the real turn), so it must be cheap to
start and hold no in-memory state across spawns; and `agy` **hands back the resume key at gate time**
(`antigravity.google/conversation_id`), so a gate persisted with it survives a host crash — which is
[0015](decisions/0015-three-kinds-of-needs-you.md)'s ask-time persistence obligation made concrete.

### A pattern worth naming, because it recurred three times in one sitting

**#333, #390 and #335 each arrived as a single issue that turned out to be several, or rested on a
premise that measurement disproved.** Splitting them *before* writing code — not at PR time — is what
kept each diff reviewable; checking #390's premise against the actual vendor CLI is what stopped a
whole feature being built against a mechanism that does not exist. **An issue body is a hypothesis, not
a specification.**

Both #345 and #381 were also found stale on inspection: #345 still demanded a direction decision that
[0006](decisions/0006-visual-direction-quiet.md) had already recorded, and #381's "split the god files
first" prerequisite was already satisfied for the file #335 actually touched.

The generalisation, paid for twice more since: **judge the thing, not a proxy for it.** A backlog
combed by issue *title* got 2 of 4 wrong; a document set reviewed by *name* got 5 of 6 wrong; and a
design transfer checked by whether records *existed* rather than what they *covered* lost 16 of 18
settled calls. The correction is mechanical verification against the source —
[`docs/design/coverage-audit.md`](design/coverage-audit.md) exists because of it.

## Why a disciplined spec produced an unusable product

The evidence that is **in the repo** is [`docs/design/`](design/) — the seven artifacts of the
2026-07-24 design pass — and [`docs/milestone-history.md`](milestone-history.md)'s M25 entry. The
original ground-up evaluation (2026-07-22) was an external artifact and is deliberately not the
citation here: a plan that rests on a link outside the repo is one revoked share away from resting on
nothing. The operative lesson, distilled: every defect found lived in a **seam**, and every
structural failure had the same shape — *something could go stale silently because nothing checked.*
The corrections are controls, not notes: a required artifact (#312), a gate (#313, #314), a lint
(#315), an immutable record (#316), and now this plan's own gate. **A recorded lesson is not a
control** — on 2026-07-21 the same lesson was written down and nothing structural followed, and it
recurred the next day at larger scale.

**The honest limit:** the two most valuable corrections in the evaluation came from the owner pushing
back, and both times the software was fine and the report was wrong. Automated journeys stop seams
rotting. They do not tell you the product *feels* bad — that still takes someone using it and saying
so.

## Open questions

Genuinely undecided. Entries closed since M25 are recorded here as closed rather than deleted,
because "we already answered that" is the cheapest thing for a plan to forget:

- Directory-less rooms (#321, #331, #407 — a neutral scratch dir).
- The typeface (#453/#456 shipped Source Sans 3 + JetBrains Mono as in-repo assets on both toolkits).
- The claude/agy effort mapping (#572/#573 measured and shipped it; #498 is the remaining, still-open
  UI/adapter work, not a reopening of the question).
- Where a project's permission ceiling is stored (`#338` —
  [0034](decisions/0034-project-permission-ceiling-lives-in-aers-own-config.md) settled it: AER's own
  app-config, keyed by project path).
- Whether the per-session turn lock tolerates a turn held open while a human answers a permission
  (`#393` ↔ `#445` — [0037](decisions/0037-permission-answers-never-share-the-turn-lock.md) settled
  it as a design constraint on the not-yet-built answer path, since there is no implementation yet to
  measure).
- Whether a delegated implementer/reviewer loop can run without a human calling `aer decide` (it never
  was open — [0038](decisions/0038-a-reviewer-verdict-never-calls-aer-decide.md) found 0019 already
  forecloses it: the workflow shape needs no new primitive, but the terminal
  `PausePoint(ReadyForReview)` stays a human's own decision, always).

- **A room lives in one directory for the M26–M30 horizon, deliberately.** `#472` found `--add-dir`
  on both CLIs, so disjoint folders are feasible at the vendor level, and #443 tracks the idea — but
  this is left out of scope on purpose rather than carried as a pending gap: revisit only on a real
  demand signal, not as something blocking the current milestone set.
- **Motion.** The visual direction is settled (**Quiet**, [0006](decisions/0006-visual-direction-quiet.md));
  how much things move is not, and it is deliberately deferred to M30 rather than decided per screen.
- **Editing a sent message discards the replies after it** (`docs/design/04-workers-commands-control.md`,
  drawn UI, no backing decision — `#587`). Narrowed, not resolved: `claude`'s flag vocabulary has
  `--fork-session` (forks a resumed session under a new ID) but nothing suggesting it can fork from an
  *earlier point* in that session rather than its latest state; `agy` has no equivalent at all. Neither
  vendor's `--help` suggests a native rewind. Whether the vendor's own on-disk session state can be
  edited/truncated directly is still genuinely open (would need investigating the session file format,
  not just flags) — if it can't either, AER would need to reconstruct a post-edit session from scratch
  rather than ask the vendor to forget tail turns, with real cost/latency/per-worker-context (0027)
  implications that need designing, not assuming.

## Not in scope

- Multi-machine / multi-daemon switching — explicitly ruled out. A single daemon is fine.
- True zero-signup multi-user remote control (a stranger installs only the Aer app, no third-party
  identity step) — out of scope. It would mean operating your own coordination/relay infrastructure
  instead of Tailscale's (security surface, uptime, cost, abuse potential), not a refinement.
  Revisit only on real multi-user demand; two candidate shapes exist if it ever returns —
  self-hosted Headscale, or a purpose-built relay proxying only `Aer.Daemon`'s existing REST+WS API.
