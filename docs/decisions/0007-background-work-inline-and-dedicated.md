# 0007 — Background work surfaces both inline and on a dedicated surface

Status: accepted
Date: 2026-07-22

## Context

The evaluation named one design question as the largest still open: *where do background
and running work — and the outputs they produce — actually live?*

AER has the data. Running executions, artifacts and lineage all exist and are correct. But they
are buried in a per-step drill-in behind `TaskView`'s **"Details — the full record"** expander, and
a *running* task shows the phone nothing at all ("Nothing is waiting on you — running"). Background
work is effectively invisible unless the user already knows which task to open and which expander to
expand. That is the same disclosure failure as the four corrected findings, applied to the thing the
product is most for: knowing what your machine is doing while you are not watching it.

The reference products each answer this with **two** surfaces, not one:

- **Claude Code** — *background tasks* (the running/queued/finished work) **and** *artifacts* (the
  files that work produced).
- **Antigravity** — *implementation plans* **and** *task documents* (the structured work products).

## Decision

Progressive disclosure across three levels, not a binary. Each step reveals more without discarding
what came before, and the middle step happens **in place** — the user is never forced to navigate
away just to see a little more.

- **Glance** — the switcher row (#336 / #337): latest activity and status, so the list answers
  *"what is each thing doing right now"* without opening anything.
- **Expand in place** — the row expands where it sits to show more of that item's activity and its
  outputs, without leaving the list. This is the deliberate correction to today's failure mode,
  where the only way to see anything is a hard drill-in through `TaskView` and a collapsed "Details"
  expander. Cheap curiosity should cost a tap, not a navigation.
- **The dedicated surface** (#360): the full activity feed — running, queued and recently-finished
  executions as their own list — **and** the durable outputs of that work as first-class, openable
  objects. This is the depth, and the place to see everything in flight across the machine at once.

The three are one continuum. A user glances at the list, expands a row in place when a line catches
their eye, and opens the dedicated surface when they want the whole picture or the full output — but
is never made to jump to the deep end to answer a shallow question. The dedicated surface is what
promotes executions and artifacts out of the "Details" expander into a place of their own; the
expand-in-place step is what stops the list from being a dead end that forces a drill-in.

## Consequences

**Easier.** A running task becomes legible from the list without opening it — which is the specific
thing the phone cannot do today. Outputs stop being buried three levels down; the product's own work
products become things you can return to, not things you have to reconstruct a path to.

**Obliges us to.** Build the dedicated surface as new scope (#360) — it does not exist in any form
today. It must work on **both platforms as one design**, a density scale over shared tokens
(decision 0006 / #345), not a second design. And it must **reuse the existing data** —
executions, `ArtifactManager` lineage — rather than introducing new engine concepts; this is a
presentation surface over what `TaskProjectionLoader` and the Details expander already read.

**Leaves open.** The user-facing *names*. "Artifacts" is already the internal word (`artifacts/`,
`ArtifactManager`); whether it survives as user-facing text is for the vocabulary lint (#315) to
decide under decision 0002, not to assume here. The decision is the two-layer **structure**; the
labels are a build detail.

**Supersedes** the assumption, implicit in the current IA, that a running task's state and its
outputs are only ever reached by opening that one task and drilling into it.

Related: #283 (index), #360 (the dedicated surface), #336 / #337 (the inline layer), #340 (derived
sessions, whose outputs land here), #334 (the states an activity feed renders), #345 / #315.
