# AER Room Specification — v1.0

**Status: current. This is the system-level specification of AER as built.** It describes the tree
at HEAD and supersedes `aer-flow-behavioral-spec-v1.0.md` as the top-level statement of what the
system is. The behavioral spec is not retired: its §§3–18 remain the authoritative contract for
scheduling semantics, and this document cites them rather than restating them (see §6). Where the
two documents disagree, this one is right and the disagreement is a defect here.

**This document describes; it does not design.** Everything in it is true of the code at the commit
that last touched it, verifiable by the cited paths. Anything that would require a new decision is
not answered here — it is filed as an issue and listed in §9. The one deliberate exception is §8,
which records direction the decision register has already settled but the tree does not yet
implement; this spec is *fulfilled* by that work arriving, and must not need amendment for it.

---

## 1. What AER is

AER runs vendor CLI agents — Claude Code, Gemini CLI — as **workers** inside **rooms**, under an
engine that records everything durably and a daemon that lets any paired client watch and steer
from anywhere. It is a drop-in replacement for a single vendor's app that a second vendor can work
in, plus the things no single-vendor tool does: orchestration across concurrent rooms, and workers
of different vendors working the same room.

Two principles carry from the existing registers unchanged and govern everything below: routing
never reads conversation content (`CLAUDE.md` Architecture Rule 1 owns the wording), and the
journal is the system of record — every state a room can be in is a projection of recorded events,
so the system cannot be in a state it has not recorded.

## 2. The room

A **room** is one working directory and everything that happens in it: the workers active there,
the chat, the running work, the artifacts, and the durable record. In the code at HEAD the room's
storage form is the *task directory* (`flow.jsonl`, `artifacts/`, bindings) — "task" is the code's
noun for what the product calls a room, and #443 tracks converging the identifiers. One directory
may contain several repositories; the room does not know or care.

A room's lifecycle surface at HEAD is the daemon's task API (`src/Aer.Daemon/Program.cs` is the
authority for the route list): open, run, decide, cancel, archive/unarchive/delete, artifact
retrieval, and a recent-rooms list. Archival is a client-side shelving state, not an engine state —
the engine has no notion of an archived room.

### 2.1 What a room can be running

- **A shape** — a saved workflow template. The shape is the product noun for a
  `WorkflowDefinition`: a static, declarative step graph whose contract — including everything it
  deliberately cannot express — is the engine spec's §11. Templates arrive through the daemon's
  template API or the CLI; the built-in catalog ships in `Aer.Adapters`.
- **A chat** — at HEAD, chat is not a separate machine: it is a two-step definition (`chat` +
  `turn-anchor`) cycling through pause and decision, one worker process per turn, run to natural
  completion. Interactive mid-execution steering does not exist anywhere in the tree; the engine
  spec's §17.4/§20 exclusion of it remains true and this spec keeps that exclusion.
- **A vendor session** — the daemon's session API (`/api/sessions/*`) starts and continues a
  vendor CLI's own resumable session. "Session" means exactly this everywhere in AER: the vendor's
  resumable conversation, never the room itself.

## 3. The turn

