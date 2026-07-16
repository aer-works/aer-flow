# Walkthrough: your first real workflow (draft → critique → send-back)

This is the "how do I actually use this" guide (M17 Phase 1, #164). Everything it uses shipped in
M11–M16; nothing here is new machinery. You will run a real two-role workflow — an **architect**
(Claude) writes a design proposal, a **critic** (Gemini/`agy`) reviews it — then use the pause
that follows to either approve the result or send the critique back to the architect and watch
both steps rerun automatically. That send-back loop is Flow spec §18.1's composition case: one
model's critique driving another model's revision, with you deciding each round.

Be aware of what you are in this loop: **the relay**. Every round of the exchange passes through
your hands (inspect the critique, decide, supply feedback). That is by design at this layer —
and it is exactly the manual work the M17 dialogue worker exists to absorb; see
`IMPLEMENTATION_PLAN.md`'s M17 phase plan.

Everything in §1–§6 needs live, authenticated vendor CLIs and is therefore a human-run activity
(see CLAUDE.md's "Live-vendor smoke tests" for why that never changes). §7 runs the identical
loop against stub CLIs with no vendor auth at all — it is how this walkthrough's commands were
verified, and a good first pass if you want to see the mechanics before spending real tokens.

## 1. Prerequisites

- An installed `aer` — see the root `README.md` ("Installing `aer`"). Everything below also works
  from a checkout via `pixi run` + `dotnet run --project src/Aer.Cli --`, but the installed tool
  reads better.
- An authenticated `claude` CLI on `PATH` (subscription login; no API key needed — the adapters
  shell out to whatever is already authenticated, exactly like the live smoke runbooks).
- An authenticated `agy` CLI on `PATH` (same deal, Google side).
- The two example files from `docs/walkthroughs/examples/`:
  - `design-review-workflow.json` — the workflow template
  - `design-review-bindings.json` — the worker bindings

Copy both somewhere convenient; the commands below assume they sit in your current directory.

## 2. The two files, and why there are two

**The template** (`design-review-workflow.json`) is pure structure — steps, data flow, retry
budgets, and where a human decision is required. Flow routes on nothing else (CLAUDE.md rule #1):

```json
{
  "WorkflowTemplateId": "design-review",
  "WorkflowTemplateVersion": 1,
  "Steps": [
    {
      "StepId": "architect",
      "Worker": "architect",
      "Inputs": [],
      "Outputs": ["proposal"],
      "DependsOn": [],
      "RetryPolicy": { "MaxAttempts": 1 }
    },
    {
      "StepId": "critic",
      "Worker": "critic",
      "Inputs": ["proposal"],
      "Outputs": ["critique"],
      "DependsOn": ["architect"],
      "RetryPolicy": { "MaxAttempts": 2 },
      "PausePoint": { "SupersedeTargets": ["architect"] }
    }
  ]
}
```

Field notes:

- `Inputs`/`Outputs` are *names*, not paths. Flow matches `critic`'s input `proposal` against its
  dependency's declared output of the same name and hands the worker a resolved absolute path at
  dispatch time (via `AER_INPUT_0`; outputs go under `AER_OUTPUT_DIR`). You never write a path in
  a template.
- `PausePoint` on `critic` means: after `critic` settles, stop and wait for an external decision
  (Flow spec §17.1).
- `SupersedeTargets: ["architect"]` declares — statically, in the template — the only step a
  send-back from this pause may target. If it's not declared here, no client (CLI or UI) can
  offer it (Flow spec §17.2: bounded, never invented at runtime).
- `MaxAttempts: 2` on `critic` because `agy`'s recorded failure mode (spike #21) is exiting 0
  having written nothing; Flow classifies the missing declared output as a retryable contract
  failure and retries it automatically (§8 → §10).

**The bindings** (`design-review-bindings.json`) are what make those roles *mean something* on
your machine — which vendor CLI, which model, what prompt, what the worker may do. They are
deliberately not template data: the same template runs under different bindings (swap models,
swap vendors, point a role at a stub) without touching structure. Bindings are never persisted
into a task directory; every command that needs them takes `--bindings` explicitly.

Binding notes:

- `Adapter` names who you're talking to (`claude`, `gemini`), not the binary (`gemini` invokes
  `agy`).
- The architect's `PermissionScope: "Bash,Read,Write"` matters. `Read` because a revision round
  reads the feedback file; `Bash` because of how that file's path arrives (next note). The M11
  live run recorded exactly this class of mistake: a `"Write"`-only scope made the reviewer
  unable to read its input, and the step failed (see `docs/runbooks/live-claude-smoke.md`). The
  critic omits `PermissionScope` — the Gemini adapter's default (`accept-edits` mode) plus its
  `--add-dir` grant already covers read-input/write-output.
- The architect's prompt tells the model to *check the environment variable*
  `AER_SUPPLEMENTARY_INPUT` itself. On a send-back rerun, Flow sets that variable in the worker
  process's environment (§17.2), pointing at the supplied feedback's **directory** — the
  supplementary execution's output directory, addressed like any other execution's, so the
  feedback file(s) live inside it. Neither adapter currently injects that path into the prompt
  text the way it does for declared inputs/outputs, so the prompt has to ask the model to look,
  and the model needs a shell tool to do the looking. That's a known rough edge of the relay
  loop, recorded for M17 (see §8 below).

## 3. Start the run

```bash
aer run design-review-workflow.json --bindings design-review-bindings.json
```

What happens: Flow freezes the template into an immutable snapshot inside a new task directory
(default: `.aer/design-review-workflow/` under your current directory — the workflow *file*
stem, not the template id; pass `--task-dir` to put it elsewhere), dispatches `architect` to the
real `claude` CLI, waits, validates that the declared `proposal` file actually exists, then
dispatches `critic` to `agy` with `AER_INPUT_0` pointing at the proposal. When `critic` settles,
its `PausePoint` takes effect and the command returns:

```
Workflow status: Paused
  architect: Succeeded
  critic: Paused (execution=<execution-id>, outcome=Succeeded, supersede-targets: architect)
```

That `execution=<execution-id>` value is what every decision below targets — copy it.

Two things worth knowing at this point:

- **Everything is on disk, nothing is in memory.** The task directory contains the bound
  snapshot, an append-only event log (`flow.jsonl`), and one artifact directory per attempt.
  Ctrl+C mid-run, a crash, a reboot — rerunning the same `aer run` command resumes exactly where
  the log says things stand; it never re-reads the workflow file once a snapshot is bound.
- **Read the critique before deciding:**

  ```bash
  cat .aer/design-review-workflow/artifacts/execution_<execution-id>/critique
  ```

  (Each attempt's artifacts live under `artifacts/execution_<its-execution-id>/` — the id the
  paused line printed is also the directory name. The `VERDICT` line at the bottom is advice
  *to you* — Flow never reads worker output to route, so acting on it is deliberately your
  call, not the engine's.)

## 4. Decide: approve

If the proposal survived critique:

```bash
aer decide .aer/design-review-workflow --execution <execution-id> --type resume \
    --bindings design-review-bindings.json
```

```
Workflow status: Terminal
  architect: Succeeded
  critic: Succeeded
```

Done — the proposal and its critique are your artifacts on disk.

## 5. Decide: send the critique back

The interesting path. Two commands: first put the feedback file into the task's artifact space as
a *supplementary execution* (§17.3), then record the supersede decision that carries it.

```bash
aer supply .aer/design-review-workflow --worker feedback --output revision-notes \
    --file .aer/design-review-workflow/artifacts/execution_<execution-id>/critique \
    --bindings design-review-bindings.json
```

Notes: `--worker`/`--output` name the supplementary execution and its single output — they are
*not* looked up in the bindings file (nothing named `feedback` exists there, on purpose); any
descriptive names work. `--file` here reuses the critic's own critique verbatim — equally valid
is a file of your own notes, or the critique plus your additions. The command prints:

```
Supplementary execution: <supplementary-id>
```

Now the send-back itself:

```bash
aer decide .aer/design-review-workflow --execution <execution-id> --type supersede \
    --target-step architect --supplementary <supplementary-id> \
    --bindings design-review-bindings.json
```

This one command drives the whole cascade (§17.5), live, before returning:

1. `architect` is superseded and reruns — this time `AER_SUPPLEMENTARY_INPUT` is set in its
   environment, pointing at the directory that holds your feedback file, and the prompt's
   revision branch kicks in. (The paths a worker receives are spelled from the task-dir argument
   you gave the command — run these commands from the same directory, or pass an absolute
   `--task-dir`/task-dir throughout, especially for CLIs that ignore their working directory the
   way `agy` does.)
2. The moment the new proposal lands, `critic`'s previous result is stale by construction
   (§11.3's staleness rule — nothing tells Flow to "go back"; the rerun's newer upstream
   execution does it), so `critic` reruns against the revised proposal.
3. `critic`'s `PausePoint` fires again, and you're back at §3's paused state — new execution id,
   new critique file, another round of your decision.

Loop §5 as many rounds as the work deserves; approve (§4) when it's done. There is also
`--type reject` (terminal "no", retry foreclosed) and `--type retry-with-revision` (rerun the
*paused step itself* with a supplementary file, no supersede) — same shapes, same commands.

## 6. The same thing, in the UI

`Aer.Ui` is the same engine surface with eyes. Launch it, click **Open** and pick the task
directory from §3 (or pass the path as a CLI argument):

- The **DAG view** shows the two steps with live status (the window re-polls every 2 seconds
  while anything is running); **history** lists every attempt including the superseded ones;
  **lineage** shows which execution's output fed which — the send-back rounds are all visible,
  nothing is overwritten.
- A paused step renders with **Approve** / **Reject** buttons, a revision-file box, and — because
  the template declared it — a **"Send back to architect"** button. Filling the revision-file /
  worker / output boxes and clicking send-back runs exactly §5's `supply` + `decide` pair as one
  action. Targets that aren't declared `SupersedeTargets` are never offered.
- **Run** does what §3 did (it asks for the template and bindings paths — bindings are asked for
  on every mutation, never remembered as authority), **Stop** is Ctrl+C's equivalent, and per-
  execution **Cancel** appears while something is in flight.
- The **template and bindings editors** (M16) author these files from blank with live structural
  validation — the walkthrough's two example files are buildable entirely in the UI, and the
  editor refuses to save anything the engine's own validator would reject.

## 7. Dry run without any vendor auth (stub CLIs)

The full loop — pause, supply, supersede cascade and all — with no subscription and no network,
by putting stub `claude`/`agy` scripts ahead on `PATH` (the same trick `pixi run verify-pack`'s
script uses in CI; POSIX shells — on Windows, WSL or Git Bash works):

```bash
STUB_DIR="$(mktemp -d)"

cat > "$STUB_DIR/claude" <<'STUB'
#!/bin/sh
mkdir -p "$AER_OUTPUT_DIR"
{
  echo "stub proposal (fresh)"
  if [ -n "${AER_SUPPLEMENTARY_INPUT:-}" ]; then
    echo "revised after feedback:"
    cat "$AER_SUPPLEMENTARY_INPUT"/*
  fi
} > "$AER_OUTPUT_DIR/proposal"
STUB

cat > "$STUB_DIR/agy" <<'STUB'
#!/bin/sh
mkdir -p "$AER_OUTPUT_DIR"
{
  echo "stub critique of: $(head -1 "$AER_INPUT_0")"
  echo "VERDICT: needs-revision"
} > "$AER_OUTPUT_DIR/critique"
STUB

chmod +x "$STUB_DIR/claude" "$STUB_DIR/agy"
export PATH="$STUB_DIR:$PATH"
```

Then run §3 → §5 → §4 exactly as written. After one send-back round, the architect's second
execution's `proposal` contains the `revised after feedback:` block with the critique's text
under it — proof the supplementary input actually reached the rerun — and a second critique
exists under the new paused execution's artifact directory, written against the revised
proposal. The stubs read `AER_OUTPUT_DIR`/`AER_INPUT_0`/`AER_SUPPLEMENTARY_INPUT` from the process
environment directly, which is exactly what the prompt-level instructions in the real bindings
make the live models do with their tools.

## 8. Rough edges you just felt (and where they're going)

Recorded here deliberately — this walkthrough is M17's requirements baseline, and these are the
findings:

1. **You are the relay.** Each critique round costs a human trip through §5. Bounded multi-turn
   exchange belongs inside one worker (Flow spec §18.2) — that's M17's dialogue worker (#165–#168).
2. **`AER_SUPPLEMENTARY_INPUT` isn't surfaced by the adapters.** Declared inputs/outputs get
   their paths injected into the prompt; the supplementary path doesn't — and it names a
   *directory* (the supplementary execution's output directory), not the file — so the bindings
   must prompt the model to inspect its environment and must grant a shell tool to do it (the
   architect's `Bash` scope exists only for this). An adapter-level fix has a real constraint —
   `IWorkerAdapter.Resolve` runs once per role, not once per execution, so it can't know whether
   a given dispatch carries a supplement — worth settling alongside M17 Phase 4's adapter work.
3. **The exchange has no conversation rendering.** History and lineage show the rounds; nothing
   renders them as the dialogue they are. That's M18, and it's blocked on M17's transcript.

## 9. Cleanup

A task directory is self-contained and disposable: `rm -rf .aer/design-review` and it never
existed. The template and bindings are yours to keep and rebind.
