# AER Flow UI Behavioral Specification — v1.0

This document defines the behavioral contract of AER Flow UI.

AER Flow UI is a control-plane and visualization layer for AER Flow. It depends on:

* AER Core v1.1
* AER Flow v1.0

This specification intentionally defines **no execution semantics**. Those belong exclusively to AER Flow and AER Core.

Each spec (Core, Flow, UI) is versioned independently; the dependency list above pins the sibling spec versions this document was written against, and cross-references always name the sibling spec file as it exists at the same commit.

**Changes from v0.9:** promotion to v1.0, with no behavioral changes — the answer M16's completion owed the implementation plan's spec-gap ledger. Projection, control surface, and authoring (implementation milestones M14–M16) now cover every capability this spec names for those surfaces, and no known gap blocks a current capability — the same terms on which the Flow spec reached v1.0. §10's conversation-view text remains a "may": the first Case 2 encapsulated multi-model worker (AER Flow spec §18.2) and the conversation view that projects it are planned work (implementation milestones M17/M18), and gaps they surface are resolved by amendment, exactly as the Flow spec has been amended post-1.0.

**Changes from v0.8:** added worker-bindings configuration files to §4's write model, closing a gap found while planning the third (authoring) UI milestone: §9 already granted "edit worker bindings" / "swap worker implementations", but §4's closed write list never named the file those grants imply the UI writes to.

**Changes from v0.7:** added §3.1 (Task Directory Discovery), closing a gap found while planning the first UI milestone: §3 defined *what* the UI reads but not how it *finds* task directories. A task directory is self-describing — identified by its durable contents, never by membership in a registry; any UI-side list of task directories is Local UI Configuration (§4), rebuildable and never authoritative; and the trusted execution stack is never required to announce, register, or enumerate tasks.

---

# 1. System Role

AER Flow UI is a deterministic projection and authoring layer.

It has exactly two responsibilities:

1. **Projection**

   * Visualize workflow state.
   * Visualize execution history.
   * Visualize artifacts.
   * Visualize worker relationships.
   * Visualize execution progress.

2. **Authoring**

   * Create workflow definitions.
   * Edit workflow definitions.
   * Configure worker bindings.
   * Configure retry policies.
   * Configure workflow metadata.
   * Configure `PausePoint`s on steps, including their `SupersedeTargets` lists (AER Flow spec §11.1, §17.1).

The UI does **not** execute workflows.

The UI does **not** schedule workflows.

The UI does **not** implement Flow logic.

---

# 2. Architectural Position

The trusted execution stack is:

```
WorkflowDefinitionSnapshot   (AER Flow spec §11.2)
        ↓
AER Flow
        ↓
ExecutionRequest
        ↓
AER Core
        ↓
Operating System
```

The UI exists outside this execution stack.

```
                Event Store
                    ▲
                    │
        WorkflowDefinitionSnapshot
                    ▲
                    │
                AER Flow UI
```

The UI observes the system and edits workflow **templates** (AER Flow spec §11.1). It does not create, hold, or interpret `WorkflowDefinitionSnapshot`s itself — that binding is performed exclusively by Flow at task creation, per AER Flow spec §11.2. The UI's role with respect to a snapshot is read-only, exactly like its role with respect to the rest of the Event Store.

---

# 3. Read Model

The UI reconstructs all visible state exclusively from:

* Workflow templates and the `WorkflowDefinitionSnapshot`s bound to existing tasks (per AER Flow spec §11)
* Flow Event Store
* Core Event Store
* Artifact directories

No live runtime state is authoritative.

If something cannot be reconstructed from durable state, it is not part of the behavioral model.

## 3.1 Task Directory Discovery

A task directory is **self-describing**: it is identified by its contents — a bound `WorkflowDefinitionSnapshot` and its Event Store (AER Flow spec §5, §11.2) — never by membership in any registry, index, or list. Whether a task exists is a fact about durable state on disk, and about nothing else.

Consequences:

* The UI may maintain a list of known task directories — recently opened locations, configured roots it scans, an index it builds. Any such list is Local UI Configuration (§4): a rebuildable convenience that may be lost, deleted, or rebuilt at any time with no loss of information. It is never authoritative; the task directory itself is.
* The UI must be able to open any valid task directory it is pointed at, whether or not any list has ever mentioned it.
* A listed directory that no longer exists, or no longer contains a valid snapshot and Event Store, is stale list state — to be reflected or pruned, never surfaced as a system error.
* No component of the trusted execution stack (§2) is required — or may be required by a UI — to announce, register, or enumerate task directories. Flow has no registry of tasks and no concept of one (AER Flow spec §2, §20); discovery is entirely a client-side concern.

How a UI populates its list — asking the user, remembering what it opened before, scanning a configured root for directories matching the reference implementation's on-disk layout — is an implementation choice, not behavioral contract, with one constraint: a scan must treat "looks like a task directory" as a hypothesis, confirmed only by the directory's actual contents.

---

# 4. Write Model

