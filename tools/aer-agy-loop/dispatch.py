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
    pixi run aer-dispatch -- [--template <name from --list-templates>] \
        --prompt-file <path> --output-name <name> \
        --working-directory <abs path> [--adapter gemini] [--model <name>] [--effort <level>] \
        [--read-files|--no-read-files] [--write-files|--no-write-files] \
        [--run-shell-commands|--no-run-shell-commands] [--network-access|--no-network-access] \
        [--timeout-minutes 20] [--scratch-root <abs path>] [--cli-path <path to Aer.Cli.exe>] \
        [--dry-run]

Prints the produced output file's content to stdout on success -- or, under `--dry-run`, the dry-run
report instead, having dispatched nothing. On failure, prints whatever `aer run` reported plus the
raw `flow.jsonl` event log (there is usually more diagnostic detail there than in the CLI's own
terminal summary) to stderr, and exits non-zero.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
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


def refresh_published_engine(repo_root: Path) -> Path:
    """#717: the engine never runs from the repo's own bin.

    An engine running from `src/Aer.Cli/bin` holds locks on the very assemblies the repo's own
    `pixi run gates` (and any dispatched build) must overwrite — measured twice in one day in both
    directions: a worker `taskkill`ed the engine MSBuild named as its lock-holder, and the
    orchestrator's own lint failed against the engine's locks while a dispatch merely ran. Running
    a COPY severs the collision: the repo's binaries stay rebuildable while any number of engines
    run.

    Each distinct build gets its own directory, named by the newest mtime across the WHOLE source
    bin tree (a single-file gate misses a rebuild that touched only a dependency DLL), and a copier
    stages privately then commits with an atomic `os.rename` onto the versioned name. Both halves
    exist because the first draft's copy-in-place was reviewed and refuted: two simultaneous
    first-time copiers could tear the shared directory (stale exe beside fresh DLLs), and copy2's
    preserved mtimes made the torn copy read "up to date" forever after. With a rename as the
    commit point, exactly one racer publishes a complete tree; the loser discards its staging and
    uses the winner's. Engines still running from older versioned dirs hold their own files; prune
    skips whatever is locked and catches it on a later refresh.
    """
    source = _default_cli_path(repo_root)
    if not source.exists():
        # The caller reports the not-built error against the source path, same as before.
        return source

    stamp = max(p.stat().st_mtime_ns for p in source.parent.rglob("*") if p.is_file())
    engines_root = repo_root / "aer-agy-loop-scratch" / "engine"
    final = engines_root / str(stamp)
    target = final / source.name

    if not target.exists():
        engines_root.mkdir(parents=True, exist_ok=True)
        staging = engines_root / f"{stamp}.staging-{uuid.uuid4().hex[:8]}"
        shutil.copytree(source.parent, staging)
        try:
            staging.rename(final)
        except OSError:
            # The other racer committed first; its tree is complete by definition of the rename.
            shutil.rmtree(staging, ignore_errors=True)
            if not target.exists():
                raise

        # Digit-named dirs only: a concurrent racer's live `.staging-` dir must never be swept.
        # A dir still hosting a running engine refuses deletion and is retried next refresh.
        for entry in engines_root.iterdir():
            if entry.is_dir() and entry.name.isdigit() and entry != final:
                shutil.rmtree(entry, ignore_errors=True)
    return target


def provision_worktree(repo: Path, branch: str) -> Path:
    """#717's --worktree: a dispatched worker that builds or tests never works in the live repo.

    Creates (or reuses) a sibling worktree for an existing branch, then runs the two provisioning
    steps whose absence has each burned a session: `git submodule update --init` (the native
    binding is a submodule) and `pixi run build-core` (52 dispatch/e2e tests fail on the missing
    native lib and none of the failures names it). Reuse requires the worktree to already be on
    the requested branch — anything else is a wrong-repo accident, refused loudly.
    """
    # Sanitized before it becomes a path segment: a branch like `feature/foo` would otherwise
    # smuggle a separator into the name and pathlib would silently nest the worktree one level
    # down (the #723 review's finding 2).
    short = branch.split("-", 1)[0] if branch.split("-", 1)[0].isdigit() else branch[:12]
    short = "".join(c if c.isalnum() or c in "._" else "-" for c in short)
    path = repo.parent / f"{repo.name}-w{short}"

    if path.exists():
        current = subprocess.run(
            ["git", "-C", str(path), "branch", "--show-current"],
            capture_output=True, text=True, encoding="utf-8", check=True).stdout.strip()
        if current != branch:
            raise SystemExit(
                f"error: worktree {path} exists but is on {current!r}, not {branch!r} -- "
                "remove it or pick the branch it actually holds.")
        return path

    subprocess.run(["git", "-C", str(repo), "worktree", "add", str(path), branch], check=True)
    subprocess.run(["git", "-C", str(path), "submodule", "update", "--init"], check=True)
    if (path / "pixi.toml").exists():
        subprocess.run(["pixi", "run", "build-core"], cwd=str(path), check=True)
    return path


