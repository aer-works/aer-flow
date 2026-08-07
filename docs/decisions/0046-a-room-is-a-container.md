# 0046 — A room is a container; work nests, not places

Status: accepted
Date: 2026-08-01

Amends [0001](0001-two-nouns-workflow-and-session.md). It **remodels the room/workflow boundary** — more
than [0013](0013-room-is-the-user-facing-noun.md), which only renamed. 0001's room-with-participants,
floor-passing, and the one-spawn-primitive idea are kept; what moves is where the line between "room"
and "workflow" sits, and what forms the tree.

## Context

0001 drew the line as: **workflow** = the authored shape you edit and reuse; **room** = "a running
instance of one." Under that, a running workflow *is* a room, and spawned work forms a tree of **child
rooms**. Two forces made that line show strain, and the owner named the second one directly.

- **Memory is room-owned and outlives runs** ([0016](0016-memory-is-room-owned.md),
  [0044](0044-memory-belongs-to-the-room-and-changes-only-by-decision.md)): a room's `memory/` spans
  many pieces of work. That reads cleanly only if the room is the durable *container*, not one run.
- **The resident orchestrator** (#778) lives in a room and kicks off work repeatedly. Under 0001 each
  kick-off is a *child room*, so a working session spawns a tree of rooms. The owner: *"the room is the
  outer container, and it runs a workflow, which doesn't change its name when it runs — it's just a
  workflow that is running."*

What recurses under an orchestrator is **work** — the Claude Code Task / sub-agent pattern. Nesting work
is intuitive; nesting *places* is not, and calling a headless sub-dispatch "a room" over-claims. The
running code already leans this way: the M26 room model (`RoomProjector` → `RoomState`) is a **container
of references to separate lane runs**, each its own room directory — not one running workflow. 0046
formalizes the direction the code already took.

## Decision

**A room is a place. A workflow is work. The place contains the work; the work keeps its name while it
runs.**

- **Room** — the container you open and navigate to: one directory, its participants (you + workers,
  including the resident orchestrator), its memory, its history, its entry in the list. Exactly
  [0013](0013-room-is-the-user-facing-noun.md)'s room — that record already described a container, not a
  run.
- **Workflow** — a unit of work run **under** a room. It is a workflow whether queued, running, or done;
  running does not rename it. Its shape is one of the three in
  [0003](0003-templates-collapse-to-three-shapes.md) (Conversation, Pipeline, Dialogue).
- **The tree is work, not places.** Delegation and fan-out form a tree of **workflow runs** under the
  one room — each in its own worktree when it runs in parallel, because the directory turn-lock
  ([0013](0013-room-is-the-user-facing-noun.md)) admits one workflow per directory (rung 4 / #669).
- **Rooms are flat; a place appears only deliberately.** The room list is the set of places you opened.
  A *new room* appears two ways, both deliberate: you open one, **or** a workflow run is **promoted** to
  a child room — when it needs its own interaction, workspace, or focused memory (e.g. the orchestrator
  kicks off a long "security audit" you want to step into and drive). Promotion is by a person, or the
  orchestrator proposes it and a person accepts. Never an automatic side effect of a dispatch. This is
  the one nesting a strict "rooms never nest" would wrongly foreclose (raised by the cross-vendor review
  of this draft); it mirrors 0009's *ephemeral-by-default, an explicit keep promotes to durable*.

0001's "spawn is one primitive regardless of who asks" is kept: the primitive spawns a **child workflow
run**, and "who asked" stays a field on it; the child is a workflow, promotable to a room.

## Rests on

| fact | how we know | if false |
|---|---|---|
| The code already models a room as a container of references to *separate* lane runs, not one running workflow | **measured** — `src/Aer.Flow/Projection/RoomProjector.cs` → `RoomState` is a `Dictionary<HeldWorkRef, HeldWorkState>` over separate lane task dirs (research 2026-08-01) | 0046 would be proposing a container model against the grain of the code rather than formalizing it |
| Reopening a task replays the entire event log — there is no state checkpoint | **measured** — `snapshot.json` is the frozen template only (`SnapshotBinder.cs:7-14`); `FlowEventLogReader` reads from byte 0; `StateProjector` projects the whole store each pump round | the container model's open-cost worry is smaller and #903 less urgent |
| 0009's retention (compaction, subtree collapse, lineage, spawn ceiling) is essentially unbuilt | **measured** — append-only `FlowEventLogWriter`/`RoomEventLogWriter`, no compaction; `SessionMetadata` has no parent/depth/kept fields; `ArtifactManager` has no prune; "archive" is a `.aer/archived` marker only (research 2026-08-01) | the container model *inherits* a retention safety net and #903 is smaller |
| No projection aggregates several concurrent runs into one room state | **measured** — `StateProjector.DeriveWorkflowStatus` is single-DAG; `RoomState` keeps each held-work ref separate, no rollup field | the 0020 one-state obligation below is already satisfied |
| A room's turns serialize on a directory-keyed lock | **measured** — `SessionTurnLockKey(directoryPath)` (0013) | parallel runs would not need separate worktrees, simplifying the fan-out story |

## Consequences

**Easier.** The recursion that reads as strange disappears: work nests (familiar — Task subagents),
places stay flat. "Room owns memory across runs" becomes the plain reading. The room/workflow line
matches the engine's own recipe/execution seam and the M26 `RoomState`.

**Harder.** This is the first record to *remodel* rather than rename the room noun. And because 0009's
retention is unbuilt, the container model does not inherit a safety net — **it creates the requirement
for one:** a room kept open indefinitely (the owner's main use case) replays an ever-growing log on open
and accumulates one artifact dir per run forever. That work is scoped and pinned as **#903**, a
prerequisite of #778, not of rungs 1–4.

**Obliges us to** rewrite 0001's "room = a running instance / rooms form a tree / child rooms" passages
to "room = container / *workflows* form the execution tree / promotable child runs"; carry 0009's node
noun (session/child → **workflow run**) into [0009](0009-session-lifecycle-and-retention.md),
[0010](0010-skills-and-advisor.md), and [0018](0018-attention-is-the-primary-signal.md), which reference
that tree; **define the one-room-state rollup** a container room projects over its child runs
([0020](0020-one-state-machine.md) requires exactly one state per room — surfacing the most
attention-worthy child state, which is what [0018](0018-attention-is-the-primary-signal.md)'s ordering
wants); and build **#903** before a resident room is viable at scale.

**Does not change** the room's participants, floor-passing, the depth/count ceiling, or the memory model
— only which object is the container and which is the tree.

**Relates to** [0001](0001-two-nouns-workflow-and-session.md) (amended), [0013](0013-room-is-the-user-facing-noun.md),
[0003](0003-templates-collapse-to-three-shapes.md), [0009](0009-session-lifecycle-and-retention.md) (its
retention is #903), [0020](0020-one-state-machine.md) (the rollup obligation), and
[0044](0044-memory-belongs-to-the-room-and-changes-only-by-decision.md).
