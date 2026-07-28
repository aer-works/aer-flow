"""Prove every assertion in `selfcheck.py` discriminates, by breaking the thing it checks.

    pixi run audit-controls

A green checker means nothing on its own. Four of `selfcheck.py`'s assertions were once satisfied by
construction rather than by comparison -- one of them re-asserted a filter that had already selected
the population, and one could not fail for any input of any kind. All four printed OK. A reviewer
found them by reading; nothing in the repo could have.

So: for each registered check, inject the fault it exists to catch and require it to go RED. A check
with no control is itself a failure here, which is what stops this file from quietly falling behind
`selfcheck.py` as assertions are added.

WHY THIS IS A SEPARATE FILE, AND WHY IT IS IN THE REPO
The arms that verified the previous two rounds of fixes lived in a scratch directory, were reported
in a commit message as verification, and were preserved nowhere. That is exactly the failure
`dispatch.py`'s own header exists to stop -- "established once, in a temp directory, then thrown away
with the session" -- reproduced while fixing the file written to prevent it. The consequence was
concrete rather than theoretical: the fix for the over-strict permission guard was protected only by
a throwaway script, so the defect could be restored and `audit-selfcheck` stayed green.

It is separate from `selfcheck.py` because the two answer different questions. `selfcheck.py` asks
"is the tooling correct?" and belongs in CI's `audit` job on every PR. This asks "would we know if it
were not?" -- it mutates copies, spawns subprocesses, and is slower.

NOTHING HERE MUTATES A TRACKED FILE. Faults are injected in-process, or into a copy of the tree in a
temp directory. A control that edited `dispatch.py` in place would, if interrupted, leave behind
precisely the fault it was injecting -- a change that makes a checker pass.
"""
from __future__ import annotations

import contextlib
import io
import re
import shutil
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
import selfcheck  # noqa: E402

CONTROLS: dict[str, list] = {}
FAILURES: list[str] = []


def control(check_name: str, describe: str):
    """Register a fault for a named check. The decorated function is a context manager body."""
    def deco(fn):
        CONTROLS.setdefault(check_name, []).append((describe, contextlib.contextmanager(fn)))
        return fn
    return deco


@contextlib.contextmanager
def swap(obj, attr, value):
    """Temporarily replace an attribute, restoring it even if the check raises."""
    missing = object()
    prior = getattr(obj, attr, missing)
    setattr(obj, attr, value)
    try:
        yield
    finally:
        if prior is missing:
            delattr(obj, attr)
        else:
            setattr(obj, attr, prior)


@contextlib.contextmanager
def mutated_tree(relative: str, edit):
    """A copy of the repo's tools/ at the same depth, with one file edited. Yields the new path.

    Same depth because `dispatch.py` derives its repo root from `__file__.parents[2]` -- a copy at
    the wrong depth silently reads a different tree, which is the exact harness fault that once made
    three control arms fail identically for a reason unrelated to the injected fault.
    """
    with tempfile.TemporaryDirectory() as tmp:
        dest_root = Path(tmp) / "repo"
        shutil.copytree(ROOT / "tools", dest_root / "tools")
        shutil.copy2(ROOT / "CLAUDE.md", dest_root / "CLAUDE.md")
        target = dest_root / relative
        original = target.read_text(encoding="utf-8")
        edited = edit(original)
        assert edited != original, f"the edit to {relative} did not apply -- this arm measures nothing"
        target.write_text(edited, encoding="utf-8")
        yield target


# ---------------------------------------------------------------------------------------------
# The faults. Each is the defect its check was written for, or one the check must be able to see.
# ---------------------------------------------------------------------------------------------

# Assembled rather than written as a literal, and this is not cosmetic: step 9 of
# `audit-completeness` walks tools/ TEXTUALLY for anything in a `"model":` position, so a bad pin
# spelled out here would be read as a real pin in a real file and fail that step. The fixture would
# break the checker it is meant to exercise. Verified: with the literal in place, step 9 went red.
BAD_PIN = "gemini-3.1-" + "pro"  # a real prefix, not an accepted value -- the #547 near-miss
PLANTED_COUNT = 42               # deliberately not the number of steps in completeness.py


