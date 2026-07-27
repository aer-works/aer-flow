# 0032 — A room always has exactly one orchestrator

Status: accepted
Date: 2026-07-26

## Context

An earlier draft of `docs/design/01-definition.md` defined the room orchestrator as *"a pinnable
role: which worker in the room is authorized to call `aer decide` on another's gate, standing in for
the human."* That framing does not survive contact with
[0038](0038-a-reviewer-verdict-never-calls-aer-decide.md), written the same design pass: `aer decide`
is called by a human, always, for every gate kind, with no orchestrator exception — a structured
verdict from any worker is evidence a human weighs, never authority to close a gate itself
([0019](0019-consulting-is-not-deciding.md)). Caught before either idea left this design pass, so
this record states the corrected role directly rather than carrying the wrong one forward with a
footnote.

Separately, the corrected role definition covered *reassigning* the role in detail but was silent on
two adjacent questions: how the *first* orchestrator gets assigned when a room is created, and what
happens if the current orchestrator is *removed from the room* (not reassigned). Both are real gaps
— an engineer implementing room creation or worker removal needs an answer to build against.

## Decision

**A room always has exactly one orchestrator. Its authority is a default addressing/attribution
role, not a decision authority — it never calls `aer decide` on anyone's behalf.**

- **What the role actually grants.** The orchestrator is where an otherwise-ambiguous routing choice
  or an unattributed artifact/action is credited — a structural, Flow-routing fact
  (Architecture Rule 1's "explicit tool return"), never a human-facing gate. It does **not** resolve
  another worker's `PausePoint`, standing in for a human or otherwise; [0038](0038-a-reviewer-verdict-never-calls-aer-decide.md)
  forecloses that for every worker, orchestrator included.
- **First assignment.** A room requires at least one worker to exist at all. That first worker
  becomes the orchestrator by default the moment the room is created — there is no separate
  "assign an orchestrator" gesture at creation time, because there is no one else to choose from.
  The person can reassign it to a different worker at any time afterward, using the existing
  reassignment control.
- **Removal.** Removing the current orchestrator from the room is refused, the same way removing a
  worker an active workflow step depends on is refused (`docs/design/02-screens.md`). To remove
  that worker, the person reassigns the orchestrator role to a different worker in the room first,
  then removes the one that held it. This is one existing gesture applied twice, not a new
  mechanism.
- **Authority is granted by an auto-bound Skill, not a special-cased field.** Being the
  orchestrator means having the orchestrator Skill attached, which AER attaches automatically to
  whichever worker currently holds the role, and detaches on reassignment. This is the same
  skill-attachment mechanism [0033](0033-skills-attach-directly-no-persona.md) uses for every other
  worker capability, not a bespoke pinned-role flag. A worker can hold the orchestrator skill
  alongside any others it has attached. Nothing about it depends on which vendor or model that
  worker is running.

## Rests on

| fact | how we know | if false |
|---|---|---|
| `aer decide` is called by a human for every gate kind, with no orchestrator exception | **measured** — [0038](0038-a-reviewer-verdict-never-calls-aer-decide.md) and [0019](0019-consulting-is-not-deciding.md); `DecisionType`/`PausePoint` exist in `src/Aer.Flow/Domain` and nothing in code enforces the human half | the orchestrator could hold decision authority after all, and this record's central limit — an addressing and attribution default, never authority — is unnecessary |
| A room can hold more than one worker | **measured** — M27's whole premise, and the room model in `src/Aer.Flow` | the role is vacuous: a single worker is trivially its own orchestrator |

## Consequences

**Easier.** Removes an entire class of "what if there's no orchestrator" questions — a notification
routing decision, an empty-state screen — none of them need to exist, because the state they'd
handle can't occur.

**Harder.** Removing a worker now sometimes requires a second step (reassign, then remove) instead
of one. This is a deliberate trade — a silently-orphaned room with no default addressee is worse
than one extra click on the uncommon path of removing the specific worker who currently holds the
role.

**Obliges us to.** Update `docs/design/01-definition.md`'s orchestrator entry and
`docs/design/02-screens.md`'s reassignment/removal sections to state the invariant explicitly,
rather than leaving it implicit.

Relates: `docs/design/01-definition.md` (the orchestrator definition this record extends),
`docs/design/02-screens.md` (the reassignment/removal UI this record's rule governs),
[0033](0033-skills-attach-directly-no-persona.md) (the skill-attachment mechanism orchestrator
authority now uses).
