# Runbook: Live dialogue smoke run (M17 Phase 5)

M17's completion gate, live half (#168): a real, bounded Claude ↔ `agy` exchange, run through
`aer run` against the dialogue worker (`Aer.Workers.Dialogue`), bound via the `"dialogue"` adapter
(`Aer.Adapters.DialogueWorkerAdapter`, M17 Phase 4). This is the first time this repo's own code
runs a Case 2 encapsulated multi-model worker against real vendor CLIs instead of stub scripts —
the thing `Aer.Cli.Tests.DialogueDispatchEndToEndTests` (M17 Phase 4) already proves unattended in
default CI on all three OSes, but only against stub vendor scripts.

**This is always a human-run step, not something an agent session can close on its own** — see
CLAUDE.md's "Live-vendor smoke tests" section. `DialogueRunner` shells out to whatever `claude`/
`agy` invocation is already authenticated on the host, the same subscription-only discipline
`ClaudeWorkerAdapter`/`GeminiWorkerAdapter` use for a top-level dispatch — nothing about that can
be provisioned headlessly from inside an agent session, and it shouldn't be worked around (e.g. by
dropping in an API key) just to make the gate pass.

## Prerequisites

- An authenticated `claude` CLI on `PATH` — see [`live-claude-smoke.md`](./live-claude-smoke.md)'s
  prerequisites; unchanged here.
- An authenticated `agy` (antigravity, Google Gemini's CLI) on `PATH` — see
  [`live-mixed-vendor-smoke.md`](./live-mixed-vendor-smoke.md)'s prerequisites; unchanged here.
- Outbound network access to both Anthropic's and Google's APIs.
- The usual repo prerequisites (`.NET 10` SDK, Rust toolchain, submodule initialized — see the
  root `README.md`).

## Running it

```bash
pixi run smoke-dialogue
```

This runs `dotnet test tests/Aer.Cli.SmokeTests` filtered to `LiveDialogueSmokeTest` — the same
project as `smoke-claude`/`smoke-mixed-vendor`, still **not** part of `AerFlow.slnx`, so it never
builds or runs as a side effect of `pixi run build`/`test`/`lint`.

The test drives `RunCommand.ExecuteAsync` — the same call `Program.cs` makes for `aer run` — against
a single-step workflow bound to the `"dialogue"` adapter. Unlike `smoke-claude`/
`smoke-mixed-vendor`, the workflow and bindings aren't static fixture files: the dialogue worker's
own config (`DialogueWorkerConfig`) is built and written to a temp directory at test run time, since
its `Participants` spawn `claude`/`agy` *directly* (no shell wrapper — see
`ProcessVendorTurnClient`), using the same one-shot-text-turn flags `ClaudeWorkerAdapter`/
`GeminiWorkerAdapter` build for a top-level dispatch, minus anything specific to Flow's own
`AER_INPUT_<n>`/`AER_OUTPUT_DIR` convention (a per-turn call inside the worker boundary never needs
it — see `DialogueParticipant`'s remarks).

The exchange is deliberately short (`TurnBudget: 2` — one turn per side, a one-sentence seed
prompt) to keep this smoke test cheap and fast, the same reasoning `live-claude-smoke.md`'s
single-sentence draft prompt uses.

Each run uses a fresh temporary task directory, so repeated runs never resume a prior one.

## What "green" means

The test passes when:

- `aer run` reaches a `Terminal` workflow status with the step `Succeeded`.
- `transcript.jsonl` exists under the execution's `artifacts/` directory with exactly 2 lines,
  each a schema-valid `TranscriptTurn` (`["initiator", "responder"]`, in order), with non-blank
  `Text`.
- The declared final output (`verdict.md`) exists on disk and is non-blank.

The test does not assert on the *content* either vendor wrote (spec §4.1's contract is "the file
exists", not "the file says X" — the same rule `live-claude-smoke.md`/`live-mixed-vendor-smoke.md`
document, which applies inside the dialogue worker's own turn loop too, per CLAUDE.md rule #1's
inversion described in `docs/decisions-of-record.md` (M17): the *worker* is allowed to read
turn text to thread context and detect a stop signal, but this runbook's assertions still never
depend on what either model actually said).

## If it fails

- **The step fails or never produces `transcript.jsonl`/`verdict.md`**: `DialogueRunner` throws
  `DialogueExecutionException` on a non-zero vendor exit or an empty turn, which `Program.cs` maps
  to a non-zero process exit — Flow's `OutcomeClassifier`/`ContractValidator` then see an ordinary
  failed worker (missing declared output), the same failure shape `ContractValidator` already
  handles for any other worker. Whatever transcript lines were appended for turns that succeeded
  *before* the failing one stay on disk under the execution's `artifacts/` directory as a forensic
  record — check them for whichever vendor's turn actually failed.
- **A specific vendor's turn is the problem**: re-run that vendor's exact command by hand — the
  per-`Participants`-entry `Command`/`Args` this runbook's test builds mirror
  `ClaudeWorkerAdapter`/`GeminiWorkerAdapter`'s own flags (`claude -p "..." --allowedTools Write
  --output-format text --model claude-haiku-4-5-20251001` / `agy -p "..." --mode accept-edits
  --model gemini-3-flash`) — to isolate a CLI-vs-worker issue. A clarifying question with no output
  is `agy`'s documented failure mode (spike #21); inside the dialogue worker this surfaces as an
  empty turn, which `DialogueRunner` already treats as a failure (M17 Phase 3).
- **Everything else** (unexpected exception, hang): the turn loop, termination, and dispatch
  integration are all already proven end to end against stub vendor scripts in
  `Aer.Workers.Dialogue.Tests` and `Aer.Cli.Tests.DialogueDispatchEndToEndTests` — if those are
  green but this isn't, the fault is almost certainly in the real `claude`/`agy` invocation itself,
  not the worker or the engine.

## Recording a green run

M17 is complete once this has been run successfully at least once. Record the date and both CLI
versions used in the PR that lands this runbook (see `docs/decisions-of-record.md`, M17) — this
file only documents *how* to run it, not a rolling log of every run.

**Recorded green run:** none yet — this is a human action item per CLAUDE.md's live-vendor rule.