@control("every gemini template pins a model `agy models` lists",
         "a template pins a name the register does not list (#547's class)")
def _bad_pin():
    broken = {**selfcheck.dispatch.TEMPLATES,
              "control": {"adapter": "gemini", "model": BAD_PIN, "write_files": True}}
    with swap(selfcheck.dispatch, "TEMPLATES", broken):
        yield


@control("no template is refused, and every grant the shell would over-reach is",
         "the coherence rule is dropped, so a withheld write dispatches whenever the shell is granted")
def _no_coherence_rule():
    # The pre-#529 guard, verbatim. Every template is coherent, so the loop over templates cannot see
    # this -- only the explicit refusal arms can.
    def pre_529(grant):
        if grant["run_shell_commands"] and not grant["network_access"]:
            return "shell without network"
        if not grant["write_files"] and not grant["run_shell_commands"]:
            return "nothing here can write the output"
        return None
    with swap(selfcheck.dispatch, "grant_refusal", pre_529):
        yield


@control("no template is refused, and every grant the shell would over-reach is",
         "the coherence rule keeps writes but drops reads, which no template would notice")
def _coherence_rule_forgets_reads():
    # Reads are the arm with nothing behind them: every template either grants read or withholds the
    # shell. Written as its own arm because the check must fail on the CATEGORY being dropped, not
    # only on the rule vanishing.
    def writes_only(grant):
        if grant["run_shell_commands"] and not grant["network_access"]:
            return "shell without network"
        if grant["run_shell_commands"] and not grant["write_files"]:
            return "reaches both anyway"
        if not grant["write_files"] and not grant["run_shell_commands"]:
            return "nothing here can write the output"
        return None
    with swap(selfcheck.dispatch, "grant_refusal", writes_only):
        yield


@control("no template is refused, and every grant the shell would over-reach is",
         "the network condition is deleted, which no template and no other arm would notice")
def _no_network_arm():
    # Walked before it was written: with only the read/write coherence rule and the unsatisfiable
    # rule left, every template stays coherent, the write arms still fire on write_files, and the
    # read arm still fires on read_files. Nothing reddens -- so `--run-shell-commands` without
    # `--network-access` would dispatch a bindings.json the engine refuses at bind time, which is
    # exactly the failure moving the rule to the caller was meant to prevent.
    def no_network(grant):
        if grant["run_shell_commands"] and not (grant["read_files"] and grant["write_files"]):
            return "reaches both anyway"
        if not grant["write_files"] and not grant["run_shell_commands"]:
            return "nothing here can write the output"
        return None
    with swap(selfcheck.dispatch, "grant_refusal", no_network):
        yield


@control("no template is refused, and every grant the shell would over-reach is",
         "the rule over-corrects and refuses every grant carrying a shell")
def _refuses_all_shell():
    # The opposite defect, and the one a refusal-only check cannot see without its control arm: this
    # makes `implement` -- the only template exercising the write path -- undispatchable.
    def any_shell(grant):
        return "reaches both anyway" if grant["run_shell_commands"] else None
    with swap(selfcheck.dispatch, "grant_refusal", any_shell):
        yield


@control("no template is refused, and every grant the shell would over-reach is",
         "the guard stops refusing anything at all")
def _no_guard():
    with swap(selfcheck.dispatch, "grant_refusal", lambda grant: None):
        yield


@control("every template dry-runs clean through the real command line",
         "--dry-run stops reporting, so a real dispatch would be indistinguishable")
def _dry_run_unmarked():
    # Removes only the marker line, leaving the early return in place: the check must not rely on
    # exit 0 plus a written bindings.json, both of which a REAL dispatch also produces.
    with mutated_tree(
        "tools/aer-agy-loop/dispatch.py",
        lambda s: s.replace('print("[dispatch.py] DRY RUN -- nothing was dispatched and nothing was spent.")',
                            'pass')
    ) as path:
        with swap(selfcheck, "DISPATCH_PY", path):
            yield


@control("every template dry-runs clean through the real command line",
         "precedence stops carrying the template into the generated bindings")
