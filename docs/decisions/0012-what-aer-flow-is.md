# 0012 — What Baton is

Status: accepted
Date: 2026-07-24

## Context

Twenty-four milestones in, the repository held **eleven decision records about parts** and **nine
journeys about behaviours**, and **no document saying what the product is**. That absence was not
noticed until it was measured.

The owner ran the desktop app by hand on 2026-07-24 and found five defects in one sitting — #465
through #469. Read together, they say something sharper than "the UI has bugs":

| Defect | What it actually was |
|---|---|
| #467 | The shell reported "no task open" while a task was open and running |
| #468 | A tab asserted "no conversation recorded" directly above the conversation |
| #465 | A dialogue prompt duplicated on turn 1 and grew every turn after |
| #466 | Non-ASCII output mangled on Windows |
| #469 | A build script's error named a problem that did not exist |

**Not one of them was "the code is wrong."** Every one was *nobody ever specified what this surface
does when X*. Two surfaces disagreed about state because nothing said which was authoritative. A
prompt grew unboundedly because nothing said what a turn is supposed to send. These are the failures
of a product with no definition, not of a team that cannot write code.

This is the same root cause [0005](0005-seam-milestones.md) named from the other direction:
milestones were capability-shaped, so every completion gate drove an HTTP surface and none touched a
person's experience. A capability gate cannot catch "this screen contradicts that screen," because
neither screen is what it measures.

## Decision

**Baton is a drop-in replacement for Claude Code that puts more than one model in the room, and
lets you leave the room without losing it.**

Three commitments follow, and every one of them constrains what may be built:

**1. A real coding agent, first.** You point it at a directory and talk to it; it edits, runs, and
reports. *Drop-in* means someone moving across from Claude Code loses nothing they had — so the
single-worker path stays fast and unadorned. **Multi-model is an escalation, never a tax on the
simple case.**

**2. A messenger, not a console.** Workers are presences you address — present or not, busy or idle
— listed together, spoken to one at a time or together. The unit of interaction is a conversation
with participants, not a job submitted to a queue. This is [0001](0001-two-nouns-workflow-and-session.md)'s
room model stated as a product commitment rather than an object model.

**3. Shapes you can draw.** Defining repeatable work must be quick and visual — draw it once, save
it, start it in a click. See [0014](0014-shapes-are-a-list-not-a-canvas.md) for the form that takes.

### What it is not

The exclusions are the useful half. A definition that rules nothing out is a wish.

- **Not an API product.** It drives vendor CLIs already authenticated on the machine, against
  **subscriptions**. No key handling anywhere, by design (CLAUDE.md's Adapter Isolation rule).
- **Not a workflow builder you live in.** Graphs author and inspect templates; they are not the
  daily surface. See [0014](0014-shapes-are-a-list-not-a-canvas.md).
- **Not a router or a judge.** Flow never reads conversation content to decide anything —
  Architecture Rule 1. Who answers is always a person's explicit choice.
- **Not a team tool.** One operator, several agents. Not multiplayer.

### The claims this obliges us to be able to demonstrate

A claim that cannot be shown working end to end is a plan, not a feature. These are deliberately
journey-shaped, because a capability gate is exactly what failed to catch the defects above:

- A model **not previously in the room** is asked about a pending decision, answers, contradicts the
  first — **and the decision is still open.**
- Two vendors act in one room on plan authentication, with no key configured anywhere.
- A fact established by one vendor is used by a different vendor later in the same room.
- Quit the desktop app mid-run; answer from the phone; reopen and find it continued.

## Consequences

**Easier.** Every subsequent design question has something to be answered *against*. "Does this
serve the simple case, the room, or the shape?" resolves most scope arguments without appeal to
taste — and a proposal serving none of them needs a different justification.

**Harder.** Commitment 1 is a permanent tax on commitments 2 and 3: every multi-model affordance
must be reachable without imposing itself on someone who only wants one agent and a folder. That is
a constraint on layout and defaults forever, not a one-time check.

**Obliges us to** re-derive `spec/journeys.md` from this rather than leave nine journeys that
predate it, and to treat the demonstrations above as the completion bar for the re-architecture.
It also **retires "capability-shaped milestone" as an acceptable planning unit** for anything
user-facing — the concrete form of [0005](0005-seam-milestones.md)'s rule.

**Does not settle** how the product looks ([0006](0006-visual-direction-quiet.md)) or what the
surfaces are. It settles what they are *for*.