The **turn** is the unit of a room's conversation: a worker runs to completion, its output lands as
an event and artifacts, and the room either proceeds (the definition's graph has more to schedule),
waits for a decision (a `PausePoint` fired), or is done. The engine spec's §17 is the full contract
for pausing and deciding; its §17.2 decision vocabulary is closed, deliberately small, and
unchanged at HEAD — the list itself lives there, not here.

Every decision is recorded with the decider's identity on the `ExternalDecisionRecorded` event.
Today every decider is a person on a client; §8 records the settled direction for what else a
decider will be able to be.

## 4. Presence

**Presence** is who and what is in the room and what state each is in: which clients are paired and
connected, which workers are running, what the engine last recorded. At HEAD it is carried by:

- **Pairing** (`PairedClientsStore`): clients pair with the daemon by code, are stored durably,
  and can be listed and revoked. Pairing is authentication of a client to the daemon — it grants no
  vendor credential and touches none (Credential Isolation, `CLAUDE.md` Architecture Rule 4).
- **Broadcast** (`DaemonBroadcast`, `/api/ws` and `/api/ws/progress`): the daemon pushes task
  progress over WebSocket to connected clients. Broadcast is a wire projection of recorded events,
  never an alternative source of truth.

Two honest gaps, filed and open, bound what presence can claim today: the record does not say
whether a running worker's process is still alive (#774), and a worker's live output is invisible
until its step completes (#775). Until those land, a client rendering presence must render the
uncertainty — "running (recorded)" is a claim about the journal, not about the process.

## 5. The resident components

What actually runs, at HEAD:

- **The engine** (`src/Aer.Flow`) — the projection-and-mutation core the engine spec defines. It
  remains a library invoked in-process; it is not a service.
- **The daemon** (`src/Aer.Daemon`) — a specified, shipping component: an ASP.NET host exposing
  the room lifecycle, template, session, pairing, and artifact APIs over REST, with WebSocket
  broadcast, supervising the sidecar (`src/Aer.Sidecar`, zero-config Tailscale) for remote reach.
  The engine spec's §20 wrote "no daemon" and §21 left the pump question open; both were overtaken
  by the code without a recorded decision — this section is that record. Its §17.1 already
  referred to the daemon in passing, contradicting §20 in the same document; §17.1 was right.
- **The pump, answered.** The engine advances only inside a mutation-interface call; something must
  make those calls. At HEAD that something is *whichever host process is driving the room*: the CLI
  (`aer run`) for a headless run, the daemon for anything a client initiates. Exactly one may
  mutate a given room at a time — the engine spec's §15 kernel-held lock is the arbiter, and it is
  what makes "two pumps exist" safe rather than a race.
- **Named clients** (`src/Aer.Ui` desktop, `src/Aer.Mobile` phone) — the engine spec's §14/§20
  "no named client architecture" posture is retired: AER ships and privileges specific first-party
  clients, and the daemon carries infrastructure (pairing, broadcast) that exists for them. What
  survives of §14 is its real invariant, unchanged: **all mutation flows through the one mutation
  interface**, and no client gets a private side door.
- **The CLI** (`src/Aer.Cli`) — run, decide, cancel, supply, status; the headless driver and the
  scripting surface.
- **The dialogue worker** (`src/Aer.Workers.Dialogue`) — the multi-model worker executable; a
  worker, not an engine component.

## 6. Scheduling — by reference

The engine spec (`aer-flow-behavioral-spec-v1.0.md`) §§3–18 are this document's scheduling chapter:
ExecutionRequest, worker contracts and schema-checked outputs, the event store and causal linking,
crash durability, the failure model, cancellation, retry, WorkflowDefinition, projection,
determinism, the mutation interface, concurrency, artifacts, pause/decision, and multi-model
composition. None of it is restated here, deliberately: one register, referenced everywhere.

Its §20 exclusions remain true of the tree except the two this document explicitly retires (the
daemon, named clients). Every other §20 exclusion stays owned and worded by §20 — re-affirmed
here by reference alone, because a renamed copy of that list is exactly the drift this document
exists to end.

## 7. The journal at HEAD

**The journal's user-facing noun is "the ledger"** (lowercase;
[0045](../docs/decisions/0045-the-product-is-baton-the-journal-is-the-ledger.md)). `flow.jsonl`
names the storage file, not the concept — "journal" stays the engine-internal term used below and
in code, while anything a person reads says "the ledger."

One clarification the engine spec's §5.1 left open to interpretation: it names two logs
(`events.jsonl`, `flow.jsonl`) while permitting physical consolidation so long as each event type
keeps exactly one writer. **The tree implements the consolidation**: a single physical `flow.jsonl`
whose entries are owner-tagged (`"owner": "flow" | "core"`, `LogEntry.cs`), ownership enforced per
event type. This is the permitted form, not a contradiction; this paragraph is the record of that
reading so it stops being re-litigated.

Reading at HEAD is full replay of the room's journal at each read (`FlowEventLogReader`,
projection); `aer status --follow` re-reads incrementally. The engine spec's §21 deferred "read
strategy" pending the daemon question; the daemon exists, and the read strategy is the one just
stated — no index, no snapshotting, replay is cheap at current journal sizes. A future performance
decision would be its own issue, not an amendment here.

## 8. Decided direction — settled by the register, not yet in the tree

This section exists so the next arrival does not read as a redesign. Each item is decided; its
implementation *fulfills* this spec. Nothing else belongs here — an undecided question goes to §9.

- **The orchestrator enters the room (M26, #778).** Operator decision, 2026-07-30: proving the
  room works includes an orchestrator working *in* it. The floor: a resident orchestrator holds
  lanes of work with **every** decision escalated to a person — the delegated-authority machinery
  in its degenerate case. The decider-identity field on `ExternalDecisionRecorded` (§3) is the
  hook it arrives through; grants beyond "escalate everything" deepen later and are not specified
  anywhere yet.
- **Quota exhaustion becomes classifiable (0026, #594).** Decision 0026 settled the shape
  (`ExhaustedUntil`); the enum at HEAD still carries only `Permanent | Retryable`, and a quota
  failure is interim-classified `Retryable`. Decided, awaiting build — not stale, not open.
- **The room owns memory (0016/0021, #672).** Decided direction: room memory as a versioned,
  visible, ordinary document shared across vendors, with workers proposing additions. Nothing in
  the tree implements it; #672 tracks the gap.

## 9. Filed, not answered

Questions this document surfaced or inherited that require decisions, each filed rather than
resolved in text: #443 (noun convergence in identifiers), #447 (the ViewModel out of the daemon),
#774 (liveness), #775 (live worker output), #745 (event timestamps), #672 (room memory), #594
(quota classification build-out). When one lands, this list shrinks; it never grows silently — a
new undecided question gets a new issue first.