def _git_cmd(workdir: Path, *argv: str, timeout: int = 30) -> tuple[str | None, str | None]:
    """One git read against workdir. Returns (stdout, None) on success or (None, reason) on failure.

    GIT_* is scrubbed from the environment: an inherited GIT_DIR/GIT_INDEX_FILE (a git hook
    exports both) overrides `-C`'s repo discovery, and the truth block would then report some
    OTHER repository's state as this workdir's -- a wrong answer, delivered confidently, from
    the one probe whose whole job is to be trustworthy.
    """
    env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
    try:
        result = subprocess.run(
            ["git", "-C", str(workdir), *argv],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout,
            env=env)
    except OSError as exc:
        return None, f"git execution error: {exc}"
    except subprocess.TimeoutExpired:
        return None, f"git command timed out after {timeout}s"
    if result.returncode != 0:
        err = result.stderr.strip() or f"git exit code {result.returncode}"
        return None, err
    return result.stdout.strip(), None


def _git(workdir: Path, *argv: str) -> str | None:
    """One git read against the workdir, or None when it is not a git repo (or git is absent)."""
    out, _ = _git_cmd(workdir, *argv, timeout=30)
    return out


def _templates_ref() -> str:
    """The provenance string for the templates this dispatch will use (#763).

    Sha and branch are the containing repo's; the dirty marker is deliberately scoped to this
    tool's own directory — a dirty src/ elsewhere does not change which templates run, and marking
    it would teach readers to ignore the marker (the lane review's finding 2: the first docstring
    claimed the wider scope the code correctly does not have).
    """
    dispatch_dir = Path(__file__).resolve().parent
    sha, err = _git_cmd(dispatch_dir, "rev-parse", "--short", "HEAD", timeout=5)
    if err or sha is None:
        return f"unavailable ({err or 'git rev-parse HEAD failed'})"

    branch, err = _git_cmd(dispatch_dir, "branch", "--show-current", timeout=5)
    if err:
        return f"unavailable ({err})"
    if not branch:
        branch = "DETACHED"

    status, err = _git_cmd(dispatch_dir, "status", "--porcelain", ".", timeout=5)
    if err:
        return f"unavailable ({err})"

    dirty = "*" if status else ""
    return f"{branch}@{sha}{dirty}"



def _git_head(workdir: Path) -> tuple[str | None, str | None]:
    return _git_cmd(workdir, "rev-parse", "HEAD")


def _print_workspace_truth(workdir: Path, head_before: str | None, head_before_err: str | None = None) -> bool:
    """#731: what the run actually did to the workspace, computed here, never by the worker.

    A worker's summary is a self-report; every one this register was designed from had to be
    verified against the real diff by hand, and that check caught real gaps. Printed on failure
    too -- what a dead worker left uncommitted is exactly what the orchestrator needs next.
    Empty-output lines are printed as (none) rather than omitted: silence would be ambiguous
    between "clean" and "not a git repo", and only one of those is evidence. Probe failures
    render as 'truth unavailable: <why>' and cause this function to return False (#780).
    """
    print(f"\n[dispatch.py] workspace truth ({workdir}):", file=sys.stderr)
    if head_before_err or head_before is None:
        head_err = f"initial HEAD check failed ({head_before_err or 'git rev-parse HEAD failed'})"
    else:
        head_err = None

    truth_ok = True
    for label, argv in (
        # The status probe does not depend on head_before, so it still runs -- and still
        # reports -- when the HEAD check failed: what a worker left uncommitted in a repo
        # whose HEAD could not be read is recoverable diagnostic, not part of the failure.
        ("uncommitted", ("status", "--short")),
        ("commits added", ("log", "--oneline", f"{head_before}..HEAD")),
        ("diff --stat", ("diff", "--stat", f"{head_before}..HEAD")),
    ):
        if head_err is not None and label != "uncommitted":
            truth_ok = False
            print(f"  {label}: truth unavailable: {head_err}", file=sys.stderr)
            continue
        value, err = _git_cmd(workdir, *argv)
        if head_err is not None:
            truth_ok = False
        if err:
            truth_ok = False
            print(f"  {label}: truth unavailable: {err}", file=sys.stderr)
        elif value:
            # Escaped, not trusted: commit subjects are worker-authored and git does not
            # sanitize them (only filenames get C-quoted). Raw ANSI escapes or \r here could
            # repaint THIS block on a terminal -- the one report the worker must not be able
            # to spoof (the #731 review's finding 1).
            safe = "".join(
                ch if ch.isprintable() or ch in "\n\t" else f"\\x{ord(ch):02x}"
                for ch in value)
            indented = "\n".join(f"    {line}" for line in safe.splitlines())
            print(f"  {label}:\n{indented}", file=sys.stderr)
        else:
            print(f"  {label}: (none)", file=sys.stderr)

    return truth_ok


def budget_preamble(timeout_minutes: int, output_name: str) -> str:
    """What the worker is never otherwise told: how long it has, and that expiry destroys its work.

    No adapter passes the budget through. `ClaudeWorkerAdapter` passes no timeout flag at all, and
    `GeminiWorkerAdapter`'s `--print-timeout` is a backstop pushed past AER's own limit so agy does
    not expire first (#588) -- neither reaches the model. On expiry AER raises `AerTimeoutException`
    and kills the process, so a report composed in memory and written at the end is lost entirely,
    not truncated. The #666 review used 19 of its 25 minutes; there is no margin to spend on a model
    that does not know it is being timed.
    """
    return (
        f"BUDGET: you have {timeout_minutes} minutes of wall-clock time. This is a hard kill, not a "
        f"warning -- when it expires your process is terminated and anything not already on disk is "
        f"lost. Write {output_name} into AER_OUTPUT_DIR EARLY and append to it as you work, rather "
        f"than composing the whole thing and saving it at the end. Order your work so the most "
        f"important findings are written first; being cut off should cost the tail, not everything. "
        f"If you are running short, write what you have and say what you did not get to.\n\n"
    )


