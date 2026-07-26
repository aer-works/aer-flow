# 0040 — Within "needs you," gates group by kind, and only an action can say "later"

Status: accepted
Date: 2026-07-26

## Context

An independent alignment audit (Gemini 3.1 Pro, dispatched through `Aer.Cli` over the whole
`docs/design/` ↔ `docs/plan.md` ↔ `docs/decisions/` corpus) found that `docs/design/04-workers-commands-control.md`
draws a specific "needs you" screen — gates grouped into three headed sections (Permissions ·
Decisions · Actions), each with its own resolution affordance, and an "Actions" item alone offering
a **Later** deferral — with no decision record behind it. Checked against [0018](0018-attention-is-the-primary-signal.md),
which is the closest candidate: 0018 governs *between-room* ordering (state band, then recency) and
treats "needs you" as a single band containing all three of [0015](0015-three-kinds-of-needs-you.md)'s
kinds together. It says nothing about how gates are presented **within** that band, and nothing about
a kind-specific deferral affordance. This is a real, undecided gap, not an oversight already covered
elsewhere — the "needs you" screen is genuine, reasoned design (`04-workers-commands-control.md`'s own
text argues for it directly: *"a flat list mixes a two-second yes/no with a five-minute judgement call
and makes both feel the same"*) that had never been promoted to a record.

## Decision

**Within the "needs you" band, gates group by 0015's three kinds, and each kind gets its own
resolution affordance. Only an action may be deferred without being treated as urgent.**

- **Grouping is by kind, not by room.** A permission, a decision, and an action are different
  questions with different costs to answer — mixing them in one flat list makes a two-second yes/no
  read the same as a five-minute judgement call.
- **A permission is presented as blocking**, because it is: it stops a worker that is otherwise ready
  to proceed. It sits first.
- **A decision wants deliberation** and offers "ask someone" alongside its direct answers — the
  consult-without-deciding path [0019](0019-consulting-is-not-deciding.md) already establishes.
- **An action alone may say "Later."** Reviewing a diff before it is applied often blocks nothing —
  offering a genuine deferral is honest, and refusing to dress it up as an alarm is what keeps the
  rest of the list credible. A permission or a decision does not get this affordance: deferring a
  permission leaves a worker stalled, and deferring a decision leaves two workers' outputs
  unreconciled — neither is a "come back later" situation the way an already-produced action is.
- **This changes nothing about notification behaviour.** [0030](0030-aer-is-its-own-notifier.md)
  already emits a push from the same durable write that records any pause, regardless of kind — an
  action still gets pushed, it just doesn't jump the queue or read as an alarm once the app is open.
  "Genuinely urgent" describes presentation, not whether the operator is told.

## Consequences

**Easier.** The "needs you" band stays legible as it grows — a person can triage by kind (answer the
quick ones, defer the reviewable ones, sit with the one that needs thought) instead of working a flat
list top to bottom.

**Harder.** The room list (0018) and the within-band grouping (this record) are now two separate
sorting rules a UI has to apply in sequence — state band across rooms, then kind within the band for
a room with more than one pending gate. Both need to agree on what a "kind" is (0015's three), which
they already share.

**Obliges us to.** Treat "Later" as scoped to actions only — a future addition that lets a permission
or decision defer the same way would need its own record, not an extension read into this one.

Relates: [0015](0015-three-kinds-of-needs-you.md) (the three kinds this groups by),
[0018](0018-attention-is-the-primary-signal.md) (the between-room ordering this is the within-band
counterpart to), [0019](0019-consulting-is-not-deciding.md) (the "ask someone" affordance on a
decision), [0030](0030-aer-is-its-own-notifier.md) (why this doesn't change push behaviour).
