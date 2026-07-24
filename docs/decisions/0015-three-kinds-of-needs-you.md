# 0015 — A pause asks for one of three things: permission, a decision, or approval

Status: proposed
Date: 2026-07-24

**Proposed, not accepted.** Two of the three kinds map onto machinery that already ships. The third
— permission — depends on #445's mechanism and an **unrun per-vendor probe**; until that probe
runs, this record describes an intent, not a guarantee. See *The open dependency* below.

## Context

When a worker stops and hands control back to the person, the product today shows one undifferentiated
"paused" and, at the surface level, a single approve/reject affordance. But a person is being asked
qualitatively different things, and answering the wrong kind of question is how the surfaces confused
each other in the manual run behind [0012](0012-what-aer-flow-is.md).

#334 already split *two* of these in the engine. `PausePointKind`
(`src/Aer.Flow/Domain/WorkflowDefinition.cs:36-54`) distinguishes:

- `ReadyForReview = 0` — "the step ran to a terminal outcome and its result awaits human
  review/approval." The historical meaning of every pause, and deliberately the zero value so
  snapshots written before the field existed still deserialize correctly (`WorkflowDefinition.cs:38-44`).
- `NeedsInput = 1` — "an interactive turn paused ready for the operator's next message… not awaiting
  approval… awaiting input."

Crucially, that kind is **a static property of the step declaration**, derived from the bound
snapshot at projection time and carried by no event, "never inferred from conversation content"
(`WorkflowDefinition.cs:26-54`). That is CLAUDE.md Architecture Rule 1 holding: Flow classifies the
*shape* of the pause, never reads the *content* to decide.

What the engine has no representation for at all is the third thing: a worker asking **"may I do
this?"** — run this command, write outside the working directory, hit the network. Today that is not
a pause; it is the silent auto-approval [0004](0004-permission-scopes.md) documents (#331).

## Decision

**Every pause is exactly one of three kinds, and the surface names which:**

| Kind | The question | Engine mapping |
|---|---|---|
| **Permission** | *May I do this?* — a capability the run is not pre-cleared for | **new** — see below |
| **Decision** | *Which way should I go?* — a fork the worker will not choose for you | `PausePointKind.NeedsInput` |
| **Approval** | *Is this finished work acceptable?* — act on a completed result | `PausePointKind.ReadyForReview` |

The three are not styling on one control. They differ in what an answer *means*: a permission answer
authorizes a capability (and composes with [0004](0004-permission-scopes.md)'s scopes); a decision
answer supplies a direction the run continues along; an approval answer accepts, revises, or rejects
work already done. Rendering them as one "paused" state is what let a surface offer "approve" where
the honest act was "answer," and vice versa.

**This is a different axis from [0004](0004-permission-scopes.md), and does not contradict it.** 0004
governs permissions *declared ahead of time* — the project/session/step scopes that decide what never
needs asking and what fails closed. This record governs what happens *at runtime when a worker asks
anyway*: a permission pause is the fall-through when a capability is neither pre-granted nor
pre-denied. 0004 is the policy; this is the interruption when policy is silent.

### The open dependency

The permission kind is only real if a vendor CLI, running headless under AER, will **stop and ask**
rather than decide for itself. That is exactly what is unverified, and the evidence cuts both ways:

- `claude` headless historically **auto-approves** — the #331 defect: it ran a command a grant
  omitted. An auto-approving CLI never raises a permission pause on its own.
- `agy` (the Antigravity CLI) headless **fails closed** — it denies a capability it cannot get
  cleared. A fail-closed CLI might deny AER's own "ask the operator" call before the operator ever
  sees it.

So permission-as-a-pause depends on #445 providing a mechanism (an AER-hosted tool the worker
calls to *request*, which blocks the turn on a human answer) **and** on a live probe confirming each
vendor actually routes through it headless instead of auto-deciding. Until that probe runs, permission
stays `proposed`; decision and approval are safe to build now because their engine kinds already ship.

## Consequences

**Easier.** Each pause surface has one job and one honest set of answers. "Needs you" stops being a
single bucket the UI has to guess the meaning of, and the guess that produced approve-where-answer-was-meant
is designed out.

**Harder.** The product now has to *know* which kind a given pause is at the moment it renders it —
trivial for decision/approval (the snapshot says so), unresolved for permission until the mechanism
and probe land. Shipping decision and approval while permission is still `proposed` means the UI must
gracefully handle a permission pause that cannot yet occur, rather than assume all three exist.

**Obliges us to** run the #445 probe before promoting this to `accepted`, keep the kind derived from
the declaration and never from content (CLAUDE.md Rule 1, as `PausePointKind` already does), and —
when permission does land — wire its answer into [0004](0004-permission-scopes.md)'s scope
intersection rather than treating it as a fourth, ad-hoc grant.

**Supersedes nothing.** It *extends* #334's two-kind split to three and names the third as the work
#445 exists to enable.

Related: #331 (enforcement — see 0004), #334 (the two-kind pause split), #445 (permission-request
mechanism and its probe).
