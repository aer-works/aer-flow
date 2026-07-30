# Runbook: `aer yield` MCP wiring (#585, decision 0035)

The wiring half of #585: `Aer.Workers.Dialogue` now spawns its own `Aer.Mcp.Host` instance per
participant (`DialogueYieldWiring`) instead of relying on the retired text-sentinel match. This
runbook is for confirming that wiring against a *real* vendor CLI — the sentinel retirement itself,
and the capture-file mechanism, are already covered unattended by
`Aer.Workers.Dialogue.Tests.DialogueRunnerTests` (stub `IVendorTurnClient`) and
`ProcessVendorTurnClientEndToEndTests` (real stub-script processes) in default CI. What those two
suites cannot prove is whether a real `claude`/`agy` CLI actually calls the `yield` tool when told
to, under the exact `--mcp-config`/`--strict-mcp-config` (claude) or workspace `--add-dir` (agy)
flags this wiring generates — that is a live-vendor claim, per CLAUDE.md's `right-instrument` gate,
and belongs here rather than in a unit test.

**This is always a human-run step, not something an agent session can close on its own** — see
CLAUDE.md's "Live-vendor smoke tests" section, same reasoning as every other runbook in this
directory.

## What already runs against the real CLIs today

`pixi run smoke-dialogue` (`docs/runbooks/live-dialogue-smoke.md`) already builds its two
participants via `DialogueParticipantPresets.For("claude", ...)` / `For("gemini", ...)`, whose
`Command` values (`"claude"`, `"agy"`) mean `DialogueYieldWiring` now wires them for MCP on every
run of that existing smoke test — the `--mcp-config`/`--strict-mcp-config`/`--add-dir` flags this
issue adds are already on the command line `smoke-dialogue` exercises, with no change needed to that
test. **What it does not yet assert** is that either participant actually calls `yield` — its debate
prompt has no reason to, and its assertions only check that both turns produced non-blank text and a
final output file exists. A green `smoke-dialogue` run after this wiring lands is evidence the new
flags don't break a normal (no-yield) exchange; it is not evidence the `yield` tool call itself
round-trips through a real vendor process.

## Verifying the yield round-trip itself (not yet scripted)

To directly confirm a real vendor CLI calls `yield` and `DialogueRunner` reads it back:

1. Build a `DialogueWorkerConfig` whose seed prompt explicitly instructs one participant to call the
   `yield` tool with `outcome: "concluded"` once it agrees with the other side (e.g. "State your
   position, then once you and the other participant agree, call the yield tool with outcome
   'concluded' and a short note").
2. Run it via `DialogueRunner`/`ProcessVendorTurnClient` the same way `smoke-dialogue` does (see that
   runbook's "Running it" section for the exact invocation shape), pointed at real `claude`/`agy`.
3. Confirm: the exchange ends before `TurnBudget` is exhausted, the ending `TranscriptTurn` has a
   non-null `YieldOutcome`, and no `yield-capture-*.json` file is left behind under the output
   directory (`DialogueYieldWiring.ReadAndConsumeCapture` deletes it once read).

No `pixi run` task or fixture exists for this scenario yet — scripting one (a
`LiveDialogueYieldSmokeTest` alongside `LiveDialogueSmokeTest`, and a `smoke-dialogue-yield` pixi
task depending on `smoke-preflight` like its siblings) is the natural next step, left undone this
session for the same reason every live-vendor gate is: it needs a real recorded run against an
authenticated CLI to mean anything, and CLAUDE.md's live-vendor-smoke rule says not to fake that with
a stub.

## Prerequisites (once the scenario above is scripted)

Same as `live-dialogue-smoke.md`: an authenticated `claude` CLI and an authenticated `agy` CLI both
on `PATH`, outbound network access to both vendors, the usual repo toolchain.

## Recording a green run

**Recorded green run:** none yet — this is a human action item per CLAUDE.md's live-vendor rule, and
the scenario itself isn't scripted yet (see above).
