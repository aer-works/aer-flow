# 0032 — A room always has exactly one orchestrator

Status: accepted
Date: 2026-07-26

## Context

`docs/design/01-definition.md` defines the room orchestrator as *"a pinnable role: which worker in
the room is authorized to call `aer decide` on another's gate, standing in for the human.
Human-assigned only, blocked while a gate is open, one per room at a time, and never retroactive."*

That covers *reassigning* the role in detail, but a design-readiness audit of the full M27 corpus
found it silent on two adjacent questions: how the *first* orchestrator gets assigned when a room
is created, and what happens if the current orchestrator is *removed from the room* (not
reassigned). Both are real gaps — an engineer implementing room creation or worker removal has
nothing to build against.

## Decision

**A room cannot exist without an orchestrator, and the current orchestrator cannot be removed
directly.**

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
  orchestrator means having the orchestrator Skill attached — "you may call `aer decide` on
  another worker's gate, standing in for the human" — which AER attaches automatically to whichever
  worker currently holds the role, and detaches on reassignment. This is the same skill-attachment
  mechanism [0033](0033-skills-attach-directly-no-persona.md) uses for every other worker
  capability, not a bespoke pinned-role flag. A worker can hold the orchestrator skill alongside any
  others it has attached. Nothing about it depends on which vendor or model that worker is running.

## Consequences

**Easier.** Removes an entire class of "what if there's no orchestrator" questions — a gate
awaiting orchestrator sign-off, a notification routing decision, an empty-state screen — none of
them need to exist, because the state they'd handle can't occur.

**Harder.** Removing a worker now sometimes requires a second step (reassign, then remove) instead
of one. This is a deliberate trade — a silently-orphaned room with no one authorized to act on
gates is worse than one extra click on the uncommon path of removing the specific worker who
currently holds the role.

**Obliges us to.** Update `docs/design/01-definition.md`'s orchestrator entry and
`docs/design/02-screens.md`'s reassignment/removal sections to state the invariant explicitly,
rather than leaving it implicit.

Relates: `docs/design/01-definition.md` (the orchestrator definition this record extends),
`docs/design/02-screens.md` (the reassignment/removal UI this record's rule governs),
[0033](0033-skills-attach-directly-no-persona.md) (the skill-attachment mechanism orchestrator
authority now uses).