def _precedence_dropped():
    with mutated_tree(
        "tools/aer-agy-loop/dispatch.py",
        lambda s: s.replace("for key, value in resolve(TEMPLATES.get(args.template, {})).items():",
                            "for key, value in resolve({}).items():")
    ) as path:
        with swap(selfcheck, "DISPATCH_PY", path):
            yield


@control("every permission boolean can be turned OFF from the command line",
         "the --no- arm is declared FIRST, so argparse takes the default from it")
def _flag_order_swapped():
    real = selfcheck.dispatch.build_parser

    def swapped(argv=None):
        parser = real(argv)
        # Re-register write_files with the negative arm first, reproducing the argparse trap: a
        # dest's default comes from the FIRST action registered for it, so this collapses the
        # tri-state to False while both flag strings remain present in the source.
        for action in parser._actions:
            if action.dest == "write_files":
                action.default = False
        return parser
    with swap(selfcheck.dispatch, "build_parser", swapped):
        yield


@control("both shapes accept known pins, and PIN_SHAPE rejects English",
         "PIN_SHAPE becomes a regex that matches NOTHING")
def _pin_shape_matches_nothing():
    # Left every assertion in selfcheck.py green while step 9's tools/ walk stopped finding any pin,
    # because both loops asserting PIN_SHAPE were tautologies over a pre-filtered population.
    with swap(selfcheck.completeness, "PIN_SHAPE", re.compile(r"(?!)")):
        yield


@control("both shapes accept known pins, and PIN_SHAPE rejects English",
         "PIN_SHAPE loosens until it matches English words")
def _pin_shape_matches_english():
    with swap(selfcheck.completeness, "PIN_SHAPE", re.compile(r"[a-z][a-z0-9.]*(?:-[a-z0-9.]+)+")):
        yield


@control("the gate-citation lint separates a slug from an ordinal",
         "the lint stops flagging anything (a numeric citation walks past it)")
def _gate_lint_blind():
    with swap(selfcheck.completeness, "gate_citation_faults", lambda files, slugs: []):
        yield


@control("the gate-citation lint separates a slug from an ordinal",
         "the lint flags everything (correct slug citations become faults)")
def _gate_lint_cries_wolf():
    # The direction that gets a lint DELETED rather than the one that lets a fault through, and the
    # one it actually shipped with: a blanket re.I made `[a-z]` match capitals, so prose about a
    # validity gate named in CamelCase was reported as citing a gate that does not exist.
    with swap(selfcheck.completeness, "gate_citation_faults",
              lambda files, slugs: [("planted.md", 1, "everything is a fault", "x", "x")]):
        yield


@control("step 9 fails CLOSED when either of its two file sources goes unreadable",
         "step 9 returns True regardless of what it can read")
def _step9_always_true():
    with swap(selfcheck.completeness, "step9_pinned_models_exist", lambda: True):
        yield


@control("no tooling file transcribes a count its own code computes",
         "a file in the population claims a step count that is wrong")
def _planted_wrong_count():
    with tempfile.TemporaryDirectory() as tmp:
        planted = Path(tmp) / "planted.py"
        # Interpolated for the same reason BAD_PIN is assembled: this file sits IN the lint's own
        # population, so a fixture spelled out as a literal is read as a real claim in a real file.
        # It was, and the lint fired on controls.py itself. Every fixture here has to be invisible to
        # the checker it feeds.
        planted.write_text(f'"""This runs all {PLANTED_COUNT} steps of the audit."""\n',
                           encoding="utf-8")
        with swap(selfcheck, "LINT_DIRS", (*selfcheck.LINT_DIRS, Path(tmp))):
            yield


@control("no tooling file transcribes a count its own code computes",
         "is_citation misclassifies, so the live quoted counts read as claims")
def _citation_always_false():
    # The lint finds no unquoted transcription today, so its only live exercise is the quoted
    # citations it skips. This is what proves that exercise is real: if `is_citation` stopped
    # recognising them they would each be read as a claim and fail their assert. Without this arm,
    # "nothing was compared" and "the comparison works" look identical from the output.
    with swap(selfcheck, "is_citation", lambda src, m: False):
        yield


@control("the two reusable instruments work on themselves",
         "code_tokens stops ignoring comments")
def _code_tokens_keeps_comments():
    with swap(selfcheck, "code_tokens", lambda t: [(0, t)]):
        yield


