# 0019 — Consulting is not deciding: you can ask anyone, and the gate stays open

Status: accepted
Date: 2026-07-24

## Context

When a worker stops and asks for a judgement — approve this, choose that, may I do this — every tool
in this category makes **the moment of asking the moment of committing.** The prompt has two buttons,
they both close it, and the only way to get another opinion is to leave, ask elsewhere, come back and
answer from memory. So the honest response to "I don't fully understand this diff" is either to
approve it anyway or to abandon the run.

That is the anxious pause this product exists to remove, and it is not a UI problem. It is a
structural one: the gate and the answer are the same object, so consulting *is* deciding.

Meanwhile the product already has the thing that would fix it — **more than one model, on
subscriptions you already pay for** ([0012](0012-what-aer-flow-is.md)). A second opinion is one
worker away. What was missing was permission to seek it without spending the decision.

This record was written from the M25 design corpus (`docs/design/`), which calls it *"the single most
important behaviour in the room model"* and *"if only one thing survives contact with implementation,
it should be this."* It was **absent from the repo entirely** until now — the first extraction
produced decision records and dropped what was not decision-shaped; see
[`docs/design/coverage-audit.md`](../design/coverage-audit.md).

## Decision

**Asking is a separate act from answering, and only the operator's answer closes a gate.**

Four parts, none of which works alone:

**1. You can put the question to anyone — including a worker not yet in the room.** Selecting a
worker that is not a participant *adds it*, and the question is its first turn. There is no separate
"manage participants" surface, because bringing someone in and asking them something are the same
gesture.

**2. The gate stays open while you consult.** Ask one worker, then a second, then a third; the
pending decision is untouched by all of it. Their answers arrive as turns in the room, attributed,
alongside the original. You can ask three and have decided nothing. **Nothing but your answer closes
the gate** — not a consulted worker agreeing, not all of them agreeing.

**3. The second opinion is formed on the same evidence, and you can see what that is.** A consulted
worker receives a **summary of the room, plus the raising turn and its attachments verbatim**, and it
can query for more ([#424](https://github.com/aer-works/aer-flow/issues/424)). What it is being given
is **listed before you send**, and every item can be removed or added.

That disclosure is what makes this trustworthy rather than magic. A second opinion formed on *your
paraphrase* is worth little; one formed on the same diff is worth a lot; and one formed on evidence
you cannot inspect is worth nothing, because you cannot tell which of the two it was.

**4. Routing is a control, never an inference.** You choose who answers. The product does **not**
read the conversation to decide who should respond, rank workers by suitability, or auto-route by
topic. This is CLAUDE.md's Architecture Rule 1 — *Flow carries discipline, Workers carry
intelligence* — surfacing in the interface: the moment the product picks the responder by reading
content, it is parsing conversation to make a routing decision.

### Why the evidence rule is "summary + verbatim + queryable" and not one of the three

The design pass considered three options for what a worker added at turn 104 receives, and took two
of them together rather than choosing:

- **Nothing** makes the answer worthless. A worker asked "is this correction necessary?" with no
  context produces a plausible-sounding guess, which is worse than no second opinion because it looks
  like one.
- **All 103 turns** is expensive and mostly irrelevant. A reviewer asked about one patch does not
  need the whole exploration, and on a metered context window the cost lands on the operator.
- **A summary alone** loses exactly the thing under dispute — the diff, the file, the failing output.

So: the **context compressed, the evidence in full**, with querying as the escape hatch when the
worker itself decides it needs more. That last part is why `#424` matters here and not only as an
integration: it turns "did it have enough context?" from the operator's guess into something the
worker resolves.

## Consequences

**Easier.** The most uncomfortable moment in using an agent — approving something you do not fully
understand — becomes two clicks and a second opinion. *"I'm not sure, what does the other one think?"*
stops being a workaround and becomes a first-class move. This is the clearest answer the product has
to "why not just use one agent", and it is unavailable to a single-vendor tool by construction.

**Harder.** A gate is now a **long-lived object with its own conversation**, not a modal awaiting one
of two answers. It must survive consultation turns, render them in order, attribute them, and still
be answerable — and it must survive a crash while doing so, which is
[0015](0015-three-kinds-of-needs-you.md)'s ask-time persistence obligation. A consulted worker also
costs real quota against a real subscription, so the disclosure list is not only a trust affordance,
it is a cost affordance.

**The failure mode to design against is the opposite of the usual one.** Everywhere else the risk is
a prompt so frictionless it trains a click-through reflex. Here the risk is that consulting feels so
cheap it becomes procrastination — three opinions and no decision. The gate must therefore stay
visibly *pending*, and the consulted answers must not accumulate into something that looks like a
resolution.

**Obliges us to** keep the responder chosen by the operator and never inferred (Rule 1); disclose the
evidence bundle before it is sent, itemised and editable; leave the gate open until the operator
answers it, with no quorum, no consensus and no "all agreed" shortcut; and persist a consulted
gate exactly like an unconsulted one.

**Relates to** [0015](0015-three-kinds-of-needs-you.md) — this record governs what you may do *while*
a pause is open; 0015 governs what kind of pause it is. Also
[0016](0016-memory-is-room-owned.md): a consulted worker reads the room's memory, which is why memory
belongs to the room rather than the worker that wrote it.

Related: `#424` (AER's own state as a queryable context source), `#445` (the mechanism a permission
pause is raised through), `#385` (the advisor preset, which is one use of this and not a separate
concept).
