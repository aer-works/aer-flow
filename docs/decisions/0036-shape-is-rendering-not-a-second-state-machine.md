# 0036 — A shape's state is Flow's existing state, rendered differently; not a second state machine

Status: accepted
Date: 2026-07-26

## Context

The M27 UX design dialogue asserted that a Dialogue "collapses to a Conversation" cleanly on human
interruption, and separately that a mid-Dialogue gate either delegates to the Room Orchestrator or
collapses the shape to let a human resolve it directly — but flagged both as undesigned at the
state-machine level, since [0020](0020-one-state-machine.md) requires **every surface renders the
room's state, none derives its own**, which a new, second, shape-specific state machine would
violate on its face.

Checking the real engine (not assumed from the dialogue's own text, which had no source access):
`Aer.Flow`'s `FlowState`/`WorkflowStatus`/`StepStatus` (`src/Aer.Flow/Domain/FlowState.cs`) already
model exactly the shape of state this problem needs — `Running`/`Paused`/`Terminal` at the workflow
level, `Pending`/`Running`/`Succeeded`/`Failed`/`Cancelled`/`Paused`/`Rejected` per step — and Flow
already has working, tested primitives for both mechanics the dialogue asserted: `CancellationRequested`
(`FlowEvent`/`FlowState.CancellationRequestedExecutionIds`) and `PausePoint`/`PausePointKind`/
`DecisionType` (`Resume`/`Reject`/`RetryWithRevision`/`Supersede`, `WorkflowDefinition.cs`).

## Decision

**A room's "shape" is not new state — it is which worker(s)/step(s) are currently bound and running,
read off Flow's existing projection. No second state machine is built for it.**

- **"No workflow step is running" already means Conversation.** This is 0003's own definition of the
  default shape (human, turn by turn, no structure) — it needs no explicit state, only the *absence*
  of a running step, which `WorkflowStatus`/`StepStatus` already expresses.
- **"A Dialogue is running" is one step, running, whose bound worker is the dialogue adapter.** The
  UI reads this the same way it would read any other running step; the "shape" label is a rendering
  choice over existing projected state, not a fact Flow tracks specially.
- **Human interruption "collapsing" a Dialogue to a Conversation is requesting cancellation of that
  running step** — the existing `CancellationRequested` mechanism, already built, already tested.
  Once cancelled, no step is running, and the room is a Conversation again by the first bullet above,
  with no separate transition to design.
- **A mid-Dialogue gate delegating to (or bypassing) the Room Orchestrator is the one genuinely new
  primitive**, and it is not a second state machine either: `PausePoint` today is declared statically
  on a `WorkflowStepDefinition`, pausing *at a step's boundary*, not from inside a still-running
  worker's own turn loop. A dialogue worker signalling "pause now, mid-execution, for a gate" needs a
  structured way to reach Flow *while running* — the same shape of problem
  [0035](0035-aer-yield-is-a-structured-mcp-tool-not-a-sentinel.md) already solves for `aer yield`.
  **This rides on the same MCP server `#585` builds**, as a second tool (a gate-request signal)
  alongside `yield`, not a separate mechanism. **Which orchestrator a gate-request escalates to is
  read fresh at the moment the request is processed, never cached from when the Dialogue started** —
  consistent with 0032's own framing that orchestrator authority is bound to whoever currently holds
  the role, not to whoever held it at some earlier point. A reassignment mid-Dialogue therefore needs
  no special handling: the next gate-request simply reaches the current holder.
- **Budget extension is a new execution, not a resumed one.** Extending a completed Dialogue's turn
  budget starts a fresh dialogue-shaped step carrying the prior transcript as context (the same shape
  `Supersede`/`RetryWithRevision` already give a pipeline step), not a paused workflow waiting to be
  unfrozen.

## Consequences

**Easier.** Nothing new to build for the state model itself — 0020's "one state machine" requirement
is satisfied automatically, because there is only ever the one. The UI-core rebuild renders shape as a
*view* over `FlowState`, the same discipline every other surface already follows.

**Harder.** The one real new capability — a running step signalling a mid-execution pause — has to be
designed as part of `#585`'s MCP server scope, not assumed to fall out of `PausePoint` as it exists
today. `#585`'s issue body should be read as covering two tools (`yield` and a gate-request signal),
not one.

**Obliges us to.** Scope `#585` to include the gate-request tool alongside `yield` before calling
either "done"; and render shape purely from `FlowState` in the UI-core rebuild rather than inventing a
parallel `ShapeState` concept anywhere.

Relates: [0003](0003-templates-collapse-to-three-shapes.md) (the three shapes this renders),
[0020](0020-one-state-machine.md) (the invariant this satisfies rather than violates),
[0035](0035-aer-yield-is-a-structured-mcp-tool-not-a-sentinel.md) (the mechanism the one new
primitive here reuses).