def shell_rules_preamble(run_shell_commands: bool) -> str:
    """The #717 rule, in every shell-granted brief — measured, not hypothetical.

    A worker whose `dotnet build` reported "locked by: Aer.Cli (18780)" ran `taskkill /F` on that
    PID and killed the engine hosting it. The gate structurally cannot stop this: it does not read
    a shell command's arguments (#659). So the rule rides in the prompt, while the structural
    defenses are the published engine copy and the worktree (both also #717) — this sentence is
    the last line, not the wall.
    """
    if not run_shell_commands:
        return ""
    return (
        "SHELL RULES: never kill, stop, or restart a process you did not start yourself -- no "
        "taskkill/kill/Stop-Process on a PID you found in an error message or lock diagnostic. If "
        "a file is locked by another process, report the lock and work around or stop; clearing "
        "it is never yours to do.\n\n"
    )


def lane_review_prompt(output_name: str) -> str:
    """Generate the prompt for the review step in --lane mode.

    Instructs an adversarial review of the branch diff (git diff main...HEAD in the working
    directory), requiring file:line evidence for findings and prose output to output_name.
    """
    return (
        f"Perform an adversarial review of the branch's diff (`git diff main...HEAD` in the working directory).\n"
        f"Identify any defects, correctness issues, unverified claims, or missing test coverage. "
        f"Every finding must include file:line evidence.\n"
        f"Write your prose report to `{output_name}` in AER_OUTPUT_DIR.\n"
    )


def build_bindings(
    worker_name: str = "worker",
    prompt_text: str = "",
    output_name: str = "",
    adapter: str = "gemini",
    working_directory: Path | None = None,
    timeout_minutes: int = 20,
    model: str | None = None,
    effort: str | None = None,
    read_files: bool = True,
    write_files: bool = True,
    run_shell_commands: bool = False,
    network_access: bool = False,
    verdict_schema: bool = False,
    steps: list[dict] | None = None,
) -> dict:
    if steps is None:
        if working_directory is None:
            working_directory = Path(".")
        steps = [{
            "step_id": worker_name,
            "worker_name": worker_name,
            "prompt_text": prompt_text,
            "output_name": output_name,
            "adapter": adapter,
            "working_directory": working_directory,
            "timeout_minutes": timeout_minutes,
            "model": model,
            "effort": effort,
            "read_files": read_files,
            "write_files": write_files,
            "run_shell_commands": run_shell_commands,
            "network_access": network_access,
            "verdict_schema": verdict_schema,
        }]

    bindings = {}
    for s in steps:
        permission_grant = {
            "ReadFiles": s["read_files"],
            "WriteFiles": s["write_files"],
            "RunShellCommands": s["run_shell_commands"],
            "NetworkAccess": s["network_access"],
        }

        produced_outputs = [{"Name": s["output_name"]}]
        if s.get("verdict_schema"):
            # Spec §4.2: the engine validates this parses as a ReviewVerdict at completion -- a review
            # whose verdict.json is missing or malformed is a FAILED execution regardless of the prose
            # report's quality. The shape instruction rides the prompt below.
            produced_outputs.append({"Name": VERDICT_OUTPUT_NAME, "Schema": "ReviewVerdict"})

        entry = {
            "Adapter": s["adapter"],
            "Contract": {
                "WorkerName": s["worker_name"],
                "RequiredInputs": [],
                "ProducedOutputs": produced_outputs,
                "OptionalMetadata": [],
            },
            "PromptTemplate": budget_preamble(s["timeout_minutes"], s["output_name"])
            + shell_rules_preamble(s["run_shell_commands"])
            + (verdict_preamble() if s.get("verdict_schema") else "")
            + s["prompt_text"],
            # Split into hours: "00:90:00" is not 90 minutes under .NET's [-][d.]hh:mm:ss, it is
            # malformed. Everything below 60 was correct, which is why the default of 20 never showed it —
            # and #588 makes a larger number the natural next thing an operator reaches for.
            "Timeout": "{:02d}:{:02d}:00".format(*divmod(s["timeout_minutes"], 60)),
            "PermissionGrant": permission_grant,
            "WorkingDirectory": _forward_slashes(s["working_directory"]),
        }
        if s.get("model"):
            entry["Model"] = s["model"]
        if s.get("effort"):
            entry["Effort"] = s["effort"]

        bindings[s["step_id"]] = entry

    return bindings


def build_workflow(
    worker_name: str = "worker",
    output_name: str = "",
    verdict_schema: bool = False,
    steps: list[dict] | None = None,
) -> dict:
    if steps is None:
        steps = [{
            "step_id": worker_name,
            "worker_name": worker_name,
            "output_name": output_name,
            "verdict_schema": verdict_schema,
            "depends_on": [],
        }]

    workflow_steps = []
    for s in steps:
        outputs = [s["output_name"]] + ([VERDICT_OUTPUT_NAME] if s.get("verdict_schema") else [])
        workflow_steps.append({
            "StepId": s["step_id"],
            "Worker": s["worker_name"],
            "Inputs": [],
            "Outputs": outputs,
            "DependsOn": s.get("depends_on", []),
            "RetryPolicy": {"MaxAttempts": 1},
        })

    return {
        "WorkflowTemplateId": f"aer-agy-loop-{uuid.uuid4().hex[:8]}",
        "WorkflowTemplateVersion": 1,
        "Steps": workflow_steps,
    }


