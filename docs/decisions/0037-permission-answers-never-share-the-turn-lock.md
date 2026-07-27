# 0037 — A permission answer must never share the per-session turn lock

Status: accepted
Date: 2026-07-26

## Context

`docs/plan.md` has carried an open question since M25: *"whether AER's per-session turn lock
tolerates a turn held open while a human answers a permission (#393 ↔ #445). The vendor half is
measured; this half is not."* [0029](0029-the-gate-is-three-mechanisms.md) measured that a real,
live permission gate holds the vendor CLI's turn open for as long as 162 seconds while a human
answers out of band — but never checked what that means for AER's *own* per-session serialization.

Checking the real code rather than reasoning abstractly: `#393`'s turn lock
(`Aer.Daemon.Program.SessionTurnLocks`, a `SemaphoreSlim(1, 1)` per session directory) is acquired in
exactly one place, `ExecuteSessionTurnAsync`, held for the full duration of processing one turn —
which includes however long the spawned vendor CLI process runs, which in turn includes however long
it sits blocked on its own MCP `tools/call` waiting for a permission answer. The lock is only ever
released at the end of that same method.

**The component that would create a conflict does not exist yet.** No production MCP server or
permission-answer endpoint is wired into `Aer.Daemon` today (the same gap
[0035](0035-aer-yield-is-a-structured-mcp-tool-not-a-sentinel.md) found for `aer yield` — 0029's own
measurements came entirely from `tools/vendor-verify`'s standalone probe, not from `src/`). So this
question cannot be *measured* against real behaviour; it can only be **designed against**, as a
constraint on whatever answer-path gets built.

## Decision

**A permission answer must never need the same session's turn lock the pending turn already holds.**
The precedent already exists in the codebase: `/api/tasks/decide` (resolving a `PausePoint` via
`DecisionType`) never touches `SessionTurnLockFor` — it calls `session.DecideAsync` directly, keyed
by `StepId`/`ExecutionId`, independent of any turn-level lock. Whoever builds the permission-gate's
answer path (alongside `#585`'s MCP server) must follow the same shape: correlate a pending permission
by its own id, reach the specific in-flight `tools/call` directly, and never route the answer through
`ExecuteSessionTurnAsync` or acquire `SessionTurnLockFor` to deliver it. Doing otherwise would
deadlock by construction — the turn holding the lock cannot finish until answered, and answering
would be waiting on a lock the turn will not release until it finishes.

**This resolves the open question by making it moot, not by measuring a system that does not exist
yet.** There is no current implementation to check for a genuine held-open turn — the resolution is a
constraint on the one that gets built, verifiable by code review against this record once it does.

## Rests on

| fact | how we know | if false |
|---|---|---|
| A live permission gate holds the vendor CLI's turn open for as long as a human takes to answer — measured at 162 seconds | **measured** — [0029](0029-the-gate-is-three-mechanisms.md) | the lock contention this record designs around never arises, and the separation it mandates is unnecessary |
| The turn lock is a per-session-directory `SemaphoreSlim(1, 1)`, acquired in exactly one place and held for the whole spawned process | **measured** — `Aer.Daemon.Program.SessionTurnLocks` and `ExecuteSessionTurnAsync`, read directly | the serialization shape differs, and the answer path may not in fact be blocked by the pending turn |
| `--session-id` is guarded by an existence check, not a lock — two concurrent processes both win | **measured** — `durability.session-id-guard-is-not-a-lock` (sentinel) | the vendor already serializes concurrent access, and AER's own lock is doing less work than this record assumes |

## Consequences

**Easier.** The pattern to follow already exists and is already tested (`/api/tasks/decide`) — this
is not new design, just naming the constraint before someone builds the permission-answer path
without it and discovers the deadlock the hard way.

**Harder.** Nothing new — this narrows a design space that was otherwise unconstrained, rather than
adding work.

**Obliges us to.** Flag this constraint explicitly in whatever issue scopes the permission-gate's
answer-path implementation (alongside `#585`), and verify the constraint by test once that endpoint
exists — a live send-a-turn-that-blocks-on-permission-then-answer-it integration test, structured the
same way `#531`'s live human run proved the vendor half.

Relates: [0029](0029-the-gate-is-three-mechanisms.md) (the mechanism whose answer-path this
constrains), [0035](0035-aer-yield-is-a-structured-mcp-tool-not-a-sentinel.md) (the same "no
production MCP server exists yet" gap, found the same way).
