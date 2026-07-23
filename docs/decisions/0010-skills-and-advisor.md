# 0010 — Worker capabilities are skills; the advisor is the first one

Status: accepted
Date: 2026-07-23

## Context

Two capabilities were raised together, and they turn out to be one architecture question.

**The advisor.** The owner wants AER to do automatically what a human does with a reviewer model:
one model supervises/critiques another's work in the room. The valuable version is **cross-vendor** —
Opus advising Gemini Flash, or the reverse. No single vendor's own advisor skill can do that; it only
advises its own model. So the AER-native advisor is strictly more powerful than any vendor's, and it
is the shape the product is already built for (Case-2 dialogue workers; the room model,
[0001](0001-two-nouns-workflow-and-session.md)).

**Skills.** As a Claude-Code-replacement, AER needs a skills story, and the ecosystem is converging on
"a set of agent skills you carry." Today only *discovery* exists (#263 surfaces a vendor's native
skills). The open question was **pass-through** (surface each vendor's native skills) versus a
**unified abstraction** (AER-owned skills that run on any vendor) — the first easy, the second the one
that fits AER's subscription-first, vendor-neutral thesis.

The connection: the advisor is best understood as *a skill* — a packaged behaviour you attach to a
worker. If participant behaviour is hardcoded now, both the advisor and any skills system get
retrofitted later. The affordance is cheap to preserve and expensive to add back.

A unified skill abstraction sounds like a large new subsystem, but it is the pattern AER **already
runs on**: a skill is *instructions + tool requirements + bundled assets*, and translating a canonical
concept into each vendor's native mechanism is exactly Adapter Isolation (CLAUDE.md rule 2) — the same
seam that translates a permission grant into `--allowedTools`/`--disallowedTools`
([0004](0004-permission-scopes.md), #331).

## Decision

**Worker capabilities are skills. Skills are app-level and canonical; the adapter realizes them
per-vendor. Participant behaviour is a skill/role binding. The advisor is the first skill.**

- **App-level canonical skills.** AER owns a vendor-neutral skill format. At dispatch the **adapter
  realizes it for the chosen vendor** — write a `SKILL.md` into a scoped skills directory for Claude,
  the plugin/agent equivalent for Gemini — with **prompt-injection as the graceful floor** when a
  vendor cannot load an ad-hoc skill (the same shape as [0004](0004-permission-scopes.md)'s fail-closed
  floor: the capability degrades in a defined way, it does not silently vanish). This is the portable,
  shareable "skill set you carry."

- **Native vendor skills are pass-through.** Each vendor's own skills stay discoverable and invokable
  (#263 already discovers them) as an escape hatch for vendor-specific power the canonical format
  can't express. App-level and vendor-level are **layers, not a choice** — app-level skills are
  *delivered through* the vendor mechanism by the adapter.

- **Participant behaviour is a named role/skill binding, not a hardcoded prompt.** The room model
  ([0001](0001-two-nouns-workflow-and-session.md), #333/#340) must model "what this participant does"
  as a binding, so a skill (or the advisor) slots on with no object-model change.

- **The advisor participant is the exemplar skill** (#385): a cross-vendor supervisor/critic attached
  to a worker. Its seam is #340 (derived sessions — "start a review from inside a session"), made
  *programmatic* rather than only human-initiated.

## Consequences

**Easier.** One authored skill runs on every vendor. The advisor works cross-vendor, which no vendor's
own skill can. Skills become portable and shareable — the thing that matters the moment AER has a user
who isn't its author. And it all rides the adapter seam that already exists, not a new subsystem.

**Harder.** The canonical skill schema has to be defined, and each adapter's realization verified —
`claude` can load a `SKILL.md`; whether `agy` can accept an injected skill (vs. falling back to
prompt-injection) is unverified and must be checked before app-level skills are promised for Gemini.

**Obliges us to.** Preserve the binding affordance in [0001](0001-two-nouns-workflow-and-session.md)'s
build (#333/#340) — this is the one thing the in-flight re-architecture must not foreclose. Count (or
deliberately exempt) advisor spawns against the depth/count ceiling
([0009](0009-session-lifecycle-and-retention.md)), or "advise everything" blows the safety rail.
Attribute cost per participant in the cross-vendor usage view (J9,
[0008](0008-runtime-streaming-over-append-log.md)), since an advisor roughly doubles model calls. The
advisor is a read-only step, so its enforcement is already covered by
[0004](0004-permission-scopes.md) + #331.

**Leaves open (build details).** Whether to ship vendor pass-through first (cheap; #263 exists) and let
the canonical layer prove itself against real skills, or build canonical first; the canonical schema
itself; where app-level skills are stored and scoped (project vs. app). None decided here — tuned when
M26 is scoped.

Relates: [0001](0001-two-nouns-workflow-and-session.md) (room model, participants),
[0004](0004-permission-scopes.md) (the adapter-translation seam, fail-closed floor),
[0008](0008-runtime-streaming-over-append-log.md) / [0009](0009-session-lifecycle-and-retention.md)
(cost and the spawn ceiling), #385 (advisor), #386 (skills), #263 (native discovery), #333/#340 (the
binding affordance), J9, J6.
