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
    pixi run aer-dispatch -- --prompt-file <path> --output-name <name> \
        --working-directory <abs path> [--adapter gemini] [--model <name>] [--effort <level>] \
        [--read-files] [--write-files] [--run-shell-commands] [--network-access] \
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


def main() -> int:
    # Windows' default console codepage (cp1252) can't represent most Unicode -- a dispatched
    # worker's own output (a box-drawing table character, an emoji, anything non-Latin-1) crashed
    # this function's own success-path print, after the workflow itself had already succeeded.
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8", errors="replace")

    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--prompt-file", required=True, type=Path, help="Path to the prompt text sent to the worker.")
    parser.add_argument("--output-name", required=True, help="Contract output name (no extension needed; matches an AER_OUTPUT_DIR file).")
    parser.add_argument("--working-directory", required=True, type=Path, help="Absolute path the dispatched worker treats as its project root.")
    parser.add_argument("--adapter", default="gemini", help="Registered adapter name (default: gemini).")
    parser.add_argument("--worker-name", default="worker", help="Worker role name used in the generated workflow/bindings (default: worker).")
    parser.add_argument("--model", default=None, help="Pin a specific model (e.g. a Gemini thinking-tier model). Omit to use the CLI's configured default.")
    parser.add_argument("--effort", default=None, help="Raw vendor-native effort-level string (e.g. claude: low|medium|high|xhigh|max, agy: low|medium|high). Passed through as-is, no validation.")
    parser.add_argument("--read-files", action="store_true", default=True)
    parser.add_argument("--no-read-files", dest="read_files", action="store_false")
    parser.add_argument("--write-files", action="store_true", default=True)
    parser.add_argument("--no-write-files", dest="write_files", action="store_false")
    parser.add_argument("--run-shell-commands", action="store_true", default=False)
    parser.add_argument("--network-access", action="store_true", default=False)
    parser.add_argument("--timeout-minutes", type=int, default=20)
    parser.add_argument("--scratch-root", type=Path, default=None, help="Where to write the generated workflow/bindings/task-dir. Default: <repo>/aer-agy-loop-scratch/runs/<uuid>.")
    parser.add_argument("--cli-path", type=Path, default=None, help="Path to Aer.Cli.exe. Default: <repo>/src/Aer.Cli/bin/Debug/net10.0/Aer.Cli.exe.")
    args = parser.parse_args()

    repo_root = Path(__file__).resolve().parents[2]
    cli_path = args.cli_path or _default_cli_path(repo_root)
    if not cli_path.exists():
        print(f"error: Aer.Cli.exe not found at {cli_path} -- build it first (pixi run build).", file=sys.stderr)
        return 2

    if args.run_shell_commands and not args.network_access:
        print(
            "error: RunShellCommands without NetworkAccess is not honorable by the gemini adapter "
            "as of this writing (--dangerously-skip-permissions is the only non-interactive shell "
            "unlock, and it unlocks network too) -- the adapter refuses this combination rather "
            "than over-granting. Pass --network-access too, or drop --run-shell-commands.",
            file=sys.stderr,
        )
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

    # CLAUDE.md gate 7 ("name the model; don't inherit it") and gate 6 ("say what it spends before
    # spending it") are both prose an agent has to remember at the moment it dispatches, which is
    # exactly when it is thinking about something else. Announcing the tier here makes the tool say it
    # instead. An omitted --model is called out rather than left blank: silently inheriting the CLI's
    # configured default is the specific failure gate 7 names, and a blank field reads like a choice.
    print(
        "[dispatch.py] about to spend: adapter={adapter} model={model} effort={effort} "
        "timeout={timeout}m".format(
            adapter=args.adapter,
            model=args.model if args.model else "<inherited from the CLI's configured default>",
            effort=args.effort if args.effort else "<vendor default>",
            timeout=args.timeout_minutes,
        ),
        file=sys.stderr,
    )

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
        print(f"\n--- flow.jsonl ({task_dir / 'flow.jsonl'}) ---", file=sys.stderr)
        log_path = task_dir / "flow.jsonl"
        if log_path.exists():
            print(log_path.read_text(encoding="utf-8"), file=sys.stderr)
        else:
            print("(not written)", file=sys.stderr)
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
