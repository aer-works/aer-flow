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


@control("no template is refused, and the guard keeps #529's shape",
         "the guard reverts to the over-strict `not write_files` rule (F1's defect)")
def _over_strict_guard():
    # THE arm this whole file exists for. Every template grants write, so the loop over templates
    # cannot see this -- only the explicit #529 polarity can. Before that polarity was added, this
    # exact mutation left `audit-selfcheck` green.
    def over_strict(grant):
        if grant["run_shell_commands"] and not grant["network_access"]:
            return "shell without network"
        if not grant["write_files"]:
            return "nothing here can write the output"
        return None
    with swap(selfcheck.dispatch, "grant_refusal", over_strict):
        yield


@control("no template is refused, and the guard keeps #529's shape",
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