The UI may write only to:

* Workflow template files (never a bound `WorkflowDefinitionSnapshot` — those are immutable per AER Flow spec §11.2)
* Worker-bindings configuration files (§9) — never a task directory; bindings are a UI/CLI input, not durable task state (§3)
* User preferences
* Local UI configuration

The UI never writes directly to:

* Flow Event Store
* Core Event Store
* ExecutionRequest records
* Artifact files

All runtime state mutations occur exclusively through Flow's mutation interface (AER Flow spec §14). The UI is one possible caller of that interface; it holds no special access beyond it.

---

# 5. Workflow Template Editing

Workflow templates are editable. `WorkflowDefinitionSnapshot` creation and binding is defined entirely in AER Flow spec §11.2 and is not redefined here. The only things this document adds are UI-specific consequences of that rule:

- The UI may edit a template at any time, including while tasks created from earlier versions of that template are still running or being replayed.
- Such edits are visible only to *future* task instantiations. The UI must never present an edit to a template as having any effect on an already-bound task — visually or otherwise — since AER Flow spec §11.2 guarantees it has none.
- When visualizing a historical or in-progress task, the UI renders the `WorkflowDefinitionSnapshot` that task is bound to, not the current state of the template it originated from. If the two have diverged, the UI should make that diff visible rather than silently rendering the live template.
- When a user submits a revised artifact through the UI (for example, hand-editing a plan during a pause, §7 below), the UI does not write that file directly. It invokes the mutation interface, which records the submission as an ordinary `ExecutionRequest` with a non-process `Worker` (e.g. `"human"`), exactly as AER Flow spec §17.3 defines. From the UI's perspective this looks like any other artifact-producing action; it does not require, and must not attempt, any special-cased "edit this file in place" capability, since artifacts are immutable by construction (AER Flow spec §16).

---

# 6. No Execution Authority Rule

The UI must never:

* emit ExecutionRequests
* schedule work
* classify execution outcomes
* mutate Event Store contents
* bypass Flow
* bypass Flow's mutation interface
* override scheduler decisions
* influence runtime execution directly
* interpret or act on a `DecisionType` (§7) itself — only Flow does

Doing so is a protocol violation.

---

# 7. User Actions

The UI may expose user actions corresponding to AER Flow's primitives:

* **Resume** a paused step (AER Flow spec §17.2, `DecisionType: Resume`)
* **Reject** a paused step (`DecisionType: Reject`)
* **Retry as-is, or with a revision** — re-run the step that just paused, optionally with a supplied artifact (`DecisionType: RetryWithRevision`, AER Flow spec §17.3)
* **Send back to an earlier step** — supersede an earlier step's already-successful output with a new attempt informed by this step's feedback (`DecisionType: Supersede`, AER Flow spec §17.1, §17.2)
* **Cancel** a running execution (AER Flow spec §9)
* Start a workflow
* Resume a workflow after a pause

These actions are implemented exclusively by invoking Flow's mutation interface (AER Flow spec §14). The UI's job is to map a small set of clear, human-facing buttons onto Flow's small, closed `DecisionType` set (§17.2) — the UI must not invent decision types beyond what Flow defines, even if a richer label would read more naturally (e.g. "Approve" is presented to the user, but is recorded as `Resume` underneath; "Send back to the Architect" is presented to the user, but is recorded as `DecisionType: Supersede, TargetStepId: architect` underneath).

**The "send back to" action is constrained by `SupersedeTargets` (AER Flow spec §17.1), and the UI must reflect that constraint rather than work around it.** When presenting a paused step's options, the UI may only offer "send back to X" for `StepId`s present in that pause point's declared `SupersedeTargets` list — never an arbitrary earlier step the user might wish existed as an option. If `SupersedeTargets` is empty for a given pause point, the UI must not present a "send back" option at all for it, rather than presenting one and having it fail at the mutation interface. This is the same discipline as the no-execution-authority rule (§6): the UI reflects what the template actually declared, it does not invent capability the template didn't grant.

`RetryWithRevision`'s feedback artifact is optional; `Supersede`'s is mandatory — the UI must require the user to supply (or select) a supplementary artifact before allowing a "send back to X" action to be submitted, since AER Flow spec §17.2 defines a `Supersede` with no supplementary artifact as itself an invalid decision.

Example, using today's reference implementation, for a pause-and-resume action:

```
User clicks "Approve"

        ↓

UI invokes the mutation interface
(today: launches `aer decide task-123 --type resume`)

        ↓

reference implementation acquires lock,
appends ExternalDecisionRecorded{DecisionType: Resume}, exits

        ↓

UI observes WorkflowResumed in the updated Event Store
```

Example, for Cancel:

```
User clicks "Cancel" on a running step

        ↓

UI invokes the mutation interface
(today: launches `aer cancel task-123 --execution <ExecutionId>`)

        ↓

reference implementation records CancellationRequested,
forwards the request toward Core (AER Core spec §7)

        ↓

UI observes ExecutionCancelled in the updated Event Store
once Core's kill sequence completes
```

