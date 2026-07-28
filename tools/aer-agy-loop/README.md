# `aer-agy-loop` — dispatch one AER workflow step, read back its output

```
pixi run aer-dispatch -- --list-templates

pixi run aer-dispatch -- \
    [--template advise|implement|review|fact-check] \
    --prompt-file <path> \
    --output-name <name> \
    --working-directory <absolute path> \
    [--adapter gemini] [--model <name>] [--effort <level>] \
    [--read-files|--no-read-files] [--write-files|--no-write-files] \
    [--run-shell-commands|--no-run-shell-commands] [--network-access|--no-network-access] \
    [--timeout-minutes 20]
```

Prints the produced output file's content to stdout. On failure, prints `aer run`'s own output plus
the raw `flow.jsonl` event log to stderr (the CLI's terminal summary alone — `Workflow status:
Terminal / worker: Failed` — carries no diagnostic detail; the log usually does) and exits non-zero.

## Why this exists

Every one of these was hand-rolled with an ad-hoc Node one-liner during the #513 cross-vendor
orchestration trial, and got a different bug each time:

- `WorkflowTemplateVersion` is an `int`, not a semver string.
- `Steps[].Inputs` and `Contract.OptionalMetadata` are JSON arrays, not objects.
- `--task-dir` must be absolute. A relative one resolves against the CLI's own cwd, but `agy` runs
  with cwd set to `WorkingDirectory` (`GeminiWorkerAdapter.cs`: `agy -p` ignores the process working
  directory entirely). A relative task-dir plus an explicit `WorkingDirectory` silently produces an
  `AER_OUTPUT_DIR` the dispatched process resolves against the wrong root — the run exits 0, the
  step is reported `Failed`, and nothing says why.

Exactly the failure mode `tools/vendor-verify/README.md` already names: established once, in a
scratch directory, thrown away with the session. This exists so the next dispatch doesn't re-pay
for the same three bugs.

## What this is not

A single-step dispatch primitive, not a loop orchestrator. Whether a reviewer's verdict means "loop
back to the implementer with these findings" is a decision this script does not make — that stays
with whoever is orchestrating the exchange. Automating that decision into glue code would be the
same shape of mistake Architecture Rule 1 already forbids inside the engine itself (Flow must never
parse conversation content to make routing decisions) — this just names that the same discipline
applies one layer up, in tooling that could otherwise grow into a shadow engine.

## Templates — pick the role, not the settings

`--template advise|implement|review|fact-check` pins vendor, model, effort, permission grant and
timeout as a set. Run `pixi run aer-dispatch -- --list-templates` for what each one is
for and what it resolves to; the definitions and the reasoning behind each setting live next to the
`TEMPLATES` dict in `dispatch.py`, and are deliberately not restated here.

Two things worth knowing before reaching for one:

- **Precedence is explicit flag > template > built-in default**, so `--template review --model haiku`
  does what it says. That is intentional: the templates are a starting point you can override, not a
  lock.
- **Every template grants write, including the reviewing ones.** A worker satisfies its
  `ProducedOutputs` contract only by writing the artifact into `AER_OUTPUT_DIR`. With writes *and*
  the shell both withheld, nothing can produce that file — measured on **claude/haiku**, both arms:
  withheld → `Contract not satisfied`; `--write-files` → `Succeeded`. That combination is refused
  here before it can spend. The gemini equivalent is **unmeasured** — there is no deny-list on that
  path, `WriteFiles:false` resolves to `--mode plan` — so the refusal is conservative there rather
  than evidenced.
- **Withholding writes while granting the shell does *not* stop a worker writing.**
  [#529](https://github.com/aer-works/aer-flow/issues/529) measured the file being created anyway by
  `Bash` on claude. On gemini this is inferred from the same substitution argument, not measured —
  and note `--dangerously-skip-permissions` is *not* the reason: the `PreToolUse` hook derives its
  deny list from all four grant categories and takes the flag's over-grant back. So that combination
  is allowed through here — it is satisfiable, and pretending otherwise would be a claim wider than
  the evidence. **"Read-only reviewer" is still not expressible**
  ([#629](https://github.com/aer-works/aer-flow/issues/629)); what is expressible is "no writes and
  no shell", which cannot report.
- **So the spread is one axis, not four shapes.** All four sit at read + write; only `implement` adds
  shell + network, which is the path #596, #611, #623 and #624 all came from. A session that only
  ever dispatches reviews never exercises that half of AER — the value is in reaching for `implement`
  sometimes, not in the set being varied.

A pinned `agy` model name is checked against `agy models` (as recorded in
`docs/vendor-capabilities.md`) by STEP 9 of `pixi run audit-completeness` — the first draft of these
templates shipped a name the CLI does not accept, and prose did not catch it.

## Using this for an advisor consult

The `advise` template is this, pinned. `--model gemini-3.1-pro-high` (agy's Pro tier, high reasoning
effort — see `agy models` for the full catalogue) is a reasonable default when dispatching a consult
rather than an implementation or review task.

**Ground it — don't ask it cold.** A bare knowledge question about a fast-moving CLI is a
training-data-staleness risk, not just a style preference: asked "what CLI flag does agy use to
auto-approve every tool permission request?" with nothing to read, `gemini-3.1-pro-high` answered
`--yolo` — confidently, and wrong. The real flag is `--dangerously-skip-permissions`. That wasn't
pure fabrication: `-y`/`--yolo` was a real flag on Google's Gemini CLI, the line `agy` (Antigravity
CLI) evolved from, and the model correctly recalled a real fact about a related tool without
noticing the two have since diverged — nothing in a bare question prompts that check. The fix isn't
"trust it less," it's "give it something to read": paste the actual current `agy --help` output (or
point it at `docs/vendor-doc-audit.md`, which records measured — not vendor-claimed — behavior) into
the prompt rather than asking from its own memory. This generalizes past this one flag: any fact
about this project's own fast-moving tooling should be grounded the same way.

**Higher effort does not fix this.** Re-asked the identical bare question at `--effort high`
(`gemini-3.1-pro-high --effort high`), the model gave the exact same wrong answer (`--yolo`) it gave
without an explicit effort setting. This isn't surprising in hindsight: effort controls how much a
model deliberates on *working through* a problem, not whether it *checks* a fact it already believes
it knows. A pure-recall question has nothing to deliberate on — more effort just delivers the same
wrong memory more confidently, it doesn't verify it. Grounding (see above) is the only thing that
actually helps; bumping effort is not a substitute for it.

## The shell-commands guard

`--run-shell-commands` without `--network-access` is refused client-side, before ever calling `aer
run`: as of this writing, `GeminiWorkerAdapter` has no way to unlock shell commands without also
unlocking network access (`--dangerously-skip-permissions` is the only confirmed non-interactive
bypass agy exposes, and it grants everything together — see the adapter's own
`TryTranslatePermissionGrant`). Requesting shell without network is a combination nothing can honor
without over-granting, so this fails fast with an explanation instead of dispatching something the
adapter will refuse anyway.
