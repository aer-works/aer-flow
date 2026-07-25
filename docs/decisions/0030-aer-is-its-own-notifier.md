# 0030 — AER is its own notifier: no vendor event announces a pause (amends 0018)

Status: accepted
Date: 2026-07-25

[0018](0018-attention-is-the-primary-signal.md)'s decision — attention orders the list, notifications
inform and never decide — is unchanged and was not in question. What this record supplies is the
thing 0018 assumed and never named: **where the signal comes from.**

## Context

0018 was written expecting a vendor hook event to announce that a room needs attention, with
`PermissionRequest` the obvious candidate. Measurement removed both candidates.

`PermissionRequest` fires **"when a permission dialog appears"** — and under `-p` no dialog ever
appears, so it never fires headless (`gate.permission-request-not-headless`). `Notification` is
silent under `-p` as well. Both were measured with a **discovery control**: the same hook command
registered on `PreToolUse` in the same settings file, which fired every time. That control is what
makes the zeros results rather than the third silent-negative of this audit — without it, "the event
did not fire" and "the settings file did not load" are indistinguishable, which is precisely how two
earlier conclusions in #527 came out wrong.

The full headless surface was then measured in one run, and the split matters more than the list:

- **Fires under `-p` (10):** `SessionStart`, `UserPromptSubmit`, `InstructionsLoaded`, `PreToolUse`,
  `PostToolUse`, `PostToolBatch`, `MessageDisplay`, `SubagentStart`, `SubagentStop`, `Stop`
- **Silent, and the condition genuinely arose:** `PermissionRequest`, `PermissionDenied`,
  `Notification`
- **Silent only because the task never created the condition** — untested, *not* absent:
  `PostToolUseFailure`, `PreCompact`/`PostCompact`, `StopFailure`, `TaskCreated`/`TaskCompleted`,
  `Elicitation`, `CwdChanged`, `ConfigChange`, `UserPromptExpansion`

A single "did not fire" list would have asserted eleven untested things as findings.

## Decision

**AER is the notifier. It does not learn that a pause exists — it creates the pause, and the
notification is emitted from that act.**

The gate is AER's own MCP tool and AER's own `PreToolUse` hook
([0029](0029-the-gate-is-three-mechanisms.md)). Both run inside AER's process boundary. So the
moment a worker's call reaches the gate, AER already holds the kind, the question, and the vendor's
correlation id — everything the notification needs — **before** any vendor event could have reported
it. Waiting for a vendor to announce a state AER itself authored is backwards, and on the headless
path there is no such announcement to wait for.

**This makes the notification path vendor-independent**, which is the correct place for it under
Architecture Rule 2: no adapter has to synthesise a missing event, because no adapter is the source
of the signal.

**Persist at ask-time, notify from the same act.** 0015's gate-durability rule already requires the
room to record a pause the instant the tool is invoked. The notification is emitted from that same
write. One event, one source of truth, and a pause recovered from disk after a restart notifies
identically to a live one — the operator cannot tell the host restarted.

**A worker that ends without asking is a different state, and AER can see it.** `Stop` fires
reliably under `-p`. A room whose worker stopped with no gate recorded is *finished*, not *needing
you* — and that distinction is now observable rather than inferred from silence, which is what
0018's "silence must be earned" requires at the room level.

## Rests on

| fact | how we know | if false |
|---|---|---|
| `PermissionRequest` and `Notification` never fire under `-p` | **measured** with a `PreToolUse` discovery control — `pixi run vendor-verify -- --only gate.permission-request-not-headless`, `--only gate.headless-event-surface` | a vendor event *could* carry the signal; this record is unnecessary though not wrong, and 0018's original assumption is restored |
| `PreToolUse` and `Stop` fire reliably under `-p` | **measured** — `--only gate.headless-event-surface` | AER's own gate cannot observe worker activity headless and the whole notification path needs rebuilding on the vendor's stream output |
| AER hosts the MCP gate, so it holds the pause data at ask-time | **structural** — follows from [0029](0029-the-gate-is-three-mechanisms.md), not an external fact | if 0029's mechanism changes, re-derive; this record's premise is 0029's conclusion |
| `PermissionDenied` never fires under `-p` | **assumed** — it logged zero, but nothing established that a denial actually occurred, so the arm is unresolved | a fourth event exists that reports denials headless; worth having, does not change the decision |
| The `Elicitation` hook event's headless behaviour | **untested** — the measuring run registered no MCP server, so its zero means nothing | if it fires, AER gains a second observation point on its own elicitations — redundant, since AER is the server |

## Consequences

**Easier.** The notification path stops depending on vendor behaviour entirely, so it neither breaks
on a vendor update nor differs between `claude` and `agy`. It is testable without a vendor: the gate
is AER's code, and its notification can be asserted in an ordinary unit test.

**Harder.** AER now owns a guarantee it cannot delegate — if AER's gate does not fire, nothing
notifies, and the failure is silent in exactly the way [0018](0018-attention-is-the-primary-signal.md)
says is most dangerous: a calm screen that has not earned its calm. This is the same obligation
0029 imposes from the other side, and it is one mechanism, not two: **the startup self-check that
proves the hook fires is also what entitles the room list to render "nothing needs you".**

**Obliges us to** emit the notification from the same durable write that records the pause (never a
second code path that could diverge), treat a worker that `Stop`s with no recorded gate as finished
rather than silent, and carry the freshness claim 0018 requires from AER's own liveness rather than
from any vendor signal.

**Amends [0018](0018-attention-is-the-primary-signal.md)**; its bands, its ordering rule, its
"notifications never decide" constraint and [0026](0026-running-out-of-plan-is-a-state-not-a-failure.md)'s
band amendment all stand unchanged.

Related: #527 (the audit), #529, #448 (the concurrency cap whose "queued" is a band in 0018),
#336/#337 (the switcher this orders).
