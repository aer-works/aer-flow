"""Assert the tooling's own enumerable surfaces, because the checkers had no checker.

Every assertion here maps to a defect that actually shipped into a draft of #627 and was caught by a
reviewer or by hand. The surfaces are enumerable -- templates x settings, booleans x flag directions,
a regex x input classes -- which is the criterion CLAUDE.md gate `record-once` names for when
something earns a checker. That criterion had been applied to docs/decisions/ and vendor-verify and
never to the tooling being written.

Runs in CI's `audit` job alongside `completeness.py`. Plain asserts, no test framework: this repo's
python tooling has none, and adding one for six assertions is the ceremony the gates exist to cut.

    pixi run audit-selfcheck

WHAT THIS CANNOT CHECK
  * That a check's population is the RIGHT population. It asserts the join holds, never that the
    join is the one worth making.
  * Anything about prose. The defect class that dominated #627 -- a comment asserting what the code
    does not do -- is not reachable from here, and #631 is the other half of that answer.
"""
from __future__ import annotations

import contextlib
import importlib.util
import io
import re
import sys
import tokenize
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FAILURES: list[str] = []


def check(name):
    """Register a named assertion. A raised AssertionError is a finding, not a crash."""
    def deco(fn):
        try:
            fn()
        except AssertionError as e:
            FAILURES.append(f"{name}: {e}")
            print(f" !! {name}\n      {e}")
        else:
            print(f" OK {name}")
        return fn
    return deco


def load(path: Path, name: str):
    """Import a tool by path without leaving a __pycache__ behind."""
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    prior, sys.dont_write_bytecode = sys.dont_write_bytecode, True
    try:
        spec.loader.exec_module(mod)
    finally:
        sys.dont_write_bytecode = prior
    return mod


dispatch = load(ROOT / "tools" / "aer-agy-loop" / "dispatch.py", "_selfcheck_dispatch")
completeness = load(ROOT / "tools" / "audit-completeness" / "completeness.py", "_selfcheck_audit")
DISPATCH_SRC = (ROOT / "tools" / "aer-agy-loop" / "dispatch.py").read_text(encoding="utf-8")


# ---------------------------------------------------------------------------------------------
# Two reusable instruments. Both caught a real error today, and both were written after the error.
# ---------------------------------------------------------------------------------------------

def code_tokens(text: str):
    """Every token except comments and docstrings -- the file's code, ignoring its prose.

    Turns "this commit only touches comments" from a characterisation into an assertion. It was
    written after a commit was described as prose-only while it had changed two user-visible string
    literals; running this is what caught that.
    """
    out = []
    for tok in tokenize.generate_tokens(io.StringIO(text).readline):
        if tok.type in (tokenize.COMMENT, tokenize.NL):
            continue
        if tok.type == tokenize.STRING and tok.string.lstrip("rbuf").startswith(('"""', "'''")):
            continue
        if tok.string.strip():
            out.append((tok.type, tok.string))
    return out


def control_arm(baseline, mutate, restore, describe=""):
    """Run a discriminating control, refusing to report unless the baseline is green FIRST.

    `baseline()` must return True for an unmutated tree. If it does not, the harness is broken and
    the mutated result means nothing -- so this raises instead of reporting a pass. That is the
    failure it exists to prevent: a control run was once reported as three passes while the
    comparison copy was reading an empty string for every file, so every arm failed identically for
    a reason that had nothing to do with the injected fault.

    Returns the mutated-tree result, having proved the instrument works.
    """
    assert baseline() is True, (
        f"control baseline is NOT green{' for ' + describe if describe else ''}, so nothing measured "
        "after this would mean anything. Two causes, and they need different fixes: either the "
        "harness is broken (the failure this exists to catch), or the tree ALREADY fails the check "
        "being controlled -- in which case fix that first and this arm will speak again. Check the "
        "other assertions above: if one of them is also red, it is the second case."
    )
    mutate()
    try:
        return baseline()
    finally:
        restore()


# ---------------------------------------------------------------------------------------------
# The enumerable surfaces
# ---------------------------------------------------------------------------------------------

def register_models() -> set[str]:
    """The agy catalogue, parsed the way step 9 parses it, so both read one register."""
    caps = (ROOT / "docs" / "vendor-capabilities.md").read_text(encoding="utf-8")
    section = re.search(r"##\s+`agy models`[^\n]*\n(.*?)(?=\n##\s|\Z)", caps, re.S)
    assert section, "the `agy models` section is missing from docs/vendor-capabilities.md"
    fence = re.search(r"```[a-zA-Z]*\n(.*?)```", section.group(1), re.S)
    assert fence, "the `agy models` section carries no fenced block"
    return set(fence.group(1).split())


@check("every gemini template pins a model `agy models` lists")
def _pins_resolve():
    accepted = register_models()
    for name, tpl in dispatch.TEMPLATES.items():
        if tpl.get("adapter") != "gemini" or not tpl.get("model"):
            continue
        assert tpl["model"] in accepted, (
            f"TEMPLATES[{name!r}] pins {tpl['model']!r}, which `agy models` does not list. "
            f"A pin the CLI rejects fails AFTER the operator has paid for the run (#547). "
            f"Accepted: {sorted(accepted)}"
        )


@check("every template grants write, or it cannot satisfy any contract")
def _templates_can_report():
    for name, tpl in dispatch.TEMPLATES.items():
        assert tpl.get("write_files") is True, (
            f"TEMPLATES[{name!r}] does not grant write_files. A worker satisfies its ProducedOutputs "
            "contract only by writing into AER_OUTPUT_DIR, so this template cannot report at all -- "
            "it runs to completion, exits 0, and fails the contract check with the run paid for. "
            "Three templates shipped this way and one wasted a 9-minute opus run proving it (#629)."
        )


