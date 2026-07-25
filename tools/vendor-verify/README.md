# `vendor-verify` — re-run the vendor behaviours AER depends on

```
pixi run vendor-verify                        # every check that needs no special authorisation
pixi run vendor-verify -- --list              # names, groups, and what each one claims
pixi run vendor-verify -- --only gate         # one group: gate | fanout | cost | lifecycle | agy
pixi run vendor-verify -- --allow-config-writes   # also the checks that touch real settings files
```

## Why this exists

The vendor tooling in this repo has three legs, and they answer different questions:

| tool | question |
|---|---|
| `pixi run vendor-probe` | what can the installed CLIs *do* (capability matrix + version lock) |
| `pixi run vendor-survey` | what do the vendors *say* (doc corpus mirror + constraint harvest) |
| `pixi run vendor-verify` | do the behaviours we *designed against* still hold |

The audit in `docs/vendor-doc-audit.md` found **four vendor statements that were wrong** and two that
contradicted each other. So documentation is a claim, not a fact — and a doc page changing is a
reason to re-run these checks, not a reason to believe the new page.

Before this existed, each of these behaviours was established once by an ad-hoc script in a
temporary directory that got deleted with the session. That is the exact failure mode that made
`vendor-probe` necessary: decision 0015 inverted its whole mechanism on a `--permission-prompt-tool`
row that nobody could re-run.

## The two rules

Both were learned by getting them wrong first, and both are non-negotiable for anything added here.

**1. One variable per check — always a control arm.**
`gate.requires-user-interaction` uses two MCP tools that are byte-identical except for the
`_meta` annotation. `gate.hook-exit-2-beats-allow` runs the same hook and the same allow rule twice,
changing only the exit code. Without the control, "the tool did not run" is equally consistent with
"the gate held" and "the model never tried" — a negative from an instrument that cannot distinguish
two causes is not evidence.

**2. Prove execution with a side effect, never with the model's prose.**
Every check asserts on a **sentinel file** written by the tool or the hook itself. A model will
report calling a tool it never called. A hook whose *command* fails looks exactly like a hook that
never fired — this audit concluded twice that agy CLI hooks were broken, when the real cause was a
leading backslash in a JSON-escaped path producing exit 127. The vendor's own logs had said so all
along.

A check that cannot separate its cases must return `INCONCLUSIVE`. That is a real result, and it is
more useful than a confident wrong one.

### The instruments, and what each one can't see

Picking the instrument is most of the work. Three are in use here, in increasing strength:

| instrument | proves | blind to |
|---|---|---|
| **sentinel file** written by the tool | that specific tool ran | *which* agent ran it — a subagent can write the file its child was supposed to write |
| **hook fire count** + a discovery control on `PreToolUse` | the event occurred, and the config was loaded | nothing, *provided* the control arm fires |
| **`SubagentStart`/`SubagentStop` timeline** | how many agents the CLI actually started, and their overlap | nothing the model can fake |

`fanout.nesting-off-by-default` went through all three. Prose first (worthless — a model will
describe a nested spawn it never performed), then a sentinel file (ambiguous — the middle subagent
can just write the file itself, byte-identically), and finally counting spawns. Each redo was
prompted by asking what *else* could produce the same observation.

## Reading the output

| status | meaning |
|---|---|
| `PASS` | the behaviour AER depends on still holds |
| `FAIL` | **it changed** — a decision may now rest on something untrue. Exit code 1. |
| `INCONCLUSIVE` | the control arm didn't establish a baseline; the check proved nothing |
| `SKIPPED` | needs `--allow-config-writes` |

**Every check asserts the *measured* behaviour, not the documented one.** Where the two disagree —
`fanout.nesting-allowed-by-default` and `gate.allowedtools-is-preapproval-not-ceiling` both
contradict their docs — `PASS` means "still contradicting, as recorded". Encoding the vendor's
version instead would leave those checks permanently red and make a real change indistinguishable
from the known discrepancy. The check name states what is true, so a `FAIL` always means *something
moved*.

## Cost and safety

Every check starts a real CLI session and spends real subscription usage, so this never runs in CI
— the same permanent-human-action-item rule as `smoke-*` and `vendor-probe`.

Checks are `safe` unless marked otherwise: **no configuration is read, copied, or modified**, and
the operator's `~/.claude` and `~/.gemini` settings are untouched. A check marked `mutates-config`
is skipped unless `--allow-config-writes` is passed; it copies the file byte-exact, adds exactly
one key, restores in a `finally`, and re-verifies the sha256 — printing a loud warning and keeping
the backup if the restore doesn't match.

**One thing `safe` does not mean.** Every `claude -p` invocation writes a session transcript into
`~/.claude/projects/<cwd-slug>/`, exactly as any ordinary CLI run does — and since each arm uses a
fresh temp working directory, a full suite would otherwise leave ~50 orphan project directories
there. The runner therefore records what exists before the run and sweeps only the directories it
created, and only ones slugged under the OS temp root. Nothing pre-existing is touched. An earlier
version of this section claimed nothing was written outside the temp dirs; that was wrong.

`CLAUDE_*` environment variables are stripped before every invocation, so a check probes the vendor
CLI rather than the harness that launched it. A check testing a specific `CLAUDE_CODE_*` knob sets
that one back — it is the variable under test.

## Layout

```
verify.py            the runner; one @check-decorated function per behaviour
servers/
  mcp_gate_server.py    control tool + gated tool, identical but for requiresUserInteraction
  mcp_prompt_tool.py    a --permission-prompt-tool that always answers allow
  mcp_slow_server.py    blocks AER_BLOCK_SECONDS with no progress notifications
```

Groups: `gate` (what actually stops an action), `fanout` (subagent depth and concurrency), `cost`
(spend and structured output), `durability` (sessions and config roots), `lifecycle` (daemon and
background sessions), `agy`.

## Running it

A full run is long — every arm is a real CLI session. Run it **per group**, in the background, and
**unbuffered**:

```
python -u tools/vendor-verify/verify.py --only gate
```

Without `-u`, Python holds `print` output in an 8 KB block buffer when stdout is redirected, so a
run in progress looks identical to a run producing nothing, and a run killed on a timeout loses
everything it had found.

## Adding a check

```python
@check("group.short-name", "group", "the one-sentence claim being tested")
def _my_check():
    ...
    return PASS, "what was observed"
```

Give it a control arm, assert on a file, and return `INCONCLUSIVE` when the control arm didn't
establish a baseline. Then record the result in the "Verified by running it" section of
`docs/vendor-doc-audit.md` and strike the row from the backlog in `docs/vendor-coverage.md`.

## What is *not* here

Group F of the backlog in `docs/vendor-coverage.md` — claims that cannot be established from this
machine at all (claude's OS-enforced sandbox doesn't exist on native Windows; org/managed settings
need an organisation; Remote Control push needs a paired device). Those are listed as unestablishable
rather than pending, so they stop looking like work.