# One name, used by the contract, the workflow, and the prompt below -- a drifted copy here would
# make the engine demand a file the prompt never asked the worker to write.
VERDICT_OUTPUT_NAME = "verdict.json"


def verdict_preamble() -> str:
    """The prompt half of spec §4.2's ReviewVerdict contract (#732).

    The canonical field-level definition is `Aer.Flow.Domain.ReviewVerdict`; this block restates
    only what the worker must type, because the worker cannot read C# xmldoc from a prompt.
    """
    return (
        f"VERDICT ARTIFACT (required): besides your report, write `{VERDICT_OUTPUT_NAME}` into "
        "AER_OUTPUT_DIR. The engine validates it against a schema at completion -- if it is "
        "missing or malformed, the run is recorded as FAILED regardless of the report's quality. "
        "Exact shape (bare JSON, no markdown fences):\n"
        '{"reviewedRef": "<branch, commit, or PR you reviewed -- required>",\n'
        ' "summary": "<one line, optional>",\n'
        ' "findings": [\n'
        '   {"severity": "high|medium|low", "claim": "<one-line statement>",\n'
        '    "status": "confirmed|refuted|unverified",\n'
        '    "anchor": {"file": "<repo-relative path>", "line": <int, optional>},\n'
        '    "detail": "<evidence and reasoning, optional>"}\n'
        " ]}\n"
        "An empty findings array is valid and means you looked and found nothing. `anchor` is "
        "optional per finding. Every finding in your prose report must appear here; the report "
        "carries your reasoning, this file carries the claims.\n\n"
    )


# Named role presets, so the tier decision is a flag rather than something the caller re-derives.
# CLAUDE.md's `second-reader` gate carries the rule for choosing one -- would a weaker model
# plausibly reach the OPPOSITE conclusion, for a reason unrelated to the thing under review? -- and
# these are the settings it resolves to. `fact-check` and `review` are separate templates rather
# than one with a knob because that question has two answers, not a dial.
#
# The reviewing templates withhold WriteFiles (#649). A worker satisfies its ProducedOutputs
# contract by writing into AER_OUTPUT_DIR, and on claude a withheld write still reaches that
# directory -- AER's PreToolUse hook confines the write tools to it rather than denying them. So a
# reviewer can produce its report without being able to edit the code it is reviewing, which is what
# every one of these grants used to require. `review` and `fact-check` pin the adapter to claude,
# which is what makes the narrowing safe; see OUTBOX_WRITE_CAPABLE_ADAPTERS and `grant_refusal()`
# for the arm-by-arm scope.
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
        #
        # Flash tier by operator directive (#742): frontier effort is an explicit --model override
        # for the pass that must notice something off-list, never the default.
        "adapter": "gemini", "model": "gemini-3.6-flash-high", "effort": None,
        "read_files": True, "write_files": True,
        "run_shell_commands": False, "network_access": False,
        "timeout_minutes": 25,
        "verdict_schema": False,
    },
    "implement": {
        "_use": "A bounded change with the approach already decided. Exercises the write path and "
                "agy's skip-permissions translation, which is the half of AER that review-only "
                "dispatches never touch. Its 40 minutes is NOT the #588 path -- every template's "
                "timeout exercises that equally -- so do not reach for the skip-permissions grant "
                "expecting it to buy that.",
        # Same tier directive as `advise` (#742).
        "adapter": "gemini", "model": "gemini-3.6-flash-high", "effort": None,
        "read_files": True, "write_files": True,
        "run_shell_commands": True, "network_access": True,
        "timeout_minutes": 40,
        "verdict_schema": False,
    },
    "review": {
        "_use": "Adversarial review of CLAIMS -- a decision record, a measured finding, anything whose "
                "rationale asserts something. The default for any PR touching src/ or making a claim "
                "in docs/.",
        # No workspace write (#649). A reviewer's deliverable is its report, which lands in
        # AER_OUTPUT_DIR — a directory a withheld write still reaches on this adapter. Until that
        # existed, every dispatch here granted the reviewer the ability to edit the very code it was
        # reviewing, purely so it could save a file.
        #
        # sonnet/high by operator directive (#742): four sonnet passes on 2026-07-30 caught
        # only-verified findings, one proven by reverting the fix under the new tests. What sonnet
        # would MISS versus a frontier pass is unmeasured (no parallel arm ran) -- escalate with an
        # explicit --model override when the review must notice something off-list.
        "adapter": "claude", "model": "sonnet", "effort": "high",
        "read_files": True, "write_files": False,
        "run_shell_commands": False, "network_access": False,
        "timeout_minutes": 25,
        # #732 / spec §4.2: reviews also produce a schema-checked verdict.json the engine validates
        # at completion, so this loop stops regex-grepping severity words out of prose reports.
        "verdict_schema": True,
    },
    "fact-check": {
        "_use": "'Confirm these specific facts against the repo.' Handed an exhaustive list, so the "
                "list determines the work and a cheap model runs it. NOT for anything where noticing "
                "something absent from the list is the point.",
        "adapter": "claude", "model": "haiku", "effort": "low",
        "read_files": True, "write_files": False,  # #649, same reason as `review` above.
        "run_shell_commands": False, "network_access": False,
        "timeout_minutes": 15,
        "verdict_schema": False,
    },
    "janitor": {
        "_use": "After an implementer commits: run the named mechanical checkers and make them green "
                "without changing behavior (#729). The canonical brief is janitor-prompt.md next to "
                "this file -- pass it via --prompt-file rather than restating the contract. The "
                "checkers determine the work, so the cheap tier runs it; anything needing judgment "
                "comes back [NOT DONE], not guessed at.",
        # The full grant is what running `pixi run ...` costs (see grant_refusal: a shell grant
        # refuses every withheld category), not a statement of trust -- the model stays the
        # cheapest one dispatchable. Same pin as verify.py's CHEAP for agy.
        "adapter": "gemini", "model": "gemini-3.6-flash-low", "effort": None,
        "read_files": True, "write_files": True,
        "run_shell_commands": True, "network_access": True,
        "timeout_minutes": 15,
        "verdict_schema": False,
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
    "verdict_schema": False,
}


