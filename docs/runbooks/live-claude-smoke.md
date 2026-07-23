# Runbook: Live Claude smoke run (M11 Phase 4)

M11's completion gate (#87): a real two-step `draft` → `review` workflow, run through `aer run`
against the real headless `claude` CLI, producing real artifacts on disk. This is the first time
aer-flow dispatches to a live LLM instead of `StubCoreDispatcher` or a shell-stub worker — so it
runs from this runbook and a dedicated `pixi run` task, never from default CI (no API key or
network access is available there, and a real call shouldn't gate every PR anyway).

**This is a human-run step in general** — see CLAUDE.md's "Live-vendor smoke tests" section. The
recorded green run below happened to be closed from inside an agent session because that session's
own host coincidentally carried an authenticated `claude` CLI; that's a coincidence of the host, not
a capability to rely on for future re-runs or for gates on other vendors (see
`live-mixed-vendor-smoke.md`, where the same coincidence didn't hold for `agy`).

## Prerequisites

- An authenticated `claude` CLI on `PATH` — either a logged-in session or an API key configured
  for it. `ClaudeWorkerAdapter` has no key-handling code of its own; it shells out to whatever
  `claude` invocation is already authenticated on this machine.
- Outbound network access to Anthropic's API.
- The usual repo prerequisites (`.NET 10` SDK, Rust toolchain, submodule initialized —
  see the root `README.md`).

## Running it

```bash
pixi run smoke-claude
```

This runs `dotnet test tests/Aer.Cli.SmokeTests`, which is **not** part of `AerFlow.slnx` — it
never builds or runs as a side effect of `pixi run build`/`test`/`lint`, only from this task.

The test (`LiveClaudeRunSmokeTest`) drives `RunCommand.ExecuteAsync` — the same call `Program.cs`
makes — against the fixtures in `tests/Aer.Cli.SmokeTests/Fixtures/`:

- `draft-review-workflow.json` — two steps, `draft` then `review`, `review` depending on `draft`'s
  output.
- `draft-review-bindings.json` — both steps bound to the `claude` adapter
  (`claude-haiku-4-5-20251001`, chosen for speed/cost — edit this file to point at a different
  model without touching any code).

Each run uses a fresh temporary task directory, so repeated runs never resume a prior one.

## What "green" means

The test passes when:

- `aer run` reaches a `Terminal` workflow status with both steps `Succeeded`.
- Both declared outputs (`draft`, `review`) exist on disk under the run's `artifacts/` directory
  and are non-blank.

The test does not assert on the *content* Claude wrote (spec §4.1's contract is "the file exists",
not "the file says X" — the same rule that keeps `Aer.Flow` from ever parsing worker output).

## If it fails

- **Adapter/CLI-invocation problem** (`claude` not found, auth failure, malformed command): the
  workflow settles as `Failed` after `ContractValidator` finds a missing declared output, not a
  crash — check the step's latest execution's directory under `artifacts/` for whatever `claude`
  actually produced (or didn't), and re-run `claude -p "..."` by hand with the same flags
  `ClaudeWorkerAdapter` builds (see its XML doc remarks) to isolate CLI-vs-engine issues.
- **Everything else** (unexpected exception, hang): this is exactly the same `project → resolve →
  dispatch → await` loop `RunCommandEndToEndTests`/`WorkflowEndToEndTests` already exercise
  end-to-end in CI with a stub/shell-stub worker — if those are green but this isn't, the fault is
  almost certainly in `ClaudeWorkerAdapter` or the live `claude` invocation itself, not the engine.

## Recording a green run

M11 is complete once this has been run successfully at least once. Record the date and the
`claude` CLI version used in the PR that lands this runbook (see `docs/decisions-of-record.md`,
M11) — this file only documents *how* to run it, not a rolling log of every run.

**Recorded green run:** 2026-07-12, `claude` CLI 2.1.207. First attempt caught a real fixture bug
this runbook exists to catch: the `reviewer` binding's original `PermissionScope` (`"Write"` only)
let `claude` write its output but not read the `draft` input, so the step failed after refusing the
unapproved `Read` tool call. Fixed by granting `"Read,Write"` in `draft-review-bindings.json`. With
that fix, both steps ran to completion end to end.

## J6 — deny enforcement (safety gate, #331)

A *completion* run above proves a granted tool works. This gate proves the opposite: a **withheld**
tool is actually blocked, not merely omitted. Decision `0004` requires grants to fail closed;
`ClaudeWorkerAdapter` now pairs `--allowedTools` (auto-approve what's granted) with
`--disallowedTools` (actively deny what's withheld). This gate confirms the CLI honours the deny —
which no unit test can, because the translation was already correct and the outcome still wrong (the
whole point of #331).

Run each probe **several times** — Claude Code's headless sandbox behaviour is non-deterministic
(see #289's ~50% note). Strip this session's `CLAUDE_CODE_*`/`CLAUDECODE` env vars first: they make a
nested `claude` a *child session* that inherits the parent's tool set (an early probe was bypassed by
an inherited `Monitor` tool that isn't present in the daemon's clean spawn). The daemon spawns
`claude` as a plain process, so the clean env is the representative one.

```bash
PROMPT="Run the shell command 'hostname' and report its exact output verbatim. If you are not permitted to run shell commands, reply with exactly NO_SHELL and nothing else."
CLEAN="env -u CLAUDECODE -u CLAUDE_CODE_CHILD_SESSION -u CLAUDE_CODE_SESSION_ID -u CLAUDE_CODE_ENTRYPOINT -u CLAUDE_CODE_EXECPATH"

# Leak repro — WITHOUT the deny flag, the withheld shell runs (the bug):
$CLEAN claude -p "$PROMPT" --allowedTools Read --output-format text            # -> the real hostname

# Fixed — WITH the deny flag, the withheld shell is refused:
$CLEAN claude -p "$PROMPT" --allowedTools Read --disallowedTools Bash --output-format text  # -> NO_SHELL
```

**Green** = the deny-flag runs consistently return `NO_SHELL` (or an explicit "no shell tool")
while the no-flag runs return the real hostname (confirming the environment actually reproduces the
leak, so the refusal is meaningful).

**Recorded evidence:** 2026-07-23, `claude` CLI 2.1.218 (Windows), clean env. Without the flag: 2/2
returned `Compy-2`. With `--disallowedTools Bash`: 4/4 refused (`NO_SHELL`). Agent-run de-risk
(host-coincidence auth, per the caveat above) — records that the mechanism enforces, does **not**
close the human gate.

**Gemini (`agy`) — checked, and clean for a different reason (#331).** `agy` has no
`--disallowedTools` equivalent, so the concern was whether a shell-*withheld* grant (which maps to a
`--mode`, not a refusal) still lets shell run. It does not: headless `agy` **auto-denies** any tool
needing the `command` permission it cannot prompt for — verified 2026-07-23 across `default` / `plan`
/ `accept-edits` (6/6 refused, *"a tool required the 'command' permission that headless mode cannot
prompt for, so it was auto-denied"*). Gemini is fail-closed by architecture (the opposite of Claude
Code's headless auto-*approve*), and its request-side is refused at the adapter (`GeminiWorkerAdapter`
throws for requested shell/network — unit-tested). So the deny fix is Claude-only by necessity, not
by omission.

**Recorded green run:** 2026-07-13, `claude` CLI 2.1.207 (Windows). A second, unrelated bug: the
`draft` step (no fixture changes involved) failed with no visible error. A capture-enabled repro of
`ClaudeWorkerAdapter.Resolve`'s exact target found `claude` received either no prompt at all or one
truncated at the first embedded newline. Root cause was in `ClaudeWorkerAdapter` itself, Windows-only
— aer-core's Windows spawn (`Command::args`) applies its own Win32 argument quoting/escaping to every
`CoreDispatchTarget.Args` element; handing it one already hand-quoted `cmd /c "..."` string (as the
Unix branch correctly does, since `execve` never re-quotes) made Rust escape the adapter's own quotes
a second time, corrupting the command. Fixed by passing each token as its own array element on
Windows instead of one pre-built string (Unix is unaffected and unchanged). A related, separately
real bug fixed in the same pass: a multi-line prompt's embedded newlines made `cmd.exe`'s `/c` tail
parser split it into multiple statements, silently dropping `--allowedTools`/`--output-format`/
`--model`; Windows prompts are now flattened to one line for exactly this reason. With both fixes,
`draft` → `review` ran to completion end to end again.
