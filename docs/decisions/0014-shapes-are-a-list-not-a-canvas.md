# 0014 — A shape is an ordered list that renders as a graph

Status: accepted
Date: 2026-07-24

[0025](0025-a-step-is-an-instruction-with-a-gate-toggle.md) says what a *step* contains: the
instruction is the step's body, previous output flows in implicitly with no template language, and
"ask me first" is a property of a step rather than a node type.

## Context

[0003](0003-templates-collapse-to-three-shapes.md) collapsed the template catalogue to three shapes
and made the pipeline one of them. That left an unanswered question the ground-up design pass had to
face directly: **what does authoring a pipeline actually look like**, and how much of the product is
it?

The running product answered "a freeform canvas" — draggable nodes on an open 2D surface, with #266
open to polish it. Three things make that the wrong default:

- **It cannot be the day job.** [0012](0012-what-aer-flow-is.md) commits to a coding agent you talk
  to; a graph canvas is a place you *build a thing*, not a place you *work*. Living in the canvas is
  precisely what [0012](0012-what-aer-flow-is.md) rules out ("not a workflow builder you live in").
- **It does not survive the phone.** A drag-to-place canvas with x/y coordinates has no honest small
  form. The product is used from a phone by the same operator (`spec/journeys.md`), and a surface
  that only exists on desktop splits the model in two.
- **Free 2D placement diffs as noise.** Two functionally identical pipelines whose nodes sit at
  different coordinates produce different files. A saved shape that a person edits and reuses
  ([0003](0003-templates-collapse-to-three-shapes.md)) has to version like source, and coordinates
  are not source.

## Decision

**A shape is an ordered list of steps. It is *rendered* as a graph; it is not *authored* as one.**

- **The list is the source of truth.** Order and each step's declared `DependsOn`
  (`src/Aer.Flow/Domain/WorkflowDefinition.cs:14-21`) determine the graph. Position is computed from
  dependencies, never stored. Two shapes with the same steps and edges are the same file, always.
- **A step's instruction is its body.** The thing you write into a step is the worker's instruction —
  the list is read top to bottom as the plan in prose, not a diagram you decode. The graph is the
  *picture* of that list, offered for inspection, not the thing you type into.
- **It is keyboard-navigable and works on a phone**, because a list is. Add a step, reorder, set a
  dependency — all list operations, available on both surfaces. **Express it GitHub-style: each step
  declares what blocks it.** Default is blocked by the step above, so a linear shape — the overwhelming
  majority of authored shapes — needs zero dependency editing. Naming a different (or no) blocker is
  the one extra list operation that expresses fan-out and fan-in, and parallelism is *emergent*: the
  engine already runs anything whose blockers are satisfied
  (`src/Aer.Flow/Scheduling/DependencyResolver.cs`'s `GetReadySteps` returns a set, not a single step).
- **The graph view is for reading, not building** — inspect a template before starting it, or watch
  a running pipeline light up step by step. Same rendering, live.

This is the concrete form of [0012](0012-what-aer-flow-is.md)'s third commitment ("shapes you can
draw"): quick, visual, saveable — a *list* you can draw, not a canvas you must lay out.

### Fan-out is a first-class requirement, not a deferred cost

**Corrected 2026-07-25 (#503, item 5).** This section originally argued the opposite — that parallel
fan-out has no first-class authoring gesture and that a linear list only expresses it "legibly," with
a drawing gesture deferred to later. That was wrong on the fact it rested on: fan-out is not rare.
This project's own working method — several agents dispatched in parallel on different briefs,
reporting back — is the multi-model product's most natural shape, not an edge case worth deferring.
A list does not need a drag-two-branches-apart gesture to express this: naming a step's blocker(s) is
already a list operation, exactly as ordinary as reordering, and the default (blocked by the step
above) means a linear shape costs nothing extra. There is no gap here to accept.

## Consequences

**Easier.** A shape diffs, reviews and merges like code. The same list renders identically on desktop
and phone, so there is one authoring model, not two. Watching a run is the same view as authoring
one, which halves the surface to build and learn.

**Harder.** Auto-layout has to be good — a computed graph that tangles its own edges is worse than no
graph. Composing more than one blocker's output, or reading from a step other than a direct blocker,
still has no expression in this model ([0025](0025-a-step-is-an-instruction-with-a-gate-toggle.md)) —
that is the real, named limitation a power user will hit; it must be documented, not discovered.

**Obliges us to** treat the freeform canvas as **out of scope — retired #266**, closed. The authoring
surface is rebuilt as a list, touched wherever a milestone's own journeys require it (no layer is
out-of-bounds by default — see plan.md's "Scope of the rebuild"), and the live pipeline view reuses
the same renderer over [0008](0008-runtime-streaming-over-append-log.md)'s stream.

**Does not change** what the three shapes *are* ([0003](0003-templates-collapse-to-three-shapes.md))
— only how a pipeline is authored and shown. Conversation and dialogue were never canvases; this
brings pipeline into line with them.