def resolve(template: dict) -> dict:
    """What a bare `--template X` resolves to: its settings over the built-in defaults.

    Every template currently spells out every key in `BUILT_IN`, so nothing is filled today. Read one
    anyway: `TEMPLATES[name].get("adapter")` on a template that omits it returns None while the
    dispatch it is describing runs on gemini, which is how a model-pin check came to skip a template
    it should have validated.
    """
    return {k: template.get(k, v) for k, v in BUILT_IN.items()}


OUTBOX_WRITE_CAPABLE_ADAPTERS = frozenset({"claude"})
"""Adapters whose `IWorkerAdapter.WithheldWritesReachTheOutbox` is true (#649): a worker with the
write tools withheld can still write its declared output into AER_OUTPUT_DIR, so a contract naming
one is satisfiable without granting a workspace write.

Mirrors the C# capability rather than re-deriving it -- `Aer.Adapters` is the register, and the
adapter answers there in its own vendor's terms. Membership is the whole difference: on claude the
write tools stay pre-approved and AER's PreToolUse hook confines them to the outbox; gemini is not a
member for the reason recorded in #670.
Empty-by-default is deliberate for the same reason it is in C#: an adapter nobody has measured
against the outbox path refuses before the run is paid for, not after.
"""


def grant_refusal(grant: dict) -> str | None:
    """Why this permission grant is refused before it can spend, or None if it is dispatchable.

    One copy, called rather than restated -- a checker that restated a condition asserted a
    different rule than this enforces and printed OK.

    The conditions overlap on purpose: each names one cause and says what to do about it, so
    collapsing them into the single predicate they add up to would refuse the same grants with a
    message that no longer tells the operator which problem they have. `selfcheck.py`'s
    `_templates_are_dispatchable` asserts that sum directly.
    """
    if grant["run_shell_commands"] and not grant["network_access"]:
        # The network arm of the same #529 rule as the condition below, kept separate only because it
        # has a second reason on one vendor. THIS arm never branches on adapter -- #529 is a property
        # of the grant, not of the vendor -- so a message blaming gemini would be handed to an
        # operator dispatching to claude. (The outbox arm below does branch, on
        # OUTBOX_WRITE_CAPABLE_ADAPTERS, because #649 genuinely differs per vendor.)
        return (
            "RunShellCommands without NetworkAccess is refused: a granted shell reaches the network "
            "anyway (curl), so withholding it does not withhold it (#529), and AER refuses the same "
            "combination at bind time. On gemini it is additionally inexpressible -- "
            "--dangerously-skip-permissions is the only non-interactive shell unlock and it grants "
            "network too. Pass --network-access, or drop --run-shell-commands."
        )

    if grant["run_shell_commands"] and not (grant["read_files"] and grant["write_files"]):
        # `WorkerBindingResolver.RefuseIfShellDefeatsAWithheldCategory`'s rule, at the caller, so the
        # flags are refused before the operator commits rather than at bind time after. Network is
        # absent because the condition above already refuses shell-without-network.
        return (
            "RunShellCommands with ReadFiles or WriteFiles withheld is refused: a granted shell "
            "reaches both anyway (cat, redirection), so withholding them does not withhold them "
            "(#529). AER refuses the same combination at bind time. Grant them, making the real "
            "reach explicit, or drop --run-shell-commands."
        )

    if (not grant["write_files"] and not grant["run_shell_commands"]
            and grant.get("adapter") not in OUTBOX_WRITE_CAPABLE_ADAPTERS):
        # Kept as its own condition for its own message: a withheld write now lands here or on the
        # coherence rule above depending on the shell, and the two refusals are not the same problem.
        #
        # Scope, since only one arm is measured:
        #   * claude, write+shell withheld -> `Contract not satisfied`; with --write-files ->
        #     `Succeeded`. Same prompt, one flag changed. This is the arm the guard fires on.
        #   * claude, write withheld + shell granted -> satisfiable, measured (#529).
        #   * gemini, write withheld + shell + network granted -> SATISFIABILITY measured
        #     2026-07-27: `--no-write-files --run-shell-commands --network-access` produced the
        #     contract output and `executionSucceeded`. The MECHANISM is NOT established. AER's
        #     event model carries no tool calls at all (`FlowEvent.cs` / `CoreEvent.cs`), so the
        #     artifact cannot name the tool that wrote the file -- #638.
        #     Two explanations survive that evidence, and this comment does not choose between them:
        #       - the hook denied the write tools and the shell wrote the file (#529's substitution);
        #       - the hook never fired, so nothing was denied and agy's own write tool wrote it.
        #     The second is live rather than theoretical: see `GeminiWorkerAdapter`'s
        #     `BuildDeniedTools` paragraph AND the fail-open one after it -- the hook only withholds
        #     while it runs, and under `--dangerously-skip-permissions`, which is what this grant
        #     translates to, there is no backstop behind it. So do not read this run as evidence
        #     that the over-grant WAS taken back.
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
            "check. Pass --write-files. Granting the shell instead is no longer an escape: it "
            "defeats a withheld write (#529), which is why the coherence rule above refuses it. "
            "See #629."
        )

    return None


