# AER — Project Overview & Goals

This document is not a behavioral specification. It defines no guarantees and has no invariants to test against. Its purpose is to state, in one place, what AER is *for* — so that as the individual specs (Core, Flow, UI, and whatever follows) evolve independently, none of them lose sight of the actual problem this exists to solve.

If a future design decision in any component spec seems technically elegant but doesn't serve the goal stated here, that's a signal to question the decision, not to update this document to match it.

---

## 1. The Original Problem

Local development increasingly involves more than one AI coding agent — today, Claude Code and Gemini/Antigravity; tomorrow, others. A common and effective pattern is to have one model produce a plan, a second model critique it, and a third (or the first again) implement and review the result. Right now, that pattern is manual: copy a plan from one tool, paste it into another, copy the critique back, decide what to do next, by hand, every time.

No existing tool does this well on Windows, for a developer on consumer subscriptions (Claude Pro, Google AI Pro) rather than metered API access, across more than one AI vendor's tooling. Tools like Cyboflow exist for similar problems but assume a Unix shell-glue environment and a single-vendor agent loop. The goal was never to port Cyboflow — it was to solve the actual problem that prompted looking at it in the first place.

## 2. What AER Actually Is

**AER lets you define roles — Architect, Critic, Implementer, Reviewer, Tester, Human-approver — and bind each role to whichever AI tool or human currently does it best, then runs that pipeline reliably, resumably, and without babysitting the handoffs by hand.**

Concretely, that means:

