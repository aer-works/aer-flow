"""Dispatch a single AER workflow step to a worker and read back its output.

WHY THIS EXISTS
---------------
The cross-vendor orchestration trial for #513 hand-rolled workflow.json/bindings.json with a Node
one-liner, three separate times, and got three different bugs from it:

  * `WorkflowTemplateVersion` must be an int, not a semver string -- guessed wrong the first time.
  * `Steps[].Inputs` / `Contract.OptionalMetadata` must be JSON arrays, not objects -- guessed wrong
    the second time.
  * A relative `--task-dir` resolves against the CLI's own cwd, but `agy` runs with cwd set to
    `WorkingDirectory` (`GeminiWorkerAdapter.cs`'s own `--add-dir` comment explains why: `agy -p`
    ignores the process working directory entirely) -- so a relative task-dir and an explicit
    `WorkingDirectory` silently produce an `AER_OUTPUT_DIR` the dispatched process resolves against
    the wrong root. The run exits 0, the workflow step is reported `Failed`, and `flow.jsonl` gives
    no hint why (`FailureClassification` is null). This actually happened; see git history around
    the #513 orchestration trial.

Every one of those is exactly the ad-hoc-script failure mode `tools/vendor-verify/README.md`
describes: established once, in a temp directory, then thrown away with the session. This exists so
the next dispatch doesn't re-derive them.

WHAT THIS DOES NOT DO
----------------------
This dispatches ONE workflow step and reports back. It does not decide whether a reviewer's verdict
means "loop back to the implementer" -- that decision stays with whoever is orchestrating (a human,
or an agent reading this script's output), per this repo's own Architecture Rule 1: Flow carries
discipline, workers carry intelligence, and nothing here is Flow -- but the same discipline applies
to keeping orchestration decisions out of glue code that could quietly grow into a shadow engine.

Usage:
    pixi run aer-dispatch -- --list-templates
    pixi run aer-dispatch -- [--template advise|implement|review|fact-check] \
        --prompt-file <path> --output-name <name> \
        --working-directory <abs path> [--adapter gemini] [--model <name>] [--effort <level>] \
        [--read-files|--no-read-files] [--write-files|--no-write-files] \
        [--run-shell-commands|--no-run-shell-commands] [--network-access|--no-network-access] \
        [--timeout-minutes 20] [--scratch-root <abs path>] [--cli-path <path to Aer.Cli.exe>]

Prints the produced output file's content to stdout on success. On failure, prints whatever
`aer run` reported plus the raw `flow.jsonl` event log (there is usually more diagnostic detail
there than in the CLI's own terminal summary) to stderr, and exits non-zero.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import uuid
from pathlib import Path


def _forward_slashes(path: Path) -> str:
    # Sidesteps JSON backslash-escaping entirely rather than getting it right twice (once here,
    # once in whatever generated the path) -- Windows accepts forward slashes in any path AER's
    # own dispatch targets consume, and this is what worked when a doubled-backslash bug from a
    # Node one-liner produced literal `C:\\Users\\...` (four characters, not a real separator) as
    # WorkingDirectory during the #513 trial.
    return str(path.resolve()).replace("\\", "/")


def _default_cli_path(repo_root: Path) -> Path:
    return repo_root / "src" / "Aer.Cli" / "bin" / "Debug" / "net10.0" / "Aer.Cli.exe"


def build_bindings(
    worker_name: str,
    prompt_text: str,
    output_name: str,
    adapter: str,
    working_directory: Path,
    timeout_minutes: int,
    model: str | None,
    effort: str | None,
    read_files: bool,
    write_files: bool,
    run_shell_commands: bool,
    network_access: bool,
) -> dict:
    permission_grant = {
        "ReadFiles": read_files,
        "WriteFiles": write_files,
        "RunShellCommands": run_shell_commands,
        "NetworkAccess": network_access,
    }

    entry = {
        "Adapter": adapter,
        "Contract": {
            "WorkerName": worker_name,
            "RequiredInputs": [],
            "ProducedOutputs": [{"Name": output_name}],
            "OptionalMetadata": [],
        },
        "PromptTemplate": prompt_text,
        # Split into hours: "00:90:00" is not 90 minutes under .NET's [-][d.]hh:mm:ss, it is
        # malformed. Everything below 60 was correct, which is why the default of 20 never showed it —
        # and #588 makes a larger number the natural next thing an operator reaches for.
        "Timeout": "{:02d}:{:02d}:00".format(*divmod(timeout_minutes, 60)),
        "PermissionGrant": permission_grant,
        "WorkingDirectory": _forward_slashes(working_directory),
    }
    if model:
        entry["Model"] = model
    if effort:
        entry["Effort"] = effort

    return {worker_name: entry}


def build_workflow(worker_name: str, output_name: str) -> dict:
    return {
        "WorkflowTemplateId": f"aer-agy-loop-{uuid.uuid4().hex[:8]}",
        "WorkflowTemplateVersion": 1,
        "Steps": [
            {
                "StepId": worker_name,
                "Worker": worker_name,
                "Inputs": [],
                "Outputs": [output_name],
                "DependsOn": [],
                "RetryPolicy": {"MaxAttempts": 1},
            }
        ],
    }


# Named role presets, so the tier decision is a flag rather than something the caller re-derives.
# CLAUDE.md's `second-reader` gate carries the rule for choosing one -- would a weaker model
# plausibly reach the OPPOSITE conclusion, for a reason unrelated to the thing under review? -- and
# these are the settings it resolves to. `fact-check` and `review` are separate templates rather
# than one with a knob because that question has two answers, not a dial.
#
# Every template grants WriteFiles, the reviewing ones included. A worker satisfies its
# ProducedOutputs contract only by writing the artifact into AER_OUTPUT_DIR, and the three read-only
# templates withhold the shell as well, so anything less cannot report at all -- see the guard in
# main() for the arm-by-arm scope. #629 is AER accepting that combination rather than refusing it at
# bind time.
#
# Only `implement` differs: it adds shell + network, which is agy's `--dangerously-skip-permissions`
# translation and the path #596, #611, #623 and #624 all came from. A session that only ever
# dispatches reviews never exercises it.
TEMPLATES = {
    "advise": {
        "_use": "Open design question with real options to weigh, BEFORE building. Cross-vendor on "
                "purpose: a second opinion from the same family that wrote the code is one instrument "
                "twice.",
        # Effort is in the model name; the flag is left unset because which control wins is unprobed
        # (#510) -- see `docs/vendor-capabilities.md`'s `agy models` section. `verify.py`'s CHEAP pins
        # the same way. STEP 9 of `pixi run audit-completeness` checks these names against that
        # register, because a name the CLI will not accept is #547's failure class.
        "adapter": "gemini", "model": "gemini-3.1-pro-high", "effort": None,
        "read_files": True, "write_files": True,
        "run_shell_commands": False, "network_access": False,
        "timeout_minutes": 25,
    },
    "implement": {
        "_use": "A bounded change with the approach already decided. Exercises the write path and "
                "agy's skip-permissions translation, which is the half of AER that review-only "
                "dispatches never touch. Its 40 minutes is NOT the #588 path -- every template's "
                "timeout exercises that equally -- so do not reach for the skip-permissions grant "
                "expecting it to buy that.",
        "adapter": "gemini", "model": "gemini-3.1-pro-high", "effort": None,
        "read_files": True, "write_files": True,
        "run_shell_commands": True, "network_access": True,
        "timeout_minutes": 40,
    },
    "review": {
        "_use": "Adversarial review of CLAIMS -- a decision record, a measured finding, anything whose "
                "rationale asserts something. The default for any PR touching src/ or making a claim "
                "in docs/. This is the tier that has actually caught the defects.",
        "adapter": "claude", "model": "opus", "effort": "xhigh",
        "read_files": True, "write_files": True,
        "run_shell_commands": False, "network_access": False,
        "timeout_minutes": 25,
    },
    "fact-check": {
        "_use": "'Confirm these specific facts against the repo.' Handed an exhaustive list, so the "
                "list determines the work and a cheap model runs it. NOT for anything where noticing "
                "something absent from the list is the point.",
        "adapter": "claude", "model": "haiku", "effort": "low",
        "read_files": True, "write_files": True,
        "run_shell_commands": False, "network_access": False,
        "timeout_minutes": 15,
    },
}

# Below the gate's own floor -- a typo, a version bump, a comment fix asserting nothing -- dispatch
# NOTHING. There is deliberately no template for that case: running a cheap reviewer out of habit is
# the ceremony the gates exist to cut, and a template would make it look sanctioned.

# Precedence: an explicit flag beats the template, the template beats these. The tri-state argparse
# defaults (None rather than True/False) are what make "was this passed?" answerable at all -- with
# `default=True` a template could never turn a permission OFF, which is exactly the direction that
# matters for a permission grant.
BUILT_IN = {
    "adapter": "gemini", "model": None, "effort": None,
    "read_files": True, "write_files": True,
    "run_shell_commands": False, "network_access": False,
    "timeout_minutes": 20,
}


def resolve(template: dict) -> dict:
    """What a bare `--template X` resolves to: its settings over the built-in defaults.

    Every template currently spells out all eight keys, so nothing is filled today. Read one anyway:
    `TEMPLATES[name].get("adapter")` on a template that omits it returns None while the dispatch it
    is describing runs on gemini, which is how a model-pin check came to skip a template it should
    have validated.
    """
    return {k: template.get(k, v) for k, v in BUILT_IN.items()}


def grant_refusal(grant: dict) -> str | None:
    """Why this permission grant is refused before it can spend, or None if it is dispatchable.

    One copy, called rather than restated. A checker restating the second condition as `write_files
    is True` asserted a stricter rule than this enforces, in a message contradicting what #529
    measured -- and printed OK.
    """
    if grant["run_shell_commands"] and not grant["network_access"]:
        return (
            "RunShellCommands without NetworkAccess is not honorable by the gemini adapter as of "
            "this writing (--dangerously-skip-permissions is the only non-interactive shell unlock, "
            "and it unlocks network too) -- the adapter refuses this combination rather than "
            "over-granting. Pass --network-access too, or drop --run-shell-commands."
        )

    if not grant["write_files"] and not grant["run_shell_commands"]:
        # BOTH conditions. Refusing on `not write_files` alone was wrong: #529 measured, on claude,
        # that a withheld-write grant with the shell granted produced the file anyway via Bash. So
        # that combination is satisfiable and is allowed through.
        #
        # Scope, since only one arm is measured:
        #   * claude, write+shell withheld -> `Contract not satisfied`; with --write-files ->
        #     `Succeeded`. Same prompt, one flag changed. This is the arm the guard fires on.
        #   * claude, write withheld + shell granted -> satisfiable, measured (#529).
        #   * gemini, write withheld + shell + network granted -> SATISFIABILITY measured
        #     2026-07-27: `--no-write-files --run-shell-commands --network-access` produced the
        #     contract output and `executionSucceeded`. The MECHANISM is still inferred, not
        #     observed -- `flow.jsonl` records execution lifecycle only, no tool calls, so which
        #     tool wrote the file is not in the artifact. Do not read
        #     `--dangerously-skip-permissions` as handing the writes over: GeminiWorkerAdapter's
        #     fourth doc paragraph retracts that for AER's path -- the PreToolUse hook derives its
        #     deny list from all four grant categories (`DeniedToolsVariable`) and takes the
        #     over-grant back, measured by `agy.hook-deny-honoured`. What makes it satisfiable is
        #     that a granted shell is absent from that deny list, which is #529's substitution
        #     argument. That the log cannot distinguish the two is #638.
        #   * gemini, write + shell both withheld -> still INFERRED. This is the arm the guard
        #     fires on, so it is the one a run cannot reach.
        #
        # This bit the review dispatch for the change that added these templates: a 9-minute opus run
        # produced nothing. AER accepting the unsatisfiable combination rather than refusing it at
        # bind time is #629; that shell defeats a withheld write at all is #529.
        return (
            "nothing here can write the output. A worker satisfies its ProducedOutputs contract by "
            "writing the artifact into AER_OUTPUT_DIR, and this grant withholds both the write tools "
            "and the shell -- so the run would burn its full budget and then fail the contract "
            "check. Pass --write-files, or --run-shell-commands --network-access (both -- shell "
            "alone is refused above), which #529 measured as defeating a withheld write anyway. "
            "See #629."
        )

    return None


def build_parser(argv=None) -> argparse.ArgumentParser:
    """The command line, built rather than described, so a checker can parse a grant instead of
    grepping for one. A substring test for `"--no-write-files"` passes on a source file that
    declares the arms in the order argparse silently mis-defaults.
    """
    argv = sys.argv if argv is None else argv
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--prompt-file", required=("--list-templates" not in argv), type=Path, help="Path to the prompt text sent to the worker.")
    parser.add_argument("--output-name", required=("--list-templates" not in argv), help="Contract output name (no extension needed; matches an AER_OUTPUT_DIR file).")
    parser.add_argument("--working-directory", required=("--list-templates" not in argv), type=Path, help="Absolute path the dispatched worker treats as its project root.")
    parser.add_argument("--template", choices=sorted(TEMPLATES), default=None,
                        help="Role preset supplying adapter/model/effort/permissions/timeout. Explicit flags still win. See --list-templates.")
    parser.add_argument("--list-templates", action="store_true", help="Print each template, what it is for, and what it resolves to, then exit.")
    parser.add_argument("--adapter", default=None, help="Registered adapter name (default: gemini, or the template's).")
    parser.add_argument("--worker-name", default="worker", help="Worker role name used in the generated workflow/bindings (default: worker).")
    parser.add_argument("--model", default=None, help="Pin a specific model (e.g. a Gemini thinking-tier model). Omit and no --model flag is sent at all, leaving the vendor CLI's own default in effect -- AER pins nothing.")
    parser.add_argument("--effort", default=None, help="Raw vendor-native effort-level string (e.g. claude: low|medium|high|xhigh|max, agy: low|medium|high). Passed through as-is, no validation.")
    parser.add_argument("--read-files", action="store_true", default=None)
    parser.add_argument("--no-read-files", dest="read_files", action="store_false")
    parser.add_argument("--write-files", action="store_true", default=None)
    parser.add_argument("--no-write-files", dest="write_files", action="store_false")
    parser.add_argument("--run-shell-commands", action="store_true", default=None)
    # The `--no-` arms are what let a template be overridden DOWNWARD -- without them `--template
    # implement` is a lock on the two flags that resolve to `--dangerously-skip-permissions`.
    # Declaration order matters: argparse takes a dest's default from the FIRST action registered
    # for it, so the positive arm (default=None) must stay first or the tri-state below breaks.
    parser.add_argument("--no-run-shell-commands", dest="run_shell_commands", action="store_false")
    parser.add_argument("--network-access", action="store_true", default=None)
    parser.add_argument("--no-network-access", dest="network_access", action="store_false")
    parser.add_argument("--timeout-minutes", type=int, default=None)
    parser.add_argument("--scratch-root", type=Path, default=None, help="Where to write the generated workflow/bindings/task-dir. Default: <repo>/aer-agy-loop-scratch/runs/<uuid>.")
    parser.add_argument("--cli-path", type=Path, default=None, help="Path to Aer.Cli.exe. Default: <repo>/src/Aer.Cli/bin/Debug/net10.0/Aer.Cli.exe.")
    return parser


def main() -> int:
    # Windows' default console codepage (cp1252) can't represent most Unicode -- a dispatched
    # worker's own output (a box-drawing table character, an emoji, anything non-Latin-1) crashed
    # this function's own success-path print, after the workflow itself had already succeeded.
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8", errors="replace")

    args = build_parser().parse_args()

    if args.list_templates:
        for name in sorted(TEMPLATES):
            t = TEMPLATES[name]
            # A bare `None` reads as "nobody thought about effort" rather than "deliberately not
            # sent" (#510), so say which it is.
            settings = " ".join(
                f"{k}=" + ("<unset -- deliberately not sent; see #510>" if v is None else str(v))
                for k, v in t.items() if not k.startswith("_"))
            print(name)
            print(f"    {t['_use']}")
            print(f"    {settings}")
            print()
        print("Below the gate's floor -- a typo, a version bump, a comment fix asserting nothing --")
        print("dispatch nothing. There is no template for that, deliberately.")
        return 0

    # Precedence: an explicit flag beats the template, the template beats the built-in default.
    for key, value in resolve(TEMPLATES.get(args.template, {})).items():
        if getattr(args, key) is None:
            setattr(args, key, value)

    repo_root = Path(__file__).resolve().parents[2]
    cli_path = args.cli_path or _default_cli_path(repo_root)
    if not cli_path.exists():
        print(f"error: Aer.Cli.exe not found at {cli_path} -- build it first (pixi run build).", file=sys.stderr)
        return 2

    refusal = grant_refusal(vars(args))
    if refusal:
        print(f"error: {refusal}", file=sys.stderr)
        return 2

    run_id = uuid.uuid4().hex[:12]
    scratch_root = (args.scratch_root or (repo_root / "aer-agy-loop-scratch" / "runs" / run_id)).resolve()
    scratch_root.mkdir(parents=True, exist_ok=True)
    task_dir = scratch_root / "task-dir"

    prompt_text = args.prompt_file.read_text(encoding="utf-8")
    working_directory = args.working_directory.resolve()

    workflow = build_workflow(args.worker_name, args.output_name)
    bindings = build_bindings(
        worker_name=args.worker_name,
        prompt_text=prompt_text,
        output_name=args.output_name,
        adapter=args.adapter,
        working_directory=working_directory,
        timeout_minutes=args.timeout_minutes,
        model=args.model,
        effort=args.effort,
        read_files=args.read_files,
        write_files=args.write_files,
        run_shell_commands=args.run_shell_commands,
        network_access=args.network_access,
    )

    workflow_path = scratch_root / "workflow.json"
    bindings_path = scratch_root / "bindings.json"
    workflow_path.write_text(json.dumps(workflow, indent=2), encoding="utf-8")
    bindings_path.write_text(json.dumps(bindings, indent=2), encoding="utf-8")

    # CLAUDE.md gate `second-reader` ("name the model; don't inherit it") and the cost-and-reversibility policy ("say what it spends before
    # spending it") are both prose an agent has to remember at the moment it dispatches, which is
    # exactly when it is thinking about something else. Announcing the tier here makes the tool say it
    # instead. This fires for every dispatch, not only a reviewer one -- an implementer or an advisor
    # consult is spent from the same budget.
    #
    # An omitted --model is named rather than left blank, because a blank field reads like a choice.
    # It resolves to the *vendor* CLI's own default: with no Model in the bindings the adapter adds no
    # --model flag at all (GeminiWorkerAdapter's `if (invocation.Model is not null)`), so nothing on
    # AER's side picked it. Saying that precisely matters here -- `gemini-3-flash` once sat in fixtures
    # and runbooks pinning nothing while the repo read as though a model had been chosen.
    print(
        "[dispatch.py] about to dispatch: adapter={adapter} model={model} effort={effort} "
        "timeout={timeout}m".format(
            adapter=args.adapter,
            model=args.model if args.model else "<none pinned -- the vendor CLI's own default>",
            # Deliberately says what is SENT, not what the vendor will do with the absence. For an
            # agy template the effort already sits in the model name, and whether an unpassed
            # `--effort` then defaults, is ignored, or is overridden by the suffix is exactly the
            # unprobed interaction in #510 -- so a banner promising "the vendor CLI's own default"
            # would assert the thing nobody has measured, on the line an operator reads before spend.
            effort=args.effort if args.effort else "<no --effort flag sent>",
            timeout=args.timeout_minutes,
        ),
        file=sys.stderr,
    )

    # Captured BEFORE the run. A reused --scratch-root carries a previous dispatch's log, and
    # printing it on failure hands over another run's PID and exit reason as this run's diagnostics
    # -- which reads as "AER ran the wrong workflow" rather than "AER wrote nothing".
    log_path = task_dir / "flow.jsonl"
    # Both the mtime AND the byte length. flow.jsonl is APPEND-only -- `FlowEventLogWriter` appends
    # lines and nothing truncates (the daemon has to DELETE the file to reset a task directory) -- so
    # an mtime check alone only catches the zero-event case. If `aer run` writes even one event into a
    # reused task-dir, the mtime moves, and printing the file hands over BOTH runs' events
    # interleaved, with the prior run's PID and exit reason reading as this run's. The length lets the
    # stale prefix be sliced off and labelled instead of silently prepended.
    log_bytes_before = log_path.stat().st_size if log_path.exists() else None
    log_mtime_before = log_path.stat().st_mtime if log_path.exists() else None

    result = subprocess.run(
        [
            str(cli_path),
            "run",
            str(workflow_path),
            "--bindings",
            str(bindings_path),
            "--task-dir",
            _forward_slashes(task_dir),
        ],
        capture_output=True,
        text=True,
    )

    print(result.stdout, end="")
    if result.returncode != 0:
        print(result.stderr, file=sys.stderr, end="")
        print(f"\n--- flow.jsonl ({log_path}) ---", file=sys.stderr)
        if not log_path.exists():
            print("(not written -- `aer run` failed before recording anything)", file=sys.stderr)
        elif log_bytes_before and log_path.stat().st_size > log_bytes_before:
            # Grew: this run wrote something, but the file still opens with another run's events.
            # Show only the bytes this run appended, and say what was withheld and why.
            with open(log_path, encoding="utf-8") as fh:
                fh.seek(log_bytes_before)
                fresh = fh.read()
            print(f"(the first {log_bytes_before} bytes belong to an EARLIER run in this reused"
                  " task-dir and are withheld -- flow.jsonl is append-only, so they would read as"
                  " this run's events. Only what this run appended is shown.)", file=sys.stderr)
            print(fresh, file=sys.stderr)
        elif log_mtime_before is not None and log_path.stat().st_mtime == log_mtime_before:
            # Untouched by this run. Say that instead of the contents: a stale log is worse than no
            # log, because it looks like evidence.
            print("(NOT THIS RUN -- this log predates the dispatch and was not touched by it.", file=sys.stderr)
            print(" `aer run` failed before writing any event, so there are no diagnostics for this", file=sys.stderr)
            print(" run. The stale contents are withheld deliberately; they describe other work.", file=sys.stderr)
            print(f" Cause is almost always a reused --scratch-root: {task_dir} already existed.", file=sys.stderr)
            print(" Omit --scratch-root to get a fresh runs/<uuid> directory.)", file=sys.stderr)
        else:
            print(log_path.read_text(encoding="utf-8"), file=sys.stderr)
        return result.returncode

    artifacts_dir = task_dir / "artifacts"
    output_files = list(artifacts_dir.glob(f"*/{args.output_name}")) if artifacts_dir.exists() else []
    if not output_files:
        print(f"error: workflow reported success but no '{args.output_name}' artifact was found under {artifacts_dir}", file=sys.stderr)
        return 3

    output_content = output_files[0].read_text(encoding="utf-8")
    print(output_content)
    print(f"\n[dispatch.py] output written to: {output_files[0]}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