# The check under these two loads `recordonce.py` itself, so the fault has to be injected into what
# `load` hands back rather than onto a module attribute.
def _loading_recordonce_as(mutate):
    real = selfcheck.load

    def patched(path, name):
        mod = real(path, name)
        if path.name == "recordonce.py":
            mutate(mod)
        return mod
    return swap(selfcheck, "load", patched)


def replacing(mod, name, value):
    """setattr, but refuse to invent an attribute that is not already there.

    A mutation is only a mutation if something reads what it replaced. Renaming `prose_words` to
    `prose_runs` left `setattr(mod, "prose_words", ...)` quietly defining a function nobody calls, so
    the arm below ran an UNMUTATED checker and reported the green as evidence the check discriminates.
    `audit-controls` caught it, one layer up, which is the only reason it is not still there.

    Bare `setattr` cannot tell a rename from a working control, and neither can a reader. This can.
    """
    assert hasattr(mod, name), (
        f"control tried to replace {mod.__name__}.{name}, which does not exist -- renamed? "
        "A mutation of an attribute nothing reads is not a control.")
    setattr(mod, name, value)


MOBILE_FILTER = "the mobile job's steps live where its path filter can see them"


def _ci_workflow_where(mutate):
    """Hands the check a mutated copy of ci.yml's parsed form. The tree itself is untouched --
    `mutated_tree` copies only tools/ and CLAUDE.md, and .github/ is read at an absolute path."""
    def patched():
        ci = selfcheck.ci_workflow()
        mutate(ci)
        return ci
    return swap(selfcheck, "ci_workflow", patched)


def _mobile_filters(ci):
    step = next(s for s in ci["jobs"]["changes"]["steps"] if "with" in s)
    import yaml
    parsed = yaml.safe_load(step["with"]["filters"])
    return step, parsed


@control(MOBILE_FILTER, "the mobile steps move back inline, where a path filter cannot see them")
def _mobile_job_is_inline():
    def inline(ci):
        ci["jobs"]["mobile"].pop("uses")
        ci["jobs"]["mobile"]["steps"] = [{"run": "flutter build apk --debug"}]
    with _ci_workflow_where(inline):
        yield


@control(MOBILE_FILTER, "the filter matches every workflow file again, so any CI edit builds an APK")
def _mobile_filter_is_broad():
    def broaden(ci):
        import yaml
        step, parsed = _mobile_filters(ci)
        parsed["mobile"] = ["src/Aer.Mobile/**", ".github/workflows/**"]
        step["with"]["filters"] = yaml.safe_dump(parsed)
    with _ci_workflow_where(broaden):
        yield


@control(MOBILE_FILTER, "the filter stops naming the file the job actually runs")
def _mobile_filter_misses_its_file():
    def drop(ci):
        import yaml
        step, parsed = _mobile_filters(ci)
        parsed["mobile"] = ["src/Aer.Mobile/**"]
        step["with"]["filters"] = yaml.safe_dump(parsed)
    with _ci_workflow_where(drop):
        yield


BUDGET = "every dispatch tells the worker the budget it is actually given"


@control(BUDGET, "the preamble is dropped, so every worker is timed without being told")
def _dispatch_says_nothing():
    with swap(selfcheck.dispatch, "budget_preamble", lambda minutes, output: ""):
        yield


@control(BUDGET, "the preamble names a fixed number instead of the budget the binding carries")
def _dispatch_states_the_wrong_budget():
    # The direction a "does it mention minutes?" test cannot see. Every template but one would still
    # read correctly, and the worker that is misinformed is the one with the longest run to lose.
    with swap(selfcheck.dispatch, "budget_preamble",
              lambda minutes, output: f"BUDGET: you have 25 minutes. Write {output} early.\n\n"):
        yield


RECORDONCE = "the record-once checker fires on restated prose, not on text the register prescribes"
RECORDONCE_PIN = "the record-once checker still finds the passages it found in a real merge"


@control(RECORDONCE, "the checker stops finding anything, so every restatement ships green")
def _recordonce_blind():
    with _loading_recordonce_as(lambda m: replacing(m, "violations", lambda by_file: [])):
        yield


