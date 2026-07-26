# 0041 — Phone template authoring ships with the shapes milestone, not deferred past it

Status: accepted
Date: 2026-07-26

## Context

`docs/design/02-screens.md` says, describing the phone's earliest screens (pairing, notifications,
before it has any folder of its own): *"Template authoring is out of scope for the phone's first
version, not ruled out."* Read in isolation this is ambiguous about *when* it stops being out of
scope — and it was misread that way once already in this session: a prior pass, fixing an unrelated
audit finding, took it as a standing exclusion and rewrote `docs/plan.md`'s M29 criterion to match
(desktop authors, phone only starts/watches) — directly contradicting
[J17](../../spec/journeys.md) (*"Author a shape on a phone, start it on the desktop, watch it run"*),
one of the product's actual required use cases, not a nice-to-have. [0014](0014-shapes-are-a-list-not-a-canvas.md)'s
own justification for a step-list over a canvas is phone-nativeness — J17 is that decision's payoff
arriving, not a deferred extra.

**The restatement itself was the defect, not just the wrong guess.** The same fact — does the phone
ever get template authoring, and when — got written independently in `02-screens.md`'s prose,
`plan.md`'s M29 criterion, and `plan.md`'s "The Bar" clarifying paragraph. When it turned out wrong,
all three needed separate fixes, and nothing had forced them to agree in the first place. This record
exists to be the one place this fact lives; everywhere else references it rather than restating it.

## Decision

**"The phone's first version" (`02-screens.md`) means the pairing/chat-only milestones before shapes
exist as a capability at all (M26–M28) — not a standing exclusion. Phone template authoring ships
with the milestone that ships shapes, per J17, with no gap between "shapes exist" and "shapes are
phone-authorable."**

- `docs/design/02-screens.md`'s phone-authoring line should read as scoped to *before shapes exist*,
  citing this record for the timeline rather than restating it.
- `docs/plan.md`'s M29 criterion is J17 verbatim (authored on a phone, started on the desktop) — not
  a paraphrase, and not something a future pass should re-derive or re-decide without reading J17
  first.
- `docs/plan.md`'s "The Bar" parity clarification should not use template authoring as an example of
  a legitimate day-one scope difference — it isn't one; it's a sequencing fact (arrives at M29, not
  M26), which is a different kind of claim than a permanent surface-exclusive gap.

## Consequences

**Easier.** One place to check "does the phone get template authoring, and when" — a future session
reads this record instead of reconciling three independently-worded passages that may have drifted.

**Harder.** Nothing new — this removes a restatement, it doesn't add a constraint.

**Obliges us to.** Check other facts this session wrote into more than one document independently
(the M27 UX dialogue's conclusions, the readiness-pass fixes) for the same restate-instead-of-reference
pattern, now that it's been caught once for real.

Relates: [J17](../../spec/journeys.md) (the required use case this resolves against),
[0014](0014-shapes-are-a-list-not-a-canvas.md) (phone-nativeness is this decision's whole
justification), `docs/design/02-screens.md` (the ambiguous line this corrects the reading of).
