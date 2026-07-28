"""Assert the tooling's own enumerable surfaces, because the checkers had no checker.

Most assertions here map to a defect that actually shipped into a draft of #627 and was caught by a
reviewer or by hand; `_instruments_self_test` is the exception -- it guards the two helpers below
rather than a shipped defect. The surfaces are enumerable -- templates x settings, booleans x flag
directions, a regex x input classes -- which is the criterion CLAUDE.md gate `record-once` names for
when something earns a checker. That criterion had been applied to docs/decisions/ and vendor-verify
and never to the tooling being written.

Runs in CI's `audit` job alongside `completeness.py`. Plain asserts, no test framework: this repo's
python tooling has none, and adding one for a handful of assertions is the ceremony the gates exist
to cut.

    pixi run audit-selfcheck

Each assertion prints the size of the population it examined. A check whose population is empty is
not a passing check -- it is a check that compared nothing -- and that has to be legible in the
output rather than reading as OK.

WHAT THIS CANNOT CHECK
  * That a check's population is the RIGHT population. It asserts the join holds, never that the
    join is the one worth making.
  * Anything about prose. The defect class that dominated #627 -- a comment asserting what the code
    does not do -- is not reachable from here, and #631 is the other half of that answer.
"""
from __future__ import annotations

import ast
import contextlib
import importlib.util
import io
import re
import sys
import tokenize
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CHECKS: list[tuple[str, object]] = []
FAILURES: list[str] = []


def check(name):
    """Register a named assertion. Registration only -- main() runs them, so the header prints first.

    A returned string is the population the assertion examined, printed alongside the OK. An
    assertion that examined nothing says so.
    """
    def deco(fn):
        CHECKS.append((name, fn))
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


def register_models() -> set[str]:
    """The agy catalogue, from completeness.py's own parser rather than a second copy of it.

    A duplicated parse here would drift against the one step 9 actually uses, which is the failure
    the whole file is about.
    """
    accepted, why = completeness.register_models()
    assert accepted is not None, f"the register cannot be parsed, so nothing below can join to it: {why}"
    return accepted


def resolved_templates() -> dict[str, dict]:
    """Every template as `--template X` actually resolves it -- dispatch's defaults filled in."""
    return {name: dispatch.resolve(tpl) for name, tpl in dispatch.TEMPLATES.items()}


# ---------------------------------------------------------------------------------------------
# Two reusable instruments
# ---------------------------------------------------------------------------------------------

