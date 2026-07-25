# `aer-agy-loop` — dispatch one AER workflow step, read back its output

```
pixi run aer-dispatch -- \
    --prompt-file <path> \
    --output-name <name> \
    --working-directory <absolute path> \
    [--adapter gemini] [--model <name>] \
    [--read-files] [--write-files] [--run-shell-commands] [--network-access] \
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

## The shell-commands guard

`--run-shell-commands` without `--network-access` is refused client-side, before ever calling `aer
run`: as of this writing, `GeminiWorkerAdapter` has no way to unlock shell commands without also
unlocking network access (`--dangerously-skip-permissions` is the only confirmed non-interactive
bypass agy exposes, and it grants everything together — see the adapter's own
`TryTranslatePermissionGrant`). Requesting shell without network is a combination nothing can honor
without over-granting, so this fails fast with an explanation instead of dispatching something the
adapter will refuse anyway.
