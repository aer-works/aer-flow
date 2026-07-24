# 0018 — Attention is the primary signal: state orders the list, notifications never decide

Status: accepted
Date: 2026-07-24

## Context

[0012](0012-what-aer-flow-is.md) commits to a product you can walk away from — several agents working
while you are not watching. That only holds if, on returning, the operator can find *the thing that
needs them* instantly, whether there are three rooms or a hundred. The design pass stress-tested
exactly that: 100 turns in one room, 100 rooms, several running pipelines, three subscription
surfaces at once. Two failure modes fall out of scale, and today's product hits both.

**Recency-only ordering breaks.** The list sorts by last activity, so the room that just needs a
one-word answer sinks below ten rooms that are merely chattering. At a hundred rooms the one thing
asking for you is unfindable — buried by the ones that are fine. Recency answers "what moved last,"
never "what needs me," and at scale those diverge completely.

**Notifications quietly became a decision surface.** A push that says "needs your approval" invites an
answer from the notification itself — and the owner's own note on how the advisor UX should *not*
work (it "blocks new messages" and "shows nothing") is the same failure from the other side:
attention machinery that acts, or obscures, instead of informing. A notification is a summons, seen
in a context with none of the room's evidence; letting it carry the verdict means deciding blind.

## Decision

**Attention — "does this need me, and how badly" — is the product's primary ordering signal, above
recency and everything else.**

**The room list sorts by state first, recency only within a state.** The bands, most-urgent first,
are the pause kinds of [0015](0015-three-kinds-of-needs-you.md) and the status model of
[0006](0006-visual-direction-quiet.md):

1. **Needs you** — permission / decision / approval pending ([0015](0015-three-kinds-of-needs-you.md))
2. **Working** — running, nothing required of you
3. **Idle / finished** — done or waiting, no demand
4. **Quiet states** — cancelled, queued, unavailable (0006's muted band — states you are *not* asked
   to act on)

Within a band, recency orders. So the room that needs a word is always above the room that is merely
busy, no matter which moved last — the property that survives a hundred rooms. This is the list-level
expression of [0006](0006-visual-direction-quiet.md)'s rule that status is the primary information and
reads without colour: the *order itself* now carries urgency, before a single mark is read.

**Notifications inform; they never decide.** A notification reports a state change and takes you *to
the room* to act. It does not carry approve/deny, and answering never happens from the notification
surface. The decision happens where its evidence is — in the room, on a real surface, with the diff
or the question in front of you. This is [0012](0012-what-aer-flow-is.md)'s "not a judge" at the
notification layer: the product routes your attention, it does not stand in for your judgment, and it
never makes the act of deciding cheaper than the act of looking.

This also constrains [0004](0004-permission-scopes.md)'s "inheritance must be visible": a permission
prompt is shown where its effective scope can be shown, which a notification cannot do — so a
permission is *announced* by notification and *answered* in the room.

## Consequences

**Easier.** Returning to a hundred rooms, the top of the list *is* the work queue — sorted by what is
asked of you, not by what twitched last. The common case ([0012](0012-what-aer-flow-is.md)'s simple
path) stays one glance away no matter how much else is in flight.

**Harder.** State-first ordering means the list **reorders as state changes** — a room jumping bands
mid-glance is disorienting if done carelessly. Reordering has to be legible (animate the move, or
settle on a cadence) rather than teleporting rows. And "inform, never decide" costs a round trip: the
operator cannot one-tap-approve from the lock screen, which is a deliberate friction, not an
oversight — the friction *is* the safeguard against deciding blind.

**Obliges us to** derive the sort key from the same status model
[0006](0006-visual-direction-quiet.md) and [0015](0015-three-kinds-of-needs-you.md) already define
(no new state vocabulary), keep notification payloads to *what changed + a link into the room* with no
embedded action, and reconcile with [0007](0007-background-work-inline-and-dedicated.md): that record
governs how deep you see into one item, this one governs the order of the items and how their changes
reach you when you are away.

**Relates to** [0009](0009-session-lifecycle-and-retention.md) — you accumulate the rooms you opened,
"count the top of the tree," so the list this orders is top-level rooms, with children surfaced under
them, not a flat hundred-deep dump.

Related: #336 / #337 (the switcher this orders), #360 (the dedicated activity surface), #448 (the
concurrency cap, whose "queued" is a band here).