- **Set up a pipeline once** (a `WorkflowDefinition`: which roles exist, what depends on what, what each role's contract is), instead of re-deciding the sequence of copy-paste steps every time.
- **Assign models to roles, not the other way around.** "Architect" is bound to Claude today; if a better model for that role exists next year, the workflow doesn't change — only the binding does.
- **Let models talk to each other indirectly, through artifacts and contracts, not through a shared chat window.** Claude writes `plan.md`; Gemini reads exactly that file, writes `review.md`; the next step reads both. This is *how* the models "talk" — not a live conversation, but a deterministic, replayable handoff, which is more reliable than either model trying to interpret the other's raw conversational output.
- **Let a human watch that handoff happen and step in before the next stage runs.** Any point in a pipeline can pause for review — approve, reject, or send a step back with a revision — before work continues (AER Flow spec §17). This was one of the two concrete things this project was started to do, not a feature bolted on later: the goal was never just automation for its own sake, it was automation a person can still see into and steer.
- **Survive crashes and interruptions without losing the thread.** If the process dies mid-pipeline — a model hangs, a laptop sleeps, a tool crashes — restarting picks up exactly where things left off, because the system never trusted memory or a live process for state; it trusted a durable, replayable log.
- **Work the same way regardless of which AI vendor is involved**, because nothing in the core execution or scheduling logic knows what an LLM is. A worker is a worker, whether it's Claude, Gemini, `git`, or `cargo test`.

## 3. Non-Goals

Stated explicitly so they don't get reintroduced by accretion later:

- **Flow itself never interprets conversation content to make a routing decision.** Flow's scheduling logic reads structured outcomes (exit codes, declared outputs, contract satisfaction) — it never parses what a worker said and branches on the meaning. This is why Core and Flow stay deterministic and boring on purpose (§5, principle 6).

  This does **not** mean turn-by-turn iteration between models is invisible, or that a human can't weigh in. A worker performing iterative refinement (AER Flow spec §18.2) may expose each internal turn as it happens — streamed via the Observation Tier, or written as numbered artifacts — for a human (or a UI) to watch live. And reviewing a model's output, then approving, rejecting, or sending it back with a revision before the next step runs, is a first-class, specified capability (AER Flow spec §17, "Pause & External Decision") — not an afterthought. What remains genuinely out of scope is steering a worker *while it is actively running*, mid-generation, without stopping it first — that requires interactive process control (Core Tier 3: stdin, PTY) that this system does not provide. A running worker can always be stopped outright via Cancellation (AER Flow spec §9), and a workflow can always pause *between* steps for review — what it cannot do is let a human reach into a step while it's mid-execution and redirect it without restarting it.
- **Not a workflow programming language.** No loops, no conditional branching syntax, no general-purpose scripting inside a workflow definition. If a task needs that kind of logic, it belongs inside a worker, not in the pipeline shape.
- **Not an enterprise CI/CD platform.** AER is scoped to a single developer's local machine and local tasks. It borrows ideas from CI/CD (event sourcing, deterministic replay) because they're the right ideas for the reliability problem, not because AER is trying to become one.
- **Not tied to any particular AI vendor**, by design — see §2. A feature that only makes sense because a process happens to be an LLM almost never belongs in the core execution layer (Core) and usually belongs in a worker adapter instead.

## 4. System Layers

Each layer has its own authoritative behavioral spec. This section gives the one-paragraph version of each; the linked documents are the actual contracts.

| Layer                         | Spec                                  | What it owns                                                                                                                                                                                                                                                                                                                                              |
| ----------------------------- | ------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Core**                      | `aer-core-behavioral-spec-v1.1.md`    | Deterministic process execution. Spawn, timeout, kill the whole process tree, report exit, and — on request — cancel a still-running execution on demand. Knows nothing about workflows, retries, or what an "AI" is — to Core, Claude and `cargo test` are indistinguishable.                                                                            |
| **Event Store**               | Defined within the Flow spec (§5)     | The durable, append-only, replayable history that is the actual source of truth for the whole system. Not a layer with its own spec file, but a concept load-bearing enough to call out here.                                                                                                                                                             |
| **Flow**                      | `aer-flow-behavioral-spec-v1.0.md`    | Workflow semantics: what a pipeline is, which steps are ready to run, how retries, cancellation, pause-for-decision, and bounded backward revision (one model's feedback driving another's rerun) work — and the guarantee that replaying the same history always produces the same decisions. Emits `ExecutionRequest`s; never executes anything itself. |
| **UI** (and any other client) | `aer-flow-ui-behavioral-spec-v0.7.md` | Visualizing pipeline state and authoring workflow templates. Has no execution authority of its own — every mutation it makes goes through the same interface any other client would use. Fully replaceable; a terminal, a desktop app, and an IDE plugin are all equally valid implementations.                                                           |

The dependency direction is strictly one-way: UI depends on Flow's concepts; Flow depends on Core's. Neither lower layer is ever aware that the layer above it exists.

## 5. Design Principles (carried across every layer)

These recur throughout the individual specs; collecting them here is meant to make them easy to hold onto when writing the *next* spec, rather than rediscovering them by trial and error again.

1. **The litmus test.** If a feature wouldn't make sense for `git`, `cargo test`, or `ffmpeg`, it doesn't belong in Core. If it requires knowing what a workflow is, it belongs in Flow, not Core.
2. **Guarantees, not mechanisms.** Specs state what must be true ("at most one writer at a time"), not how that's achieved today ("a kernel file lock"). Mechanisms are free to change; guarantees are the actual contract.
3. **One source of truth.** The Event Store is authoritative. Anything else — a manifest, a cache, a UI's in-memory state — must be fully reconstructable from it and safe to delete and rebuild at any time.
4. **Determinism over cleverness.** Identical history plus identical configuration must always produce identical decisions. This is what makes crash recovery, audits, and debugging tractable instead of mysterious.
5. **Don't abstract before you have two.** Interfaces (a worker abstraction, a mutation API) get extracted from real, concrete implementations once at least two exist — not designed up front for hypothetical future consumers.
6. **Workers carry the intelligence; the system carries the discipline.** Anything model-specific — prompting, multi-turn coordination between models, vendor-specific quirks — lives inside a worker's implementation. Core and Flow stay boring on purpose, because boring is what makes them trustworthy.

## 6. Scope vs. Extensibility — Holding Both Honestly

This project is being built, realistically, for one or two recurring use cases for one person. That's worth saying plainly rather than dressing it up as something bigger than it is.

At the same time, the Core / Flow / UI split exists *specifically because* it costs little now and preserves the option later — not because this is secretly meant to be a community platform. Principle 5 (§5) already governs the right boundary: separate things into layers when the separation is cheap and the logic genuinely differs (Core's "I don't know what a workflow is" vs. Flow's "I don't know what an LLM is" are different concerns, regardless of how many people ever use this) — but don't build speculative generality for hypothetical future users (a plugin marketplace, a multi-tenant mutation API, a Python binding with no consumer) before a real second use case demands it.

Concretely, this means: the layering itself is not over-engineering. A second worker adapter, a different scheduling rule, or someone else's UI could plug into the existing seams (Core's FFI boundary, Flow's mutation interface, the WorkerContract abstraction) without anyone needing to have anticipated their specific use case — because those seams were chosen for what AER itself needed, not for an imagined audience. What *would* be over-engineering is adding capability — a plugin registry, multi-user auth, a marketplace of contracts — before anyone, including Philip, has a concrete second use case for it. If that day comes, the right move is the same one this whole thread has used repeatedly: build the second concrete thing, then extract what it actually has in common with the first.

## 7. Repository Layout

- **`aer-core`** — Rust; its own repo because it's the one boundary that's genuinely cross-language and earns the isolation. Publishes a C ABI and a .NET P/Invoke binding.
- **`aer-flow`** — A single .NET solution containing the Flow engine, worker adapters, and the CLI as separate projects. Lives here because Core/Flow/UI are one team's work and there's no benefit in splitting them before a genuine second consumer exists.
- **`aer-works/.github`** — Org profile. No code lives here.

For current milestone status and implementation sequencing, see `IMPLEMENTATION_PLAN.md` in each repo.