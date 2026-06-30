# AER Flow — Implementation Plan

The behavioral spec (`spec/aer-flow-behavioral-spec-v1.0.md`) is authoritative for what the system must guarantee. This document is authoritative for how we are getting there: which subsystems exist, how they group into milestones, and what is in scope for the current milestone.

---

## Capability Map

Subsystems derived from the behavioral spec, independent of milestone ordering.

### Event Store Layer
- **Log Manager** — Append-only `flow.jsonl`; atomic line writes and fsync sequencing (§5, §7).

### Projection Engine
- **State Projector** — Reads `flow.jsonl` to deterministically reconstruct `FlowState` from scratch on demand (§12).
- **Staleness Computer** — Derives dependency staleness from `UpstreamExecutionIds` and `Supersede` history (§11.3).

### Scheduling Layer
- **Template Parser** — Reads and validates the static DAG `WorkflowDefinition` (§11.1).
- **Snapshot Binder** — Freezes the template into an immutable `WorkflowDefinitionSnapshot` when a flow is created (§11.2).
- **Graph Resolver** — Implements `NextExecutionRequest = f(FlowState)`: finds ready steps given the current projection and dependency graph.

### Execution & Dispatch
- **Core Dispatcher** — Maps Flow `ExecutionRequest`s to `aer-core` via the M5 .NET P/Invoke binding.
- **Artifact Manager** — Pre-allocates `artifacts/execution_N/` directories; assigns input/output artifact paths before dispatch (§16).
- **Contract Validator** — Asserts `ProducedOutputs` exist on disk before classifying an execution as `ExecutionSucceeded` (§8).

### Control Interface
- **Mutation API** — The single interface for admitting external decisions, recording pause acknowledgements, and triggering cancellation (§14). All clients (CLI, UI) go through this.

---

## Milestone Roadmap

### M7: Foundation — Linear Execution
*Goal: Successfully execute a hardcoded, linear sequence of steps end-to-end.*

Capabilities introduced:
- Log Manager (basic append-only writes, no crash-recovery hardening yet)
- Template Parser (linear A → B graphs; no fan-out)
- Core Dispatcher (calling aer-core M5 bindings)
- Contract Validator (success vs. failure classification)
- Artifact Manager (output directory allocation)

Blocked by: `aer-core` M5 (.NET binding must be complete and merged).

### M8: Reactive Scheduler — DAGs & Projection
*Goal: Support complex DAGs and deterministic state reconstruction from the log.*

Capabilities introduced:
- State Projector (full replayability from `flow.jsonl`)
- Graph Resolver (concurrent step execution; fan-out and fan-in)
- Snapshot Binder (immutable templates at flow creation time)
- Retry Engine (handling `ExecutionFailed` with `FailureClassification: Permanent`)

### M9: External Decisions & Supersede Cascades
*Goal: The human-in-the-loop pause and supersede mechanics.*

Capabilities introduced:
- Mutation API (handling external decisions via CLI/UI)
- `PausePoint` support (§17)
- `Supersede` logic and the Automatic Invalidation Cascade (§17.5)
- Staleness Computer

### M10: Cancellation & Edge Cases
*Goal: On-demand cancellation and robust crash recovery.*

Capabilities introduced:
- Hardened sync-write crash recovery (§7)
- Cancellation integration (§9): wiring `CancellationToken` through to `aer-core`'s cancel handle

---

## Current Milestone

**None yet.** M7 is blocked on `aer-core` M5.

## Completed Milestones

None.

---

## Open Questions

- **Worker adapter interface**: The spec requires `aer-core` for execution but says nothing about how a worker (Claude, Gemini, `cargo test`) is configured. What does the worker registration interface look like? Resolved when M7 implementation begins and we have one concrete worker to extract from.
- **CLI shape**: What commands exist? (`aer-flow run`, `aer-flow status`, `aer-flow approve`?) Deferred until the Mutation API design (M9) is concrete enough to drive the CLI surface.
- **Flow storage location**: Where does `flow.jsonl` live — next to the workflow definition file, in a `.aer/` directory, configurable? Resolved in M7.