The UI does not perform these mutations itself, and does not assume cancellation is instantaneous — the UI should reflect "cancellation requested" as soon as it observes `CancellationRequested`, and only reflect the execution as actually stopped once it separately observes `ExecutionCancelled`. These are two different events with a real-world gap between them, however small, since Core's kill sequence (AER Core spec §5) takes a moment to run.

To the behavioral model, the UI invoking the mutation interface is identical to any other caller invoking it — including a user typing a command into a terminal, if the reference implementation happens to be a CLI. This is a direct consequence of AER Flow spec §14 and §20: Flow has no concept of a privileged client.

---

# 8. DAG Editing

The UI may:

* edit workflow templates' graphs
* edit dependencies
* validate graph structure
* detect dependency cycles
* visualize execution order
* preview workflow topology
* toggle `PausePoint` on a given step, and edit its `SupersedeTargets` list, constrained to that step's actual transitive ancestors (AER Flow spec §11.1, §17.1)

The UI may simulate scheduling for visualization purposes.

Simulation results are advisory only.

Simulation must never influence runtime execution.

---

# 9. Worker Representation

Workers are presented as logical capabilities rather than concrete binaries.

The UI may:

* assign WorkerContracts (AER Flow spec §4)
* swap worker implementations
* edit worker bindings
* display implementation metadata
* display estimated cost or latency

Worker selection is persisted only as workflow template data. A change made through the UI takes effect only for tasks instantiated after the change, per §5 above. Changing which step runs next is never something the UI lets the user invent on the spot — but a paused step's already-declared `SupersedeTargets` list (AER Flow spec §17.1) may legitimately let a user send work back to an earlier step via `Supersede` (§7 above). The distinction is bounded vs. unbounded, not "never": the UI may only ever offer targets the template itself already declared reachable from this exact pause point, never an arbitrary step the template didn't name (AER Flow spec §17.2).

---

# 10. Multi-Model Visualization

The UI may present workflows using higher-level visualizations, including:

* DAG view
* Conversation view
* Timeline view
* Artifact lineage view

Conversation-style visualizations are purely projections.

Each visible conversation step must correspond to durable workflow artifacts or durable execution history.

The UI must never invent hidden runtime state that cannot be reconstructed from durable data. For a Case 2 encapsulated multi-model worker (AER Flow spec §18.2) that streams its internal turns via the Observation Tier, the UI may render those turns live as they arrive — but may not offer an action that pauses or redirects the worker mid-stream, since that capability does not exist (AER Flow spec §17.4).

---

# 11. Deterministic Projection

Given identical:

* The relevant `WorkflowDefinitionSnapshot`(s) (or templates, for not-yet-instantiated workflows)
* Flow Event Store
* Core Event Store
* Artifact directories

the UI must produce identical visual state.

Rendering must not depend on:

* wall-clock time
* process state
* memory caches
* hidden runtime information

This is the UI's own application of AER Flow spec §13's determinism guarantee, extended to projection/rendering rather than `ExecutionRequest` generation. It does not relax or restate that guarantee — it is a separate, additional one specific to this layer.

---

# 12. Transparency Rule

Every visual element presented by the UI must be explainable through durable system state.

For every displayed execution, artifact, dependency, workflow transition, pause, or decision, the user must be able to trace its origin through:

```
WorkflowDefinitionSnapshot (or template)
+
Event Store
+
Artifacts
=
Displayed State
```

If a displayed state cannot be reconstructed this way, it does not belong in the behavioral model.

---

# 13. Replaceability

The UI is not part of the trusted computing base.

Any application that:

* edits workflow templates,
* invokes Flow's mutation interface for user actions, and
* reconstructs state from the Event Store and bound `WorkflowDefinitionSnapshot`s,

is behaviorally equivalent.

Examples include:

* desktop applications
* web applications
* terminal user interfaces
* IDE integrations
* editor extensions

No UI implementation receives additional execution authority.

---

# 14. Relationship to Flow

Flow remains solely responsible for:

* workflow scheduling
* dependency evaluation
* retry logic
* execution classification
* ExecutionRequest generation
* workflow progression
* `WorkflowDefinitionSnapshot` creation and binding (AER Flow spec §11.2)
* interpreting `DecisionType` and `FailureClassification` (AER Flow spec §17.2, §8.1)

The UI remains solely responsible for:

* visualization
* authoring of workflow **templates** (never snapshots)
* user interaction, including translating human-facing labels (e.g. "Approve") onto Flow's closed `DecisionType` vocabulary

Flow never depends on the UI, and — per AER Flow spec §2, §14, and §20 — has no model of "a UI" at all, only of a single, unspecified mutation interface.

The UI never participates in Flow's execution semantics.

---

# 15. System Principle

> The UI is a control plane for workflow templates and a projection over durable history, including the immutable snapshots Flow has bound to tasks and the decisions recorded against them.

> The UI is never a control plane for workflow execution.