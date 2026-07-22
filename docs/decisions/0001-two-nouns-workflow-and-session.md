# 0001 — Two nouns: workflow and session

Status: accepted
Date: 2026-07-22

## Context

One object is called four things, and two of them are navigation destinations sitting next to each
other in the same rail. Observed in a single sitting on the running product:

```
desktop rail    Task (singular) and Tasks (plural) — two separate destinations
desktop Chat    "No session open."  /  "Start new chat"
mobile home     "No task is open on the host yet."  /  "Browse recent tasks"
on disk         ~/.aer/sessions/   and   ~/.aer/tasks/
engine + spec   workflow
```

A rail carrying both *Task* and *Tasks* as distinct destinations is the clearest single symptom that
the vocabulary was never one thing. Underneath, a session already **is** a task: same single-step
supersede chain, same event store, same projection loader. It just gets a second metadata file and a
separate root, which is why `isSession` special-casing is scattered through daemon and UI alike.

## Decision

**Workflow** — the authored shape you edit and reuse.
**Session** — a running instance of one.

**"Task" is deleted from the product's vocabulary entirely.** It was the redundant third name that
made "session" and "task" read as interchangeable.

A chat is a session whose workflow is the conversation shape. A review run is a session whose
workflow is an authored pipeline. See [0003](0003-templates-collapse-to-three-shapes.md).

## Consequences

**Easier.** Every list, empty state and status line has one word available for the thing it is
showing. The `Task` / `Tasks` rail split stops being defensible, which is a feature — it forces the
IA question rather than letting it hide behind vocabulary.

**Harder.** Two storage roots (`~/.aer/tasks`, `~/.aer/sessions`) currently encode the distinction
being deleted, and a third exists for authored workflows under `OneDrive\Documents\AER Flow`.
Unifying them is a migration, not a rename.

**Obliges us to** rename in code as well as UI — see [0002](0002-one-vocabulary.md) — and to update
the behavioural spec's normative terms, which is one-time but not free.