def build_parser(argv=None) -> argparse.ArgumentParser:
    """The command line, built rather than described, so a checker can parse a grant instead of
    grepping for one. A substring test for `"--no-write-files"` passes on a source file that
    declares the arms in the order argparse silently mis-defaults.
    """
    argv = sys.argv if argv is None else argv
    # `--list` and `--list-t` are valid abbreviations -- argparse's allow_abbrev defaults to True and
    # accepts any unambiguous prefix. A literal `"--list-templates" not in argv` test does not see
    # them, so asking for the catalogue got "the following arguments are required: --prompt-file".
    listing = any(a.startswith("--list") for a in argv)
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--lane", action="store_true",
                        help="Build and run a 3-step workflow (implement -> janitor -> review) in a single dispatch.")
    parser.add_argument("--prompt-file", default=None, type=Path, help="Path to the prompt text sent to the worker.")
    parser.add_argument("--output-name", default=None, help="Contract output name (no extension needed; matches an AER_OUTPUT_DIR file).")
    parser.add_argument("--working-directory", default=None, type=Path, help="Absolute path the dispatched worker treats as its project root.")
    parser.add_argument("--template", choices=sorted(TEMPLATES), default=None,
                        help="Role preset supplying adapter/model/effort/permissions/timeout. Explicit flags still win. See --list-templates.")
    parser.add_argument("--list-templates", action="store_true", help="Print each template, what it is for, and what it resolves to, then exit.")
    parser.add_argument("--adapter", default=None, help="Registered adapter name (default: gemini, or the template's).")
    parser.add_argument("--worker-name", default=None, help="Worker role name used in the generated workflow/bindings (default: worker).")
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
    parser.add_argument("--verdict-schema", action="store_true", default=None,
                        help="Also require a schema-checked verdict.json (spec §4.2). The review template sets this; the flag exists to add it to an ad-hoc dispatch or (--no-verdict-schema) drop it from one.")
    parser.add_argument("--no-verdict-schema", dest="verdict_schema", action="store_false")
    parser.add_argument("--timeout-minutes", type=int, default=None)
    parser.add_argument("--dry-run", action="store_true",
                        help="Resolve the template, run every guard, generate workflow/bindings, then stop without dispatching. Spends nothing.")
    parser.add_argument("--scratch-root", type=Path, default=None, help="Where to write the generated workflow/bindings/task-dir. Default: <repo>/aer-agy-loop-scratch/runs/<uuid>.")
    parser.add_argument("--cli-path", type=Path, default=None, help="Path to Aer.Cli.exe. Default: a published COPY of the repo bin (refreshed when the repo bin is newer) so the engine never holds the repo's own binaries -- #717. Passing this flag skips the copy entirely.")
    parser.add_argument("--worktree", metavar="BRANCH", default=None, help="Provision (or reuse) a sibling git worktree of --working-directory on this existing branch -- submodules initialised, native lib built -- and dispatch there instead. #717: a worker that builds or tests never works in the live repo.")
    return parser


def _print_flow_log(log_path: Path, log_bytes_before: int | None, log_mtime_before: float | None, task_dir: Path) -> None:
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


