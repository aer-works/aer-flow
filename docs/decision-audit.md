# Decision audit — every record re-checked against what #527 measured

**Date: 2026-07-25.** Population: every numbered record in [`decisions/`](decisions/). Disposition
vocabulary is fixed — `unaffected`, `amended`, `superseded`, `rewritten` — and enforced by
`pixi run audit-completeness`, which also requires a reason in its own column.

This exists because the audit falsified vendor claims that several decisions were built on, and
"which decisions did that break?" had to be recovered by re-reading everything. That recovery is
what made [`Rests on`](decisions/README.md#rests-on--the-load-bearing-facts-and-what-would-falsify-them)
mandatory going forward. This sweep is the one-time equivalent for the records written before it.

## What "unaffected" means here, and what it does not

**It is a claim about dependency, not about quality.** A record is `unaffected` when nothing the
audit measured is load-bearing for it — most of these decisions are about product shape (nouns,
surfaces, ordering, vocabulary) and rest on no vendor behaviour at all. Reading a sweep row as "this
record was re-validated" would be wrong; the honest reading is "the audit's findings do not reach
it."

**The blind spot this cannot cover:** a decision resting on a vendor fact nobody thought to check
looks identical to one resting on no vendor fact. Enumeration cannot find its own blind spot. What
narrows it is that the audit gave all 382 mirrored pages a disposition rather than sampling — but
that bounds the *documentation*, not reality.

## The sweep

| # | disposition | why |
|---|---|---|
| 0001 | unaffected | Two nouns (workflow, session). Vocabulary and object model; no vendor mechanism is load-bearing. |
| 0002 | unaffected | One vocabulary, no translation map. A rule about our own naming, unreachable by vendor findings. |
| 0003 | unaffected | Templates collapse to three shapes. Authoring-model decision, independent of how workers are driven. |
| 0004 | unaffected | Permission scopes are pre-declared policy. 0029 changes the runtime *mechanism*; the policy axis 0004 governs is explicitly the other one, as 0015 already recorded. |
| 0005 | amended | Seam milestones alternate with capability ones. Still right, but the audit adds a seam the sequence did not have: the gate self-check of 0029 and 0030. Carried into the milestone re-verification rather than edited here. |
| 0006 | unaffected | Visual direction is Quiet. Presentation rules; nothing measured touches them. |
| 0007 | unaffected | Background work: inline glance, dedicated surface for depth. Surface-shape decision, no vendor dependency. |
| 0008 | amended | Live streaming over a durable append log. Unchanged in principle, but two measurements bear on it: two processes cannot write one transcript, and `--session-id` is an existence check rather than a lock, so the single-writer guarantee is AER's to enforce. |
| 0009 | unaffected | Session lifecycle and retention — a tree you count the top of. Retention policy, independent of vendor behaviour. |
| 0010 | unaffected | Worker capabilities are skills; the advisor is the first. Capability model; the audit found nothing that reaches its structure. |
| 0011 | unaffected | Token-based context management. Already corrected by 0027 on its own terms; the audit measured nothing further about context accounting. |
| 0012 | unaffected | What AER Flow is. The product thesis. Finding 4 (an API key silently disables Remote Control) *strengthens* its premise rather than changing it. |
| 0013 | unaffected | Room is the user-facing noun. Naming decision. |
| 0014 | unaffected | A shape is an ordered list that renders as a graph. Authoring model; unrelated to worker control. |
| 0015 | amended | Its three kinds of pause stand; its **mechanism** guidance — prefer `--permission-prompt-tool` — is amended by 0029, because the gate turned out to be three mechanisms covering three tool populations. |
| 0016 | unaffected | Memory belongs to the room, not the worker. Ownership decision; the audit measured nothing about memory. |
| 0017 | unaffected | Vendor, model and effort are three separate choices. Its effort clause was already corrected by 0023; nothing measured touches the split itself. |
| 0018 | amended | Its bands and its "notifications never decide" rule stand; the notification **source** it assumed (a vendor event) does not exist headless, and 0030 supplies the answer. |
| 0019 | unaffected | Consulting is not deciding. A gate-semantics rule about who may be asked; 0029 changes what enforces the gate, not who can be consulted while it is open. |
| 0020 | unaffected | One state machine, every surface renders the room's state. Architecture rule about our own surfaces. |
| 0021 | unaffected | Artifacts are files: vendor-neutral, versioned, attributed. The audit reinforces vendor-neutrality but changes nothing. |
| 0022 | unaffected | The permission ladder is offered at the moment of asking; denial is an answer. 0015's measured contract already showed denial messages reach the model verbatim, which supports this rather than altering it. |
| 0023 | unaffected | Effort named by behaviour, models offered by purpose. Its disclosed-collapse rule is *reused* by 0029 for the gate, which extends its reach without changing it. |
| 0024 | unaffected | Commands are namespaced by owner. Addressing decision, no vendor dependency. |
| 0025 | unaffected | A step's instruction is its body; "ask me first" is a toggle. The toggle's *enforcement* is 0029's subject; the authoring shape here is untouched. |
| 0026 | unaffected | Running out of plan is a state with a reset time. `errorCode: "credits_required"` is the typed signal it needs and confirms the shape it assumed. |
| 0027 | unaffected | Context belongs to the worker; running out is a choice. The audit measured token *accounting* (subagent exclusion), not context ownership. |
| 0028 | unaffected | No permissive control is ever the default. The audit supplies a sharp instance — `auto` is the convenient mode that removes the gate — which illustrates the rule rather than amending it. |
| 0029 | rewritten | New record. States the gate as three mechanisms with three populations, replacing 0015's single-mechanism guidance, and carries its own `Rests on`. |
| 0030 | rewritten | New record. AER is its own notifier, because both vendor events 0018 assumed are silent under `-p`. Carries its own `Rests on`. |

## Decisions whose dependencies are now recorded

0029 and 0030 carry `Rests on` tables. The remaining 28 predate the requirement and are not being
retrofitted wholesale — that would manufacture dependency tables from memory, which is the failure
mode the requirement exists to prevent. The rule going forward: **a record acquires a `Rests on`
table when it is next amended**, written from what is then known rather than reconstructed.

Two rows in the new tables are worth surfacing because they are marked **assumed**, not measured:

- **Hooks on Windows run through Git Bash and have historically failed silently there.** Windows is
  the primary development host and the mandatory `PreToolUse` hook of 0029 sits on top of this. It
  is the highest-value unrun check in the set.
- **A second concurrent login against one subscription is permitted.** Not measurable from an agent
  session; it needs the account owner. Per-worker config roots depend on it.

## Recomputing this

```
pixi run audit-completeness
```

It fails if any record on disk lacks a row, if a row's disposition is not one of the four words, if
a row gives no reason, or if either of 0029/0030 loses its `Rests on` table. It cannot check whether
a *reason* is correct — only that one was given.