@control(RECORDONCE, "the checker reads code as prose, so ordinary duplicated test setup is flagged")
def _recordonce_reads_code():
    # The false-positive direction, and the one a fires-on-restatement check cannot see alone: a
    # checker that flags every shared `using var stderr = new StringWriter();` blocks real work
    # while looking exactly as healthy as one that works.
    #
    # Reads every line as prose while leaving contiguity intact -- one run per hunk, as the real
    # thing produces. Injecting both faults at once would let a contiguity regression masquerade as
    # this one, and this arm is named for exactly one of them.
    def read_everything(mod):
        replacing(mod, "prose_runs",
                  lambda path, hunks: [[w for line in hunk for w in mod.normalise(line)]
                                       for hunk in hunks])
    with _loading_recordonce_as(read_everything):
        yield


@control(RECORDONCE, "comment context is lost, so docstrings and block bodies go invisible again")
def _recordonce_reads_leaders_only():
    # The pre-#675 reader, verbatim: a leader match per line, with no notion of what a line is inside.
    # Distinct from the arm above, which is the false-positive direction -- this is the one that
    # silently NARROWS the population, and narrowing is the failure that ships green. Repinning
    # PROVEN_GROUPS on #675 was caused by one docstring this cannot see.
    leader = re.compile(r"^\s*(///|//|/\*|\*|#|--|<!--)")

    def leaders_only(mod):
        replacing(mod, "comment_text",
                  lambda lines, openers, blocks:
                      (line if leader.match(line) else None for line in lines))
    with _loading_recordonce_as(leaders_only):
        yield


@control(RECORDONCE, "an index row counts as prose, so adding a decision record fails CI")
def _recordonce_reads_index_rows():
    # Why a row is excluded at all is recorded beside `TABLE_ROW` in recordonce.py.
    with _loading_recordonce_as(lambda m: replacing(m, "TABLE_ROW", re.compile(r"(?!)"))):
        yield


@control(RECORDONCE_PIN, "the pin is emptied, so the checker can stop finding anything and stay green")
def _recordonce_pin_is_vacuous():
    with _loading_recordonce_as(lambda m: replacing(m, "PROVEN_GROUPS", ())):
        yield


def main() -> int:
    print(__doc__.strip().splitlines()[0])
    print("=" * 78)

    names = [n for n, _ in selfcheck.CHECKS]
    checks = dict(selfcheck.CHECKS)

    # A check with no control is a failure. Otherwise this file silently falls behind selfcheck.py,
    # which is the same "population that quietly shrinks" defect it exists to catch.
    uncontrolled = [n for n in names if n not in CONTROLS]
    orphans = [n for n in CONTROLS if n not in checks]

    total = 0
    for name in names:
        arms = CONTROLS.get(name, [])
        if not arms:
            continue
        print(f"\n{name}")
        # GREEN BASELINE FIRST, per arm. Without it a check that fails for an unrelated reason reads
        # as a discriminating control -- three arms were once reported as passes while every one of
        # them was failing because the harness could not read the tree at all.
        try:
            checks[name]()
        except Exception as e:  # noqa: BLE001
            FAILURES.append(f"{name}: baseline NOT green ({type(e).__name__}: {e})")
            print(f"   !! baseline is not green, so no arm below can mean anything: {e}")
            continue

        for describe, fault in arms:
            total += 1
            try:
                with fault():
                    checks[name]()
            except Exception:  # noqa: BLE001 -- any raise means the check noticed
                print(f"   OK  red under: {describe}")
            else:
                FAILURES.append(f"{name}: STAYED GREEN under {describe}")
                print(f"   !!  STAYED GREEN under: {describe}")
                print("       the check does not discriminate against the defect it names")

    print("\n" + "=" * 78)
    for name in uncontrolled:
        FAILURES.append(f"{name}: no control")
        print(f" !! no control registered for: {name}")
    for name in orphans:
        FAILURES.append(f"control for a check that no longer exists: {name}")
        print(f" !! control registered for a check that does not exist: {name}")

    if FAILURES:
        print(f"\n{len(FAILURES)} problem(s) across {total} arms over {len(names)} checks.")
        return 1
    print(f"\nAll {total} control arms discriminate, across {len(names)} checks.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())