@check("every permission boolean can be turned OFF from the command line")
def _both_flag_directions():
    # The population comes from the templates rather than a hand-written list, so a fifth permission
    # is covered the day it is added.
    booleans = sorted({k for tpl in dispatch.TEMPLATES.values()
                       for k, v in tpl.items() if isinstance(v, bool)})
    assert booleans, "no boolean permissions found in TEMPLATES -- the population is empty"
    for key in booleans:
        flag = "--" + key.replace("_", "-")
        assert f'"{flag}"' in DISPATCH_SRC, f"{key} has no {flag} flag"
        assert f'"--no-{key.replace("_", "-")}"' in DISPATCH_SRC, (
            f"{key} has no --no-{key.replace('_', '-')} arm, so a template cannot be overridden "
            "downward on it. That made `--template implement` a lock on exactly the two flags that "
            "resolve to --dangerously-skip-permissions."
        )


@check("PIN_SHAPE rejects English, TOKEN_SHAPE accepts the whole catalogue")
def _shapes_discriminate():
    # PIN_SHAPE guards the tools/ walk, where `--model` appears in prose and every following word is
    # a candidate. TOKEN_SHAPE guards the register's own fence, where requiring a digit would be
    # inverted -- a digit-free catalogue entry would be reported as a bad parse.
    for word in ("read-only", "fail-closed", "cross-vendor", "skip-permissions"):
        assert not completeness.PIN_SHAPE.fullmatch(word), (
            f"PIN_SHAPE matches {word!r}, an English word. It is everywhere in this repo, and a "
            "match makes the walk report it as an invalid model pin."
        )
    for model in register_models():
        assert completeness.PIN_SHAPE.fullmatch(model), \
            f"PIN_SHAPE rejects {model!r}, which `agy models` lists -- the walk would skip a real pin"
        assert completeness.TOKEN_SHAPE.fullmatch(model), \
            f"TOKEN_SHAPE rejects {model!r} -- step 9 would call a correct parse a bad one"


@check("step 9 fails CLOSED when it can no longer see its sources")
def _step9_fails_closed():
    # Monkeypatched rather than mutating the tree: a test that renames files leaves the repo broken
    # if it is interrupted, and completeness.py derives ROOT from __file__ so it cannot simply be
    # relocated -- that is what silently broke a control run into reading empty strings.
    # step 9 prints its full report on every call and this invokes it five times; the assertions
    # read its return value, so the output is noise here.
    def baseline():
        with contextlib.redirect_stdout(io.StringIO()):
            return completeness.step9_pinned_models_exist() is True

    # (a) the register goes missing
    real_read = completeness.read
    result = control_arm(
        baseline,
        lambda: setattr(completeness, "read",
                        lambda p: "" if "vendor-capabilities" in p else real_read(p)),
        lambda: setattr(completeness, "read", real_read),
        describe="step9 with the register unreadable")
    assert result is False, "step 9 passed with docs/vendor-capabilities.md unreadable"

    # (b) verify.py's CHEAP arm stops yielding a pin
    result = control_arm(
        baseline,
        lambda: setattr(completeness, "read",
                        lambda p: "" if "verify.py" in p else real_read(p)),
        lambda: setattr(completeness, "read", real_read),
        describe="step9 with CHEAP unreadable")
    assert result is False, (
        "step 9 passed while verify.py yielded no pin -- a population that silently shrinks is how a "
        "check keeps printing OK about less and less"
    )


@check("no doc transcribes a count the code computes")
def _no_transcribed_counts():
    # `record-once`: never transcribe a value that lives somewhere authoritative. Both instances were
    # real -- a docstring said "eight steps" while main() ran nine, and a comment said "(today: 12)"
    # where the register's fence holds 11.
    src = (ROOT / "tools" / "audit-completeness" / "completeness.py").read_text(encoding="utf-8")
    steps = len(re.findall(r"^def step\d", src, re.M))
    words = {"one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7,
             "eight": 8, "nine": 9, "ten": 10, "eleven": 11, "twelve": 12}
    for m in re.finditer(r"\b([a-z]+|\d+)\s+steps\b", src, re.I):
        tok = m.group(1).lower()
        claimed = words.get(tok, int(tok) if tok.isdigit() else None)
        if claimed is None:
            continue
        assert claimed == steps, (
            f"a doc claims {claimed} steps; {steps} step functions are defined. Cite the code or "
            "drop the number -- this exact sentence stood at 'eight' while main() ran nine."
        )
    fence_count = len(register_models())
    for m in re.finditer(r"today:\s*(\d+)", src):
        assert int(m.group(1)) == fence_count, (
            f"a comment says 'today: {m.group(1)}' where the register's fence holds {fence_count}"
        )


@check("the two reusable instruments work on themselves")
def _instruments_self_test():
    a = "x = 1  # comment\n"
    b = "x = 1  # a completely different comment\n"
    assert code_tokens(a) == code_tokens(b), "code_tokens is sensitive to comment text"
    assert code_tokens(a) != code_tokens("x = 2\n"), "code_tokens missed a real code change"
    try:
        control_arm(lambda: False, lambda: None, lambda: None, describe="deliberately red baseline")
    except AssertionError:
        pass
    else:
        raise AssertionError("control_arm reported a result on a RED baseline -- its whole purpose")


def main() -> int:
    print(__doc__.strip().splitlines()[0])
    print("=" * 78)
    for line in ("Every assertion maps to a defect that shipped into a draft of #627.",
                 "Cannot check: whether a population is the RIGHT population, or anything in prose."):
        print(f"  - {line}")
    print()
    if FAILURES:
        print(f"\n{len(FAILURES)} failing assertion(s).")
        return 1
    print("\nAll assertions hold.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
