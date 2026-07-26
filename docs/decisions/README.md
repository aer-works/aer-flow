# Decision records

Numbered, immutable-ish records of decisions that shape the product. One decision per file.

They exist because intent was scattered across issue comments, chat transcripts, spec prose and
three competing planning documents — and scattered intent is what produced a six-destination product
where four surfaces show the same objects and none reconciles with the others.

## How they relate to everything else

| Artefact | Answers | Lives |
|---|---|---|
| **Decision record** | *why* we chose this, **and it is still in force** | here, numbered, cited by the rest |
| **Journey** | *what the product promises* | `spec/`, see #312 |
| **Behavioural spec** | *what the engine does* | `spec/` |
| **Milestone history** | *what we decided **then***, kept for provenance | [`docs/milestone-history.md`](../milestone-history.md) |
| **Plan** | *what we are doing **now*** | [`docs/plan.md`](../plan.md), gated |
| **Issue** | *what to change* | GitHub, cites a journey |

The spec cites decisions. Issues cite journeys. **#283 is the index that links both** — it is not
another document competing with them.

**Decision record vs milestone history** — the distinction that used to be missing, and the reason
that file was renamed (it was `decisions-of-record.md`, which read as a rival to this folder):

> A **decision record** is a rule you must follow today. **Milestone history** is why a past
> milestone did what it did — provenance, cited from code comments and runbooks when someone asks
> "why is this field here?". If a historical decision is still binding, it belongs *here*, as a
> numbered record. History is never the authority for current work.

Neither is the same as the plan: the plan says what is being built, and defers *status* to the
sources that keep it.

## Format

Front matter, then the record:

```
# NNNN — Title
Status: proposed | accepted | superseded by NNNN
Date: YYYY-MM-DD
```

