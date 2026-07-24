# 0024 — Commands are namespaced by owner, and `/ask-all` is the broadcast

Status: accepted
Date: 2026-07-24
Amends: [0010](0010-skills-and-advisor.md)

## Context

[0010](0010-skills-and-advisor.md) settled that worker capabilities are **skills** — app-level
canonical, realized per-vendor by the adapter, with native skills passed through. That model stands.
What it does not answer is the question a room with two workers forces and a single-agent tool never
faces, stated in
[`04-workers-commands-control.md`](../design/04-workers-commands-control.md):

> A drop-in replacement has to carry these, and they raise a question single-agent tools never face:
> **in a room with two workers, whose commands does `/` show?**

Both workers may have a `/compact`. They do different things. Neither is wrong. Without an answer,
`/compact` is ambiguous the first time a second worker joins — and being a drop-in replacement
([0012](0012-what-aer-flow-is.md)) means slash commands must work from day one, not after a
disambiguation ritual.

## Decision

**Commands and skills are namespaced by who owns them, in three tiers.**

| Tier | Contains | Goes to |
|---|---|---|
| **Room** | `/add`, `/shape`, `/files`, `/usage` — and AER's canonical skills | the room; always works |
| **Each vendor** | that vendor's own commands and native skills, grouped under its name | that worker only |
| **Everyone** | `/ask-all` | every worker at once |

**1. Room commands act on the room and always work.** They are also the discoverable surface for
everything else — the corpus notes that `/files`, `/usage` and `/shape` open the panels, *"which means
those surfaces have a keyboard path and do not depend on finding an icon."*

**2. A vendor's commands are grouped under that vendor and go to that worker**, so `/compact` is
unambiguous even when both have one, *"and nobody has to learn which tool a command came from."*

**3. Canonical skills appear under Room; native skills appear under their vendor; both are marked as
such.** This is 0010's two tiers made visible. The corpus's framing, from
[`06-answers.md`](../design/06-answers.md): *"Skills that AER defines are realised per vendor and
appear under 'Room' — they work with whoever you address, which is what makes them worth defining
once."* The palette already namespaces by owner, so this needs no new structure — it needs the Room
tier populated by AER's canonical skills rather than only by built-in commands.

**4. Skills and commands are shown together and marked**, because *"the distinction is the vendor's,
not the user's. What matters to a person is 'what can I type here', not which mechanism implements
it."*

**5. `/ask-all` puts one question to every worker and lays the answers side by side.** It is
deliberately **a command, not a mode** — you drop into it for one message and out again. The corpus
calls it *"the cheapest way to get value from a multi-worker room"*, and it is differentiating claim
**08**: *"one question, two answers side by side, disagreeing"* is more informative than either alone.

**6. On a phone there is no slash palette.** Typing `/` to discover commands is a keyboard idiom that
does not survive a touch keyboard. The same commands, with the same namespacing, become an **Actions
sheet reached from the room header** — browsed rather than typed. The corpus is explicit that nothing
is lost, *"because the phone's job is deciding and watching, not driving."*

### Why `/ask-all` is not routing

Broadcasting to *everyone* requires no judgement about who should answer, so it does not read the
conversation to choose a recipient — it declines to choose. That keeps it on the right side of
CLAUDE.md Architecture Rule 1 and of [0019](0019-consulting-is-not-deciding.md)'s rule that routing is
a control and never an inference. Worth stating because "ask all" and "ask the right one" sound
adjacent and are opposites: the second is the thing the product must never do.

## Consequences

**Easier.** A person moving from a single-agent tool types the commands they already know and they
land where expected. Adding a second worker adds a tier rather than creating a collision. And
`/ask-all` gives the multi-worker room an immediate payoff that costs one command to reach — the
corpus's *"cheap to love"*.

**Harder.** The palette must enumerate each vendor's commands and native skills, which means asking a
CLI what it offers — capability discovery per vendor, behind Adapter Isolation, and a surface that
degrades sensibly when a vendor reports nothing. `/ask-all` multiplies quota: one keystroke spends a
turn on every worker in the room, against real subscriptions, so its cost has to be as visible as its
convenience ([0019](0019-consulting-is-not-deciding.md) makes the same point about consultation). The
phone's Actions sheet is a second presentation of the same namespaced set and must not be allowed to
drift from it.

**Obliges us to** namespace by owner rather than merging into one flat list; keep Room commands
working regardless of who is present; mark canonical and native skills distinctly while listing them
together; render `/ask-all`'s answers side by side rather than as sequential turns that read like a
conversation between workers; keep the phone's Actions sheet generated from the same command set; and
never infer a recipient from message content.

**Relates to** [0010](0010-skills-and-advisor.md), which this amends — 0010 owns what a skill *is*,
this owns how it is addressed. [0019](0019-consulting-is-not-deciding.md) governs asking one worker;
this governs asking all of them. [0013](0013-room-is-the-user-facing-noun.md) supplies the tier's
name.

Related: `#386` (skills — canonical packages realized per-vendor), `#385` (the advisor preset, which
0010 makes the first canonical skill), `#268` (keyboard-first triage — `/` and `@` are part of that
map).
