# Runbook: Live interactive-session smoke run (M24 Phase 1/2, retroactive)

M24 Phase 1's own issue (#262) listed six verification checks; only live in-turn streaming had any
test coverage when the phase was marked closed. This runbook, added retroactively, closes the two
of the remaining five that are actually live-verifiable in automated form:

- A ~50-turn stress run at real chat frequency against the real, authenticated `claude` CLI,
  confirming the repeated-supersede pattern (projection/replay cost, artifact directory naming)
  holds at real cadence, not just against a fast local stub.
- A real compact round trip: after the fix landed alongside this runbook (compact was previously
  just forwarding `/compact` to the resumed vendor session instead of actually starting a fresh one
  — see the "fix(daemon): Compact actually starts a fresh native session" commit), this confirms the
  live `claude` CLI produces a real, substantial summary from a fresh native session, not just that
  the AER-side plumbing runs.

**This is always a human-run step, not something an agent session can close on its own** — see
`CLAUDE.md`'s "Live-vendor smoke tests" section.

## What this does NOT cover — still manual / unautomated

- **Vendor-handoff-retains-context** (#262's other stress item: turn 2 goes to a different adapter
  than turn 1 and still has turn 1's context) — needs both `claude` and `agy` authenticated and a
  qualitative judgment call on whether the handoff turn's response actually reflects turn 1's
  content, which isn't something to fake with a length-based assertion the way this runbook's
  compact check does. Exercise manually: start a session with `claude`, send one turn, then
  `POST /api/sessions/send` with `Adapter: "gemini"` and confirm the response references turn 1's
  content.
- **Minimal-overhead (`--bare`) latency comparison** — not currently possible at all, automated or
  manual, without a code change: `InteractiveSessionMaterializer.Materialize` hardcodes
  `MinimalOverhead: true` for every interactive session (`InteractiveSessions.cs:115`), and neither
  `StartSessionRequest` nor `SendSessionMessageRequest` exposes a way to override it. A real
  comparison needs a temporary adapter-level or request-level toggle added first — flagged here as a
  known gap in the verification gate itself, not silently worked around.

## Prerequisites

- An authenticated `claude` CLI on `PATH` — see
  [`live-claude-smoke.md`](./live-claude-smoke.md)'s prerequisites; unchanged here.
- Outbound network access to Anthropic's API.
- The usual repo prerequisites (`.NET 10` SDK, Rust toolchain, submodule initialized).
- Expect real subscription usage: 51 real turns (50 chat turns + 1 compact turn), each a real `claude
  --print` invocation. Budget several minutes to tens of minutes depending on response length and
  API latency.

## Running it

```bash
pixi run smoke-session
```

This runs `dotnet test tests/Aer.Cli.SmokeTests` filtered to `LiveSessionSmokeTest` — same project
as `smoke-claude`, still **not** part of `AerFlow.slnx`, so it never builds or runs as a side effect
of `pixi run build`/`test`/`lint`.

Unlike `smoke-claude`/`smoke-mixed-vendor`/`smoke-dialogue`, this test doesn't call
`RunCommand.ExecuteAsync` — interactive sessions have no `aer` CLI command at all, so it starts a
real `Aer.Daemon` instance (`DaemonHost.RunDaemonAsync`, real `WorkerAdapterRegistry.Default`, not a
stub) on a dynamically OS-assigned port (issue #296 — avoids colliding with another concurrent test
run) and drives it exactly the way `Aer.Ui`/`Aer.Mobile` would: `POST
/api/sessions/start`, 49 more `POST /api/sessions/send` calls, then `POST
/api/sessions/{id}/compact`.

## What "green" means

The test passes when:

- All 50 turns complete and are persisted in order (`SessionMetadata.Turns` has 50 entries with
  sequential `TurnIndex`).
- The compact turn takes the handoff branch (`VendorHandoffSynthesized = true`) and a new native
  vendor session id is issued.
- The compact turn's `AssistantResponse` is real, non-blank, and substantial (over 40 characters) —
  not asserting its exact content (this repo's convention against parsing worker output), just that
  the live CLI produced a real summary rather than an empty or error reply.

The test does not assert on any turn's exact text content, and does not assert a specific wall-clock
time — record the stress-run duration it prints to console output as a data point, not a pass/fail
gate.

## If it fails

- **A turn never completes / times out**: check `~/.aer/sessions/live-session-smoke-*` on disk for
  the actual materialized session and its `bindings.json`/`.aer/session.json` — the same triage
  approach as `live-claude-smoke.md`.
- **Compact's response is blank or trivially short**: the live `claude` CLI may have declined or
  errored on the `/compact` prompt — re-run `claude --print --session-id <the CurrentVendorSessionId
  from before compact> "/compact ..."` by hand to isolate CLI-vs-engine issues, matching the fresh
  native-session-id `ExecuteSessionTurnAsync`'s handoff branch issues (a brand-new session id, not a
  resume — check `.aer/session.json`'s `CurrentVendorSessionId` before and after).
- **Everything else**: the daemon HTTP surface itself is proven in CI via `DaemonIntegrationTests`
  and `SessionTurnBranchingTests` (stub-adapter coverage of the same branching logic) — if those are
  green but this isn't, the fault is in the live `claude` CLI invocation, not the engine or daemon.

## Recording a green run

Record the date, `claude` CLI version, and the printed stress-run duration in the PR that lands this
runbook — this file only documents *how* to run it, not a rolling log of every run.