def main() -> int:
    # Windows' default console codepage (cp1252) can't represent most Unicode -- a dispatched
    # worker's own output (a box-drawing table character, an emoji, anything non-Latin-1) crashed
    # this function's own success-path print, after the workflow itself had already succeeded.
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8", errors="replace")

    args = build_parser().parse_args()

    if args.list_templates:
        # The catalogue is exactly what a stale checkout misrepresents (#763's originating
        # incident), so the listing names its own provenance too.
        print(f"templates ref: {_templates_ref()}")
        print()
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

    if args.lane:
        if args.template is not None:
            print("error: --lane cannot be combined with --template", file=sys.stderr)
            return 2
        if args.worker_name is not None:
            print("error: --lane cannot be combined with --worker-name", file=sys.stderr)
            return 2
        if args.output_name is not None:
            print("error: --lane cannot be combined with --output-name", file=sys.stderr)
            return 2

        # The #741 review's finding 2: these all default to None and are template-resolved per step
        # in lane mode, so an explicit value would be silently ignored -- and a flag that looks
        # accepted but does nothing is worse than a refusal. Refuse each by name.
        for flag_name in ("adapter", "model", "effort", "timeout_minutes", "read_files",
                          "write_files", "run_shell_commands", "network_access", "verdict_schema"):
            if getattr(args, flag_name) is not None:
                print(f"error: --lane resolves every step's settings from its template; "
                      f"an explicit --{flag_name.replace('_', '-')} would be silently ignored, so it is refused.",
                      file=sys.stderr)
                return 2
        if args.prompt_file is None:
            print("error: the following arguments are required: --prompt-file", file=sys.stderr)
            return 2
        if args.working_directory is None:
            print("error: the following arguments are required: --working-directory", file=sys.stderr)
            return 2
    else:
        if args.prompt_file is None:
            print("error: the following arguments are required: --prompt-file", file=sys.stderr)
            return 2
        if args.output_name is None:
            print("error: the following arguments are required: --output-name", file=sys.stderr)
            return 2
        if args.working_directory is None:
            print("error: the following arguments are required: --working-directory", file=sys.stderr)
            return 2
        if args.worker_name is None:
            args.worker_name = "worker"

        # Precedence: an explicit flag beats the template, the template beats the built-in default.
        for key, value in resolve(TEMPLATES.get(args.template, {})).items():
            if getattr(args, key) is None:
                setattr(args, key, value)

    repo_root = Path(__file__).resolve().parents[2]
    # A dry run keeps the plain repo-bin path: it never spawns the engine, and refreshing a copy
    # would put --dry-run out of reach of CI's `audit` job, which has no .NET and no build (#639).
    cli_path = args.cli_path if args.cli_path else (
        _default_cli_path(repo_root) if args.dry_run else refresh_published_engine(repo_root))
    if not cli_path.exists() and not args.dry_run:
        print(f"error: Aer.Cli.exe not found at {cli_path} -- build it first (pixi run build).", file=sys.stderr)
        return 2

    working_directory = args.working_directory.resolve()
    # Not under --dry-run: provisioning creates a real worktree and runs a real build, and the dry
    # run's whole promise is that nothing is mutated or spent (#639).
    if args.worktree and not args.dry_run:
        working_directory = provision_worktree(working_directory, args.worktree)
        print(f"[dispatch.py] worktree: {working_directory} (branch {args.worktree})")

    if not args.lane:
        refusal = grant_refusal(vars(args))
        if refusal:
            print(f"error: {refusal}", file=sys.stderr)
            return 2

        prompt_text = args.prompt_file.read_text(encoding="utf-8")
        step_specs = [{
            "step_id": args.worker_name,
            "worker_name": args.worker_name,
            "prompt_text": prompt_text,
            "output_name": args.output_name,
            "depends_on": [],
            "adapter": args.adapter,
            "working_directory": working_directory,
            "timeout_minutes": args.timeout_minutes,
            "model": args.model,
            "effort": args.effort,
            "read_files": args.read_files,
            "write_files": args.write_files,
            "run_shell_commands": args.run_shell_commands,
            "network_access": args.network_access,
            "verdict_schema": args.verdict_schema,
        }]
    else:
        janitor_prompt_path = Path(__file__).resolve().parent / "janitor-prompt.md"
        if not janitor_prompt_path.exists():
            print(f"error: janitor prompt file not found at {janitor_prompt_path}", file=sys.stderr)
            return 2

        implement_prompt = args.prompt_file.read_text(encoding="utf-8")
        janitor_prompt = janitor_prompt_path.read_text(encoding="utf-8")
        review_prompt = lane_review_prompt("report.md")

        step_specs = [
            {
                "step_id": "implement",
                "worker_name": "implement",
                "prompt_text": implement_prompt,
                "output_name": "implement-report.md",
                "depends_on": [],
                "working_directory": working_directory,
                **resolve(TEMPLATES["implement"]),
            },
            {
                "step_id": "janitor",
                "worker_name": "janitor",
                "prompt_text": janitor_prompt,
                # Must match the filename janitor-prompt.md itself instructs (its closing line), or
                # the one prompt carries two contradictory filenames and the contract demands the one
                # the canonical brief never mentions -- the #741 review's finding 1, found before any
                # lane was paid for.
                "output_name": "janitor.md",
                "depends_on": ["implement"],
                "working_directory": working_directory,
                **resolve(TEMPLATES["janitor"]),
            },
            {
                "step_id": "review",
                "worker_name": "review",
                "prompt_text": review_prompt,
                "output_name": "report.md",
                "depends_on": ["janitor"],
                "working_directory": working_directory,
                **resolve(TEMPLATES["review"]),
            },
        ]

        for s in step_specs:
            refusal = grant_refusal(s)
            if refusal:
                print(f"error: step '{s['step_id']}': {refusal}", file=sys.stderr)
                return 2

    run_id = uuid.uuid4().hex[:12]
    scratch_root = (args.scratch_root or (repo_root / "aer-agy-loop-scratch" / "runs" / run_id)).resolve()
    scratch_root.mkdir(parents=True, exist_ok=True)
    task_dir = scratch_root / "task-dir"

    workflow = build_workflow(steps=step_specs)
    bindings = build_bindings(steps=step_specs)

    workflow_path = scratch_root / "workflow.json"
    bindings_path = scratch_root / "bindings.json"
    workflow_path.write_text(json.dumps(workflow, indent=2), encoding="utf-8")
    bindings_path.write_text(json.dumps(bindings, indent=2), encoding="utf-8")

    if args.lane:
        print(
            "[dispatch.py] {verb}: lane 3 steps (implement -> janitor -> review)".format(
                verb="WOULD dispatch" if args.dry_run else "about to dispatch",
            ),
            file=sys.stderr,
        )
    else:
        s = step_specs[0]
        print(
            # "would dispatch" under --dry-run. The banner exists to announce a spend before it happens,
            # and a dry run has none -- so saying "about to dispatch" and then dispatching nothing would
            # make this line assert what the code does not do.
            "[dispatch.py] {verb}: adapter={adapter} model={model} effort={effort} "
            "timeout={timeout}m".format(
                verb="WOULD dispatch" if args.dry_run else "about to dispatch",
                adapter=s["adapter"],
                model=s["model"] if s["model"] else "<none pinned -- the vendor CLI's own default>",
                # Deliberately says what is SENT, not what the vendor will do with the absence. For an
                # agy template the effort already sits in the model name, and whether an unpassed
                # `--effort` then defaults, is ignored, or is overridden by the suffix is exactly the
                # unprobed interaction in #510 -- so a banner promising "the vendor CLI's own default"
                # would assert the thing nobody has measured, on the line an operator reads before spend.
                effort=s["effort"] if s["effort"] else "<no --effort flag sent>",
                timeout=s["timeout_minutes"],
            ),
            file=sys.stderr,
        )
    print(f"[dispatch.py] templates ref: {_templates_ref()}", file=sys.stderr)

    if args.dry_run:
        # Stops HERE, after the JSON is generated, not before. The three bugs this script exists to
        # stop -- an int WorkflowTemplateVersion, arrays rather than objects, an absolute task-dir --
        # all live in the build above, so a dry run that skipped it would validate the half that was
        # never the problem.
        print("[dispatch.py] DRY RUN -- nothing was dispatched and nothing was spent.")
        print(f"    workflow:   {workflow_path}")
        print(f"    bindings:   {bindings_path}")
        print(f"    task-dir:   {_forward_slashes(task_dir)}")
        print(f"    Aer.Cli:    {cli_path}"
              f"{'' if cli_path.exists() else '   <-- NOT BUILT; a real run would fail here'}")
        if not args.lane:
            print("    grant:      " + " ".join(
                f"{k}={getattr(args, k)}" for k in
                ("read_files", "write_files", "run_shell_commands", "network_access")))
        else:
            print("    lane:       implement -> janitor -> review")
        return 0

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
    head_before, head_before_err = _git_head(working_directory)

    outer_deadline_minutes = sum(s["timeout_minutes"] for s in step_specs) + 5
    outer_deadline_seconds = outer_deadline_minutes * 60

    # Popen + two-stage communicate rather than subprocess.run(timeout=...), and the difference IS
    # the fix (the lane review's HIGH, verified against CPython's subprocess source): on Windows,
    # run()'s TimeoutExpired handler kills the direct child and then calls communicate() with NO
    # timeout to collect output — and #767's measured hazard is precisely a surviving process
    # holding inherited pipe handles, which keeps that unbounded cleanup read blocked forever. The
    # second communicate here is bounded; if the pipes are still held past it, we say so and exit —
    # communicate's reader threads are daemon threads, so exiting is safe, and nothing beyond the
    # direct child is ever killed (the never-kill rule stands for the rest of the tree).
    engine = subprocess.Popen(
        [
            str(cli_path),
            "run",
            str(workflow_path),
            "--bindings",
            str(bindings_path),
            "--task-dir",
            _forward_slashes(task_dir),
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    try:
        engine_stdout, engine_stderr = engine.communicate(timeout=outer_deadline_seconds)
    except subprocess.TimeoutExpired as ex:
        print(
            f"\n[dispatch.py] ERROR: outer deadline expired after {outer_deadline_minutes}m ({outer_deadline_seconds}s). "
            f"Engine PID: {engine.pid}. Killing the engine process (only the direct child).",
            file=sys.stderr,
        )
        engine.kill()
        try:
            engine_stdout, engine_stderr = engine.communicate(timeout=15)
        except subprocess.TimeoutExpired:
            print(
                "[dispatch.py] pipes still held 15s after the kill -- some surviving process "
                "inherited the engine's output handles (#767's measured shape). Abandoning the "
                "read; output captured so far follows.",
                file=sys.stderr,
            )
            engine_stdout = ex.stdout if isinstance(ex.stdout, str) else None
            engine_stderr = ex.stderr if isinstance(ex.stderr, str) else None
        print(
            f"[dispatch.py] flow.jsonl ({log_path}) holds whatever the engine recorded prior to expiry.",
            file=sys.stderr,
        )
        if engine_stdout:
            print(engine_stdout, end="")
        # Return value deliberately unused: this path already exits 1. Passing the HEAD error
        # keeps the rendering honest -- without it a failed HEAD probe reads as a clean tree,
        # the exact #780 defect, on the one path where the tree is most likely to be mid-work.
        _print_workspace_truth(working_directory, head_before, head_before_err)
        if engine_stderr:
            print(engine_stderr, file=sys.stderr, end="")
        _print_flow_log(log_path, log_bytes_before, log_mtime_before, task_dir)
        return 1

    result = subprocess.CompletedProcess(engine.args, engine.returncode, engine_stdout, engine_stderr)

    print(result.stdout, end="")
    truth_ok = _print_workspace_truth(working_directory, head_before, head_before_err)
    if result.returncode != 0:
        print(result.stderr, file=sys.stderr, end="")
        _print_flow_log(log_path, log_bytes_before, log_mtime_before, task_dir)
        return result.returncode

    if not truth_ok:
        print("error: workspace truth could not be established", file=sys.stderr)
        return 1

    primary_output_name = "report.md" if args.lane else args.output_name
    artifacts_dir = task_dir / "artifacts"
    output_files = list(artifacts_dir.glob(f"*/{primary_output_name}")) if artifacts_dir.exists() else []
    if not output_files:
        print(f"error: workflow reported success but no '{primary_output_name}' artifact was found under {artifacts_dir}", file=sys.stderr)
        return 3

    output_content = output_files[0].read_text(encoding="utf-8")
    print(output_content)
    print(f"\n[dispatch.py] output written to: {output_files[0]}", file=sys.stderr)
    has_verdict = args.lane or args.verdict_schema
    if has_verdict:
        for verdict_path in artifacts_dir.glob(f"*/{VERDICT_OUTPUT_NAME}"):
            print(f"[dispatch.py] structured verdict: {verdict_path}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
