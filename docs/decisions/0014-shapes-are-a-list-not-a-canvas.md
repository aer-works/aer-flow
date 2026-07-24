# 0014 — A shape is an ordered list that renders as a graph

Status: accepted
Date: 2026-07-24

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
  dependency — all list operations, all available on both surfaces.
- **The graph view is for reading, not building** — inspect a template before starting it, or watch
  a running pipeline light up step by step. Same rendering, live.

This is the concrete form of [0012](0012-what-aer-flow-is.md)'s third commitment ("shapes you can
draw"): quick, visual, saveable — a *list* you can draw, not a canvas you must lay out.

### The cost we are accepting

**Parallel fan-out has no first-class authoring gesture yet.** A linear list expresses "A then B then
C" naturally and "A, then B and C in parallel, then D" only through each step's `DependsOn` — legible
when read, but there is no drag-two-branches-apart gesture the way a canvas has. We accept this: the
overwhelming majority of authored shapes are short and mostly linear, parallelism is still fully
*expressible* through dependencies (the engine already runs it), and a later affordance for drawing a
fan-out is additive. Buying phone-parity, clean diffs, and keyboard authoring is worth a fan-out
gesture we can add when a real shape needs it.

## Consequences

**Easier.** A shape diffs, reviews and merges like code. The same list renders identically on desktop
and phone, so there is one authoring model, not two. Watching a run is the same view as authoring
one, which halves the surface to build and learn.

**Harder.** Auto-layout has to be good — a computed graph that tangles its own edges is worse than no
graph. And the fan-out gap above is a real, named limitation a power user will hit; it must be
documented, not discovered.

**Obliges us to** treat the freeform canvas as **out of scope, which likely retires #266** (polish
for a canvas we will not build — verify against its current body before closing). The authoring
surface is rebuilt as a list ([0012](0012-what-aer-flow-is.md)'s UI-only rebuild), and the live
pipeline view reuses the same renderer over
[0008](0008-runtime-streaming-over-append-log.md)'s stream.

**Does not change** what the three shapes *are* ([0003](0003-templates-collapse-to-three-shapes.md))
— only how a pipeline is authored and shown. Conversation and dialogue were never canvases; this
brings pipeline into line with them.