def code_tokens(text: str):
    """Every token except comments and docstrings -- the file's code, ignoring its prose.

    Turns "this commit only touches comments" from a characterisation into an assertion. It was
    written after a commit was described as prose-only while it had changed two user-visible string
    literals; running this is what caught that.

    Docstrings are located by POSITION, via ast, not by quote style. A `'''...'''` string used as a
    real value is code and stays; a triple-quoted string in a docstring slot is prose and goes. The
    earlier quote-style test had both backwards, and on Python 3.12+ (PEP 701) also missed
    f-string-quoted prose entirely, since an f-string no longer tokenizes as STRING.

    An instrument for a caller to point at two revisions of a file. It has no standing population, so
    nothing here asserts anything about this repo's own files with it -- only that it works.
    """
    docstrings = set()
    for node in ast.walk(ast.parse(text)):
        if isinstance(node, (ast.Module, ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            body = getattr(node, "body", None)
            if not body:
                continue
            first = body[0]
            if isinstance(first, ast.Expr) and isinstance(first.value, ast.Constant) \
                    and isinstance(first.value.value, str):
                docstrings.add((first.lineno, first.col_offset))

    out = []
    for tok in tokenize.generate_tokens(io.StringIO(text).readline):
        if tok.type in (tokenize.COMMENT, tokenize.NL):
            continue
        if tok.type == tokenize.STRING and tok.start in docstrings:
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

    Returns the mutated-tree result, having proved the baseline was green. Note what that does and
    does not buy: it rules out a baseline that fails for every input, but a `baseline` that ignores
    the mutation entirely still returns True twice and reports a NON-discriminating pass. Only the
    caller's assertion on the returned value catches that, which is why every caller asserts False.
    """
    assert baseline() is True, (
        f"control baseline is NOT green{' for ' + describe if describe else ''}, so nothing measured "
        "after this would mean anything. Two causes, and they need different fixes: either the "
        "harness is broken (the failure this exists to catch), or the tree ALREADY fails the check "
        "being controlled -- in which case fix that first and this arm will speak again. Check the "
        "other assertions: if one of them is also red, it is the second case."
    )
    mutate()
    try:
        return baseline()
    finally:
        restore()


# ---------------------------------------------------------------------------------------------
# The enumerable surfaces
# ---------------------------------------------------------------------------------------------

@check("every gemini template pins a model `agy models` lists")
def _pins_resolve():
    accepted = register_models()
    checked = []
    for name, tpl in resolved_templates().items():
        # Resolved, not raw: a template that omits `adapter` dispatches to gemini anyway, and reading
        # the raw dict would skip exactly that one.
        if tpl["adapter"] != "gemini" or not tpl["model"]:
            continue
        checked.append(name)
        assert tpl["model"] in accepted, (
            f"TEMPLATES[{name!r}] pins {tpl['model']!r}, which `agy models` does not list. "
            f"A pin the CLI rejects fails AFTER the operator has paid for the run (#547). "
            f"Accepted: {sorted(accepted)}"
        )
    assert checked, (
        "no template resolves to a gemini adapter with a model pin, so this compared nothing. "
        "Either the templates changed shape or `resolve()` stopped defaulting the adapter."
    )
    return f"{len(checked)} of {len(dispatch.TEMPLATES)} templates pin an agy model"


@check("no template is refused by dispatch's own permission guards")
def _templates_are_dispatchable():
    # Calls `grant_refusal` rather than restating its conditions. The restatement asserted
    # `write_files is True` for every template -- STRICTER than the product, which refuses only when
    # write AND shell are both withheld, and with a message that contradicted what #529 measured.
    assert dispatch.TEMPLATES, "TEMPLATES is empty -- this compared nothing"
    for name, tpl in resolved_templates().items():
        refusal = dispatch.grant_refusal(tpl)
        assert refusal is None, (
            f"TEMPLATES[{name!r}] resolves to a grant dispatch.py refuses before it can spend, so "
            f"the template cannot be used at all: {refusal}"
        )
    return f"{len(dispatch.TEMPLATES)} templates"


@check("every permission boolean can be turned OFF from the command line")
def _both_flag_directions():
    # The population comes from the templates rather than a hand-written list, so a fifth permission
    # is covered the day it is added.
    booleans = sorted({k for tpl in resolved_templates().values()
                       for k, v in tpl.items() if isinstance(v, bool)})
    assert booleans, "no boolean permissions found in TEMPLATES -- the population is empty"

    # PARSED, not grepped. The substring test this replaces passed on a source file whose arms were
    # declared in the order argparse silently mis-defaults: argparse takes a dest's default from the
    # FIRST action registered for it, so a `--no-` arm declared first makes the default False instead
    # of None and the tri-state collapses -- with the string `"--no-write-files"` still present.
    base = ["--list-templates"]  # the only argv that makes the three required args optional
    for key in booleans:
        flag = "--" + key.replace("_", "-")
        neutral = dispatch.build_parser(base).parse_args(base)
        assert getattr(neutral, key) is None, (
            f"{key} defaults to {getattr(neutral, key)!r}, not None. The tri-state is what makes "
            "'was this passed?' answerable; without it a template can never override a permission "
            "downward. Check which arm is declared first."
        )
        on = dispatch.build_parser(base + [flag]).parse_args(base + [flag])
        assert getattr(on, key) is True, f"{flag} does not set {key} True"
        off_flag = "--no-" + key.replace("_", "-")
        off = dispatch.build_parser(base + [off_flag]).parse_args(base + [off_flag])
        assert getattr(off, key) is False, (
            f"{off_flag} does not set {key} False, so a template cannot be overridden downward on "
            "it. That made `--template implement` a lock on exactly the two flags that resolve to "
            "--dangerously-skip-permissions."
        )
    return f"{len(booleans)} booleans x 3 directions (unset/on/off)"


@check("PIN_SHAPE rejects English; TOKEN_SHAPE accepts the whole catalogue")
def _shapes_discriminate():
    # PIN_SHAPE guards the tools/ walk, where `--model` appears in prose and every following word is
    # a candidate, so it requires a digit. TOKEN_SHAPE guards the register's own fence, where
    # requiring a digit would be INVERTED -- agy serves models from several vendors and a digit-free
    # catalogue entry would be reported as a bad parse.
    english = ("read-only", "fail-closed", "cross-vendor", "skip-permissions")
    for word in english:
        assert not completeness.PIN_SHAPE.fullmatch(word), (
            f"PIN_SHAPE matches {word!r}, an English word. It is everywhere in this repo, and a "
            "match makes the walk report it as an invalid model pin."
        )
    models = register_models()
    for model in models:
        assert completeness.TOKEN_SHAPE.fullmatch(model), \
            f"TOKEN_SHAPE rejects {model!r} -- step 9 would call a correct parse a bad one"
    # PIN_SHAPE is NOT asserted over the whole register: its digit requirement is a deliberate cost,
    # not a defect, so a digit-free catalogue entry is measured and reported rather than failed.
    blind = sorted(m for m in models if not completeness.PIN_SHAPE.fullmatch(m))
    for model in models - set(blind):
        assert completeness.PIN_SHAPE.fullmatch(model), f"PIN_SHAPE rejects {model!r} unexpectedly"
    note = f"{len(english)} English words rejected, {len(models)} catalogue entries accepted"
    return note + (f"; PIN_SHAPE is blind to {blind} (digit-free, invisible to the tools/ walk)"
                   if blind else "; no catalogue entry is digit-free, so the walk's blind spot is empty")


@check("step 9 fails CLOSED when either of its two file sources goes unreadable")
def _step9_fails_closed():
    # TWO of step 9's four sources, and the title says two rather than implying all four. The
    # uncontrolled pair: dispatch.py's own absence (a hard failure whose arm would need the import to
    # fail, not the read) and the tools/ regex walk (whose population is the whole tree).
    #
    # Monkeypatched rather than mutating the tree: a test that renames files leaves the repo broken
    # if it is interrupted, and completeness.py derives ROOT from __file__ so it cannot simply be
    # relocated -- that is what silently broke a control run into reading empty strings.
    # step 9 prints its full report on every call; the assertions read its return value, so the
    # output is noise here.
    def baseline():
        with contextlib.redirect_stdout(io.StringIO()):
            return completeness.step9_pinned_models_exist() is True

    real_read = completeness.read
    sources = {
        "docs/vendor-capabilities.md (the register)": "vendor-capabilities",
        "tools/vendor-verify/verify.py (the CHEAP pin)": "verify.py",
    }
    for label, needle in sources.items():
        result = control_arm(
            baseline,
            lambda needle=needle: setattr(completeness, "read",
                                          lambda p: "" if needle in p else real_read(p)),
            lambda: setattr(completeness, "read", real_read),
            describe=f"step9 with {label} unreadable")
        assert result is False, (
            f"step 9 passed with {label} unreadable -- a population that silently shrinks is how a "
            "check keeps printing OK about less and less"
        )
    return f"{len(sources)} of step 9's 4 sources controlled"


@check("no tooling file transcribes a count its own code computes")
def _no_transcribed_counts():
    # `record-once`: never transcribe a value that lives somewhere authoritative. Both patterns were
    # real -- a docstring said "eight steps" while main() ran nine, and a comment said "(today: 12)"
    # where the register's fence holds 11.
    #
    # The population is every python file in this pair of tools, INCLUDING this one, which is where
    # both live instances were: this file's own docstring said "six assertions" while more than that
    # were registered, and a comment said "invokes it five times" about a call it made fewer times.
    # (Both are quoted here, so `is_citation` skips them -- they are also its only live population.)
    files = sorted((ROOT / "tools" / "audit-completeness").glob("*.py")) + \
        sorted((ROOT / "tools" / "aer-agy-loop").glob("*.py"))
    assert files, "no tooling files found -- the population is empty"
    steps = len(re.findall(r"^def step\d", completeness.read("tools/audit-completeness/completeness.py"), re.M))
    fence_count = len(register_models())
    words = {"one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7,
             "eight": 8, "nine": 9, "ten": 10, "eleven": 11, "twelve": 12}

    def claimed(tok):
        tok = tok.lower()
        return words.get(tok, int(tok) if tok.isdigit() else None)

    def is_citation(src, m):
        """True if the count sits inside a double-quoted span on its own line.

        A quoted count is reporting what some OTHER text said; an unquoted one is this file making a
        claim. The distinction is not decoration: this check's own comment recording why it exists
        quotes both historical wrong values, and the first version failed on that sentence. A check
        that cries wolf about the note explaining it gets deleted.

        Two stated costs. A genuine transcription written inside double quotes is skipped. And only
        `"` is paired -- prose apostrophes make `'` unpairable -- so a single-quoted citation still
        reads as a claim.
        """
        line_start = src.rfind("\n", 0, m.start()) + 1
        line_end = src.find("\n", m.end())
        line = src[line_start:line_end if line_end != -1 else len(src)]
        quotes = [i for i, c in enumerate(line) if c == '"']
        rel = m.start() - line_start
        return any(a < rel < b for a, b in zip(quotes[0::2], quotes[1::2]))

    found = cited = 0
    for path in files:
        src = path.read_text(encoding="utf-8")
        for m in re.finditer(r"\b([a-z]+|\d+)\s+(steps|assertions)\b", src, re.I):
            n = claimed(m.group(1))
            if n is None:
                continue
            if is_citation(src, m):
                cited += 1
                continue
            found += 1
            expected = steps if m.group(2).lower() == "steps" else len(CHECKS)
            assert n == expected, (
                f"{path.name} claims {n} {m.group(2).lower()}; {expected} are defined. Cite the code "
                f"or drop the number -- this exact sentence stood at 'eight' while main() ran nine."
            )
        for m in re.finditer(r"today:\s*(\d+)", src):
            if is_citation(src, m):
                cited += 1
                continue
            found += 1
            assert int(m.group(1)) == fence_count, (
                f"{path.name} says 'today: {m.group(1)}' where the register's fence holds {fence_count}"
            )
    # Zero transcribed counts is the DESIRED state -- this is a lint, and a lint with no violations
    # is healthy. So an empty population is not a failure, but it is REPORTED as empty: the version
    # of this check that scanned one file found nothing after that file was rewritten, and went on
    # printing OK about a comparison it was no longer making.
    scanned = f"{len(files)} files scanned"
    skipped = f", {cited} quoted citation(s) skipped" if cited else ""
    return (f"{scanned}, {found} transcribed count(s) verified{skipped}" if found
            else f"{scanned}{skipped}; NOTHING transcribes a count, so nothing was compared")


@check("the two reusable instruments work on themselves")
def _instruments_self_test():
    # Discriminating in both directions. The earlier version asserted only that a comment change is
    # invisible, which stayed green with the docstring branch deleted entirely.
    assert code_tokens("x = 1  # comment\n") == code_tokens("x = 1  # different comment\n"), \
        "code_tokens is sensitive to comment text"
    assert code_tokens('"""doc a."""\nx = 1\n') == code_tokens('"""doc b."""\nx = 1\n'), \
        "code_tokens is sensitive to docstring text"
    assert code_tokens("x = 1\n") != code_tokens("x = 2\n"), "code_tokens missed a real code change"
    # The defect it was written for: a string literal a USER sees is code, not prose, however it is
    # quoted. Triple-quoted and not in a docstring slot, so quote style alone cannot classify it.
    assert code_tokens('x = """v1"""\n') != code_tokens('x = """v2"""\n'), \
        "code_tokens treated a triple-quoted VALUE as prose -- quote style is not position"

    try:
        control_arm(lambda: False, lambda: None, lambda: None, describe="deliberately red baseline")
    except AssertionError:
        pass
    else:
        raise AssertionError("control_arm reported a result on a RED baseline -- its whole purpose")
    return "4 code_tokens polarities + control_arm's red baseline"


def main() -> int:
    print(__doc__.strip().splitlines()[0])
    print("=" * 78)
    for line in ("Most assertions map to a defect that shipped into a draft of #627.",
                 "Cannot check: whether a population is the RIGHT population, or anything in prose."):
        print(f"  - {line}")
    print()

    for name, fn in CHECKS:
        try:
            population = fn()
        except AssertionError as e:
            FAILURES.append(f"{name}: {e}")
            print(f" !! {name}\n      {e}")
        else:
            print(f" OK {name}")
            if population:
                print(f"      {population}")

    if FAILURES:
        print(f"\n{len(FAILURES)} failing assertion(s).")
        return 1
    print(f"\nAll {len(CHECKS)} assertions hold.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