Then: **Context** (what forced the decision, with evidence), **Decision** (what we chose, stated
plainly), **Consequences** (what this makes easy, what it makes hard, what it obliges us to do),
and — **required since 2026-07-25 (#527)** — **Rests on**.

### `Rests on` — the load-bearing facts, and what would falsify them

A list of the specific external facts the decision would not survive losing. Each row names the
fact, how it is known, and what happens to this decision if it turns out false.

```
## Rests on

| fact | how we know | if false |
|---|---|---|
| A `PreToolUse` hook exiting 2 blocks a tool even with an allow rule | **measured** — `pixi run vendor-verify -- --only gate.hook-exit-2-beats-allow` | the gate has no vendor-independent enforcement point; §3 is void |
| A second concurrent login against one subscription is permitted | **assumed** — needs the account owner; not measurable from an agent session | per-worker config roots collapse to one; §5's isolation model needs replacing |
```

Note what the example does *not* contain: a row like *"`--allowedTools` bounds what a worker can
do — assumed"*. That claim is **measured false** ([#529](https://github.com/aer-works/aer-flow/issues/529)),
and filing a known-false fact as merely unverified is the most dangerous row this table can carry —
it reads as pending work rather than as a broken dependency. **If a fact is false, the decision is
already broken; say so in the decision, don't park it here.**

**Why this is now mandatory.** The vendor audit (#527) falsified four vendor claims this project had
built on, and the decisions that broke — 0015 and 0018 — had asserted a *mechanism* without recording
what it rested on. When the mechanism turned out to be wrong there was no way to see what else fell
with it, so the blast radius had to be recovered by re-reading everything. A decision that names its
dependencies makes that mechanical instead.

Two rules for the column:

- **Distinguish measured from assumed, always.** An assumed row is not a defect — it is a
  verification task with a known owner. An assumed row *recorded as measured* is how 0015 broke.
- **Prefer a fact that a command can re-check.** Where a `pixi run vendor-verify` check exists, cite
  it by name; that turns "is this still true?" into something a future session runs rather than
  re-derives. See [`../documentation-lessons.md`](../documentation-lessons.md).

## Rules

- **Never edit a decision to change its meaning.** Supersede it with a new record and set the old
  one's status. The reasoning that was wrong is as useful as the reasoning that was right — three
  findings in the evaluation that produced these records were confidently wrong before they were
  checked, and knowing that is what stops them being re-derived.
- **Cite evidence, not preference.** "Chat and codebase sessions produce byte-identical bindings"
  beats "these feel redundant."
- A decision that no issue or spec section cites is either not a decision or not yet applied.

## Index

| # | Title | Status |
|---|---|---|
| [0001](0001-two-nouns-workflow-and-session.md) | Two nouns: workflow and session (the session is a room; user-facing noun now *room*, amended by 0013) | accepted |
| [0002](0002-one-vocabulary.md) | One vocabulary, no translation map | accepted |
| [0003](0003-templates-collapse-to-three-shapes.md) | Templates collapse to three shapes | accepted |
| [0004](0004-permission-scopes.md) | Permissions scope by project, session and step (the ladder-at-point-of-ask and denial-is-an-answer added by 0022) | accepted |
| [0005](0005-seam-milestones.md) | Capability milestones alternate with seam milestones | accepted |
| [0006](0006-visual-direction-quiet.md) | Visual direction is "Quiet" (emphasis rule added by 0028) | accepted |
| [0007](0007-background-work-inline-and-dedicated.md) | Background work: glance inline, expand in place, dedicated surface for depth | accepted |
| [0008](0008-runtime-streaming-over-append-log.md) | Runtime: live streaming over a durable append log | accepted |
| [0009](0009-session-lifecycle-and-retention.md) | Session lifecycle & retention: a tree you count the top of | accepted |
| [0010](0010-skills-and-advisor.md) | Worker capabilities are skills (app-level canonical, per-vendor realization); the advisor is the first one; addressing/namespacing added by 0024 | accepted |
| [0011](0011-token-based-context-management.md) | Context management is token-based, not turn-based (its unit and trigger **corrected by 0027**) | accepted, except those |
| [0012](0012-what-aer-flow-is.md) | What AER Flow is: a drop-in Claude Code replacement with more than one model in the room | accepted |
| [0013](0013-room-is-the-user-facing-noun.md) | Room is the user-facing noun; session is the vendor's | accepted |
| [0014](0014-shapes-are-a-list-not-a-canvas.md) | A shape is an ordered list that renders as a graph (a step's contents added by 0025; `DependsOn` corrected to engine-only) | accepted |
| [0015](0015-three-kinds-of-needs-you.md) | A pause asks for one of three things: permission, a decision, or approval (its **mechanism** guidance amended by 0029) | accepted, except that |
| [0016](0016-memory-is-room-owned.md) | Memory belongs to the room, not the worker | accepted |
| [0017](0017-vendor-model-effort-are-three-choices.md) | Vendor, model and effort are three separate choices (its effort-naming clause **corrected by 0023**) | accepted, except that clause |
| [0018](0018-attention-is-the-primary-signal.md) | Attention is the primary signal: state orders the list, notifications never decide (rate-limited band amended by 0026; notification **source** supplied by 0030) | accepted |
| [0019](0019-consulting-is-not-deciding.md) | Consulting is not deciding: you can ask anyone, and the gate stays open | accepted |
| [0020](0020-one-state-machine.md) | One state machine: every surface renders the room's state, none derives its own (carries "errors are content") | accepted |
| [0021](0021-artifacts-are-files.md) | Artifacts are files: vendor-neutral, versioned, attributed, never silently overwritten | accepted |
| [0022](0022-permission-ladder-and-denial-is-an-answer.md) | The permission ladder is offered at the moment of asking, and a denial is a real answer (amends 0004) | accepted |
| [0023](0023-effort-and-models-are-named-by-behaviour.md) | Effort is named by behaviour and models are offered by purpose, never a vendor's own string (corrects 0017) | accepted |
| [0024](0024-commands-are-namespaced.md) | Commands are namespaced by owner, and /ask-all is the broadcast (amends 0010) | accepted |
| [0025](0025-a-step-is-an-instruction-with-a-gate-toggle.md) | A step's instruction is its body, and "ask me first" is a toggle on the step (amends 0014) | accepted |
| [0026](0026-running-out-of-plan-is-a-state-not-a-failure.md) | Running out of plan is a state with a reset time, not a generic failure | accepted |
| [0027](0027-context-is-per-worker.md) | Context belongs to the worker, not the room; running out is a choice (corrects 0011) | accepted |
| [0028](0028-no-permissive-control-is-the-default.md) | Visual rank is a decision: no permissive control is ever the default (amends 0006) | accepted |
| [0029](0029-the-gate-is-three-mechanisms.md) | The gate is three mechanisms with three populations, not one (amends 0015) | accepted |
| [0030](0030-aer-is-its-own-notifier.md) | AER is its own notifier: no vendor event announces a pause (amends 0018) | accepted |
| [0031](0031-skills-are-account-wide.md) | Skills are account-wide, not project-scoped: one library per person (resolves an open question in 0010) | accepted |
| [0032](0032-room-orchestrator-is-mandatory.md) | A room always has exactly one orchestrator: first worker added by default, removal refused until reassigned | accepted |
| [0033](0033-skills-attach-directly-no-persona.md) | Skills attach directly to a worker; there is no Persona object | accepted |
| [0034](0034-project-permission-ceiling-lives-in-aers-own-config.md) | A project's permission ceiling lives in AER's own app-config, keyed by project path (resolves an open obligation in 0004) | accepted |
| [0035](0035-aer-yield-is-a-structured-mcp-tool-not-a-sentinel.md) | `aer yield` is a structured MCP tool call, not a text sentinel — reuses 0029's mechanism, needs none of its held-open complexity | accepted |
