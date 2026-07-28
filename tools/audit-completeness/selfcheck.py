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

Each assertion reports the population it examined, and `main()` fails any check that does not --
because "OK" over an empty population is the failure mode this file exists to catch, and a check
that quietly stopped comparing looks exactly like one that compared and agreed. A LINT with nothing
to flag is still a pass; it just has to say what it searched.

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
import json
import re
import subprocess
import sys
import tempfile
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


# Module-level so `controls.py` can point a check at a MUTATED COPY in a temp tree. A control that
# edited these tracked files in place would leave the repo broken if it were interrupted, and the
# faults being injected are deliberately the kind that make a checker pass -- the worst kind to
# leave behind.
DISPATCH_PY = ROOT / "tools" / "aer-agy-loop" / "dispatch.py"
LINT_DIRS = (ROOT / "tools" / "audit-completeness", ROOT / "tools" / "aer-agy-loop")

dispatch = load(DISPATCH_PY, "_selfcheck_dispatch")
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
    """The file's code, ignoring its prose: every token except comments, docstrings, and whitespace.

    WHAT IT CANNOT SEE, stated because the obvious reading of the line above is wrong: NEWLINE,
    INDENT and DEDENT all strip to empty and are dropped with the rest of the whitespace, so BLOCK
    STRUCTURE is invisible. `if x:\\n    y = 1\\nz = 2` and `if x:\\n    y = 1\\n    z = 2` produce
    identical token lists. Moving a statement into or out of a conditional, a loop or a `try` is
    exactly the edit someone would want a "prose-only?" instrument to catch, and this one does not.
    The self-test below carries that case as a known-failing polarity rather than leaving it implied.

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


def is_citation(src, m):
    """True if the count sits inside a double-quoted span on its own line.

    A quoted count is reporting what some OTHER text said; an unquoted one is this file making a
    claim. The distinction is not decoration: this check's own comment recording why it exists
    quotes both historical wrong values, and the first version failed on that sentence. A check
    that cries wolf about the note explaining it gets deleted.

    Two stated costs. A genuine transcription written inside double quotes is skipped. And only
    `"` is paired -- prose apostrophes make `'` unpairable -- so a single-quoted citation still
    reads as a claim.

    Triple-quote delimiters are blanked before pairing, and that is load-bearing rather than tidy: a
    ONE-LINE docstring wraps its own contents in `"` characters, so a wrong count inside one paired
    as a quoted span and was skipped as a citation. `controls.py` caught it on its first run, with a
    planted count the lint reported as clean.

    Writing that example out with a real number here made this very file trip the lint -- the third
    fixture in a row to do so. Anything illustrating a count must not BE one.
    """
    line_start = src.rfind("\n", 0, m.start()) + 1
    line_end = src.find("\n", m.end())
    line = src[line_start:line_end if line_end != -1 else len(src)]
    # Blanked, not stripped, so every offset below still lines up with the real line.
    line = re.sub(r'"{3}', "   ", line)
    quotes = [i for i, c in enumerate(line) if c == '"']
    rel = m.start() - line_start
    return any(a < rel < b for a, b in zip(quotes[0::2], quotes[1::2]))


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


@check("no template is refused, and every grant the shell would over-reach is")
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

    # The templates alone CANNOT catch what this check exists for: every one of them is coherent, so
    # the loop above stays green under any rule that only ever refuses. The refusals have to be
    # asserted directly, on the grants that separate one rule from the next.
    #
    # `write_files: False` is refused under every shell setting *on an adapter whose withheld writes
    # cannot reach the outbox* -- which since #649 is a per-adapter answer, not a universal one. The
    # arms below inherit `BUILT_IN["adapter"] == "gemini"`, and that is load-bearing rather than
    # incidental: the same grants dispatch on claude, which the pair above asserts directly. So
    # `grant_refusal` no longer adds up to "writes are always required", and the three conditions
    # cannot be collapsed into one predicate even in principle. The conditions are kept apart for their messages, not their verdicts. Asserted
    # here so the sum is a fact that runs, and so collapsing them has to fail this instead of quietly
    # losing the read_files and network arms.
    granted = {**dispatch.BUILT_IN, "read_files": True, "write_files": True,
               "run_shell_commands": True, "network_access": True}

    # One arm per category the rule must keep refusing, because each is separately droppable. Read
    # and network have no template behind them at all -- every template either grants both or
    # withholds the shell -- so dropping either from the rule leaves the loop above green.
    refusal_arms = {
        "writes withheld, shell granted": {**granted, "write_files": False},
        # Same category, no shell: a different rule and a different message, so it needs its own arm.
        "writes withheld, no shell": {**granted, "write_files": False, "run_shell_commands": False},
        "reads withheld, shell granted": {**granted, "read_files": False},
        "network withheld, shell granted": {**granted, "network_access": False},
    }
    # #649: on an adapter whose withheld writes reach AER_OUTPUT_DIR the "nothing here can write the
    # output" arm is false, and refusing it refuses the read-only reviewer the whole feature exists
    # for. Asserted as a pair against the identical gemini grant, so the rule cannot be satisfied by
    # allowing that shape everywhere -- which is what would silently un-refuse gemini, where the
    # vendor still denies the write before AER's hook is reached.
    reviewer = {**granted, "write_files": False, "run_shell_commands": False}
    assert dispatch.grant_refusal({**reviewer, "adapter": "claude"}) is None, (
        "a read-only claude reviewer is refused. Its declared output lands in AER_OUTPUT_DIR, which "
        "a withheld write still reaches on that adapter (#649) -- refusing it forces every reviewing "
        "template to grant a workspace write it does not need."
    )
    assert dispatch.grant_refusal({**reviewer, "adapter": "gemini"}) is not None, (
        "the same grant dispatches on gemini, which cannot satisfy the contract -- see #901."
    )

    for label, arm in refusal_arms.items():
        assert dispatch.grant_refusal(arm) is not None, (
            f"a grant with {label} dispatches. With the shell it is #529's over-grant -- the "
            "operator withheld a category `cat`, redirection or `curl` reaches anyway, and AER "
            "refuses it at bind time; without the shell nothing can satisfy the ProducedOutputs "
            "contract and the run burns its full budget to fail (#629)."
        )

    # The discriminating control. Without it every arm above passes on a `grant_refusal` that refuses
    # everything, which would make the whole dispatch path dead and still print OK.
    assert dispatch.grant_refusal(granted) is None, (
        "the fully-granted shell grant is refused, so no template exercising the write path can "
        "dispatch at all. The coherence rule is meant to refuse grants that withhold a category the "
        "shell reaches -- not grants that carry a shell."
    )
    return (f"{len(dispatch.TEMPLATES)} templates + {len(refusal_arms)} refusal arms "
            "+ the coherent control + the outbox-capable/incapable pair")


@check("every template dry-runs clean through the real command line")
def _templates_dry_run():
    # The checks above import `grant_refusal` and `build_parser` separately. Neither covers their
    # COMPOSITION in main(): precedence applied, then guards, then the workflow/bindings build. This
    # goes through the actual command line, which is the only surface an operator uses.
    #
    # Free to run and needs no vendor -- that is what #639's --dry-run bought. Before it, the only
    # grants testable without spending were the ones the guards REFUSE, so the allow path could only
    # be checked by paying for a run, and checking it once cost exactly that.
    assert dispatch.TEMPLATES, "TEMPLATES is empty -- this compared nothing"
    with tempfile.TemporaryDirectory() as scratch:
        def dry_run(*extra):
            cmd = [sys.executable, str(DISPATCH_PY),
                   "--prompt-file", str(ROOT / "CLAUDE.md"), "--output-name", "out",
                   "--working-directory", str(ROOT), "--scratch-root", scratch,
                   "--dry-run", *extra]
            return subprocess.run(cmd, capture_output=True, text=True, cwd=ROOT)

        for name in sorted(dispatch.TEMPLATES):
            done = dry_run("--template", name)
            assert done.returncode == 0, (
                f"`--template {name} --dry-run` exits {done.returncode}, so the template cannot be "
                f"dispatched at all:\n{done.stderr.strip()[:400]}"
            )
            # Exit 0 plus a written bindings.json is ALSO true of a real, successful dispatch. Delete
            # --dry-run's early return and, on a machine with Aer.Cli.exe built, this check becomes
            # four live vendor runs -- two agy, one opus at xhigh -- and still prints OK. The marker
            # is the only thing that distinguishes "stopped" from "spent".
            assert "DRY RUN -- nothing was dispatched" in done.stdout, (
                f"`--template {name} --dry-run` exited 0 without printing the dry-run report, so it "
                "may have DISPATCHED. Cost is the operator's call; a checker must not be able to "
                "start spending on its own."
            )
            payload = json.loads((Path(scratch) / "bindings.json").read_text(encoding="utf-8"))
            entry = payload["worker"]
            expected = dispatch.resolve(dispatch.TEMPLATES[name])
            # The bindings are what AER actually reads. A template's dict agreeing with itself proves
            # nothing about what reached the engine -- precedence runs in between. All eight resolved
            # keys, not just the permissions: a precedence bug that dropped the MODEL pin is #547's
            # failure class, and it would be invisible to the only check that reads the bindings.
            for key, field in (("read_files", "ReadFiles"), ("write_files", "WriteFiles"),
                               ("run_shell_commands", "RunShellCommands"),
                               ("network_access", "NetworkAccess")):
                assert entry["PermissionGrant"][field] == expected[key], (
                    f"template {name!r} declares {key}={expected[key]} but the generated bindings "
                    f"carry {field}={entry['PermissionGrant'][field]} -- precedence dropped it on "
                    "the way to the engine"
                )
            assert entry["Adapter"] == expected["adapter"], (
                f"template {name!r} declares adapter={expected['adapter']!r}; bindings carry "
                f"{entry['Adapter']!r}")
            # Model and Effort are OMITTED when unset rather than written null, so absence is the
            # assertion for a template that pins nothing -- see build_bindings.
            for key, field in (("model", "Model"), ("effort", "Effort")):
                if expected[key]:
                    assert entry.get(field) == expected[key], (
                        f"template {name!r} pins {key}={expected[key]!r}; bindings carry "
                        f"{entry.get(field)!r}")
                else:
                    assert field not in entry, (
                        f"template {name!r} sets no {key}, but bindings carry "
                        f"{field}={entry.get(field)!r} -- AER would pin something nobody chose")
            # Exercises the hour-split at the same time: "00:90:00" is malformed under .NET's
            # [-][d.]hh:mm:ss, and every timeout below 60 hides it.
            hours, minutes = divmod(expected["timeout_minutes"], 60)
            assert entry["Timeout"] == f"{hours:02d}:{minutes:02d}:00", (
                f"template {name!r} sets timeout_minutes={expected['timeout_minutes']} but bindings "
                f"carry Timeout={entry['Timeout']!r}")

        # Polarity, through the same surface: a grant nothing can satisfy must be REFUSED, not
        # dry-run clean. Asserted on the MESSAGE, not the exit code -- dispatch.py returns 2 from the
        # guard AND from the missing-CLI branch, and argparse exits 2 on any command-line error, so a
        # one-character typo in the flag below would make this arm measure nothing forever.
        refused = dry_run("--no-write-files", "--no-run-shell-commands")
        assert "nothing here can write the output" in refused.stderr, (
            f"a grant withholding both writes and the shell did not produce the guard's refusal "
            f"(exit {refused.returncode}). The guards do not fire through the command line, or the "
            f"arm's own flags no longer parse:\n{refused.stderr.strip()[:300]}"
        )

        # The other refusal, on its own message for the same reason. This one would otherwise reach
        # AER and be refused there instead, after the operator has committed to the flags (#529).
        incoherent = dry_run("--no-write-files", "--run-shell-commands", "--network-access")
        assert "reaches both anyway" in incoherent.stderr, (
            f"a grant withholding writes while granting the shell dry-ran clean (exit "
            f"{incoherent.returncode}). dispatch.py would build a bindings.json that "
            f"WorkerBindingResolver refuses:\n{incoherent.stderr.strip()[:300]}"
        )
    return (f"{len(dispatch.TEMPLATES)} templates x 8 resolved keys vs the generated bindings, "
            "+ 2 refusal polarities")


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


@check("both shapes accept known pins, and PIN_SHAPE rejects English")
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
    # POSITIVE control, against a literal rather than the register. Asserting either shape over
    # `register_models()` proves nothing: that parser REJECTS any register whose tokens do not all
    # fullmatch TOKEN_SHAPE (completeness.py's `unshaped` arm), so the population arrives
    # pre-filtered and the assertion is satisfied by the filter that produced it. With no positive
    # control, `PIN_SHAPE = re.compile(r"(?!)")` -- a regex matching NOTHING -- left every assertion
    # in this file green while step 9's tools/ walk silently stopped finding any pin at all.
    known_pins = ("gemini-3.1-pro-high", "gemini-3.6-flash-low", "claude-sonnet-4-6",
                  "gpt-oss-120b-medium")
    for pin in known_pins:
        assert completeness.PIN_SHAPE.fullmatch(pin), (
            f"PIN_SHAPE rejects {pin!r}, a real agy model name. The tools/ walk gates on this, so it "
            "would stop finding pins entirely and step 9 would pass by looking at nothing."
        )
        assert completeness.TOKEN_SHAPE.fullmatch(pin), (
            f"TOKEN_SHAPE rejects {pin!r} -- the register parse would call a correct parse a bad one"
        )
    models = register_models()
    # The register is still read, for the ONE thing it can honestly say: how big PIN_SHAPE's stated
    # blind spot currently is. Its digit requirement is a deliberate cost, not a defect, so a
    # digit-free catalogue entry is measured and reported rather than failed.
    blind = sorted(m for m in models if not completeness.PIN_SHAPE.fullmatch(m))
    note = (f"{len(english)} English words rejected, {len(known_pins)} known pins accepted, "
            f"{len(models)} catalogue entries parsed")
    return note + (f"; PIN_SHAPE is blind to {blind} (digit-free, invisible to the tools/ walk)"
                   if blind else "; no catalogue entry is digit-free, so the walk's blind spot is empty")


@check("the gate-citation lint separates a slug from an ordinal")
def _gate_lint_discriminates():
    # Step 10's population is the whole repo, so it can only ever report "0 faults" -- which is what
    # a lint pointed at nothing also reports. `gate_citation_faults` is pure for exactly this
    # reason: drive it with planted input and both directions become checkable.
    slugs = completeness.gate_slugs(completeness.read("CLAUDE.md"))
    assert slugs, "CLAUDE.md defines no gate slugs -- the lint has no expected set to judge against"

    # ASSEMBLED, NOT SPELLED OUT -- the fifth fixture in this pair of files to need it. Every checker
    # here scans the directory it lives in, so a fault written as a literal IS a fault, in a real
    # file, and the checker reports itself. Step 10 did exactly that on these two lines. The rule:
    # a fixture for a checker must not be readable BY that checker.
    ordinal = "run this before shipping -- CLAUDE.md gate " + "8."
    absent_slug = "see gate " + "`record-twice` for the rule."

    # MUST be caught. The first is what `pixi.toml` actually carried; the second is what renaming a
    # gate would leave behind everywhere.
    caught = {"an ordinal": ordinal, "a slug that does not exist": absent_slug}
    for label, text in caught.items():
        faults = completeness.gate_citation_faults({"planted.md": text}, slugs)
        assert faults, f"the lint does not flag {label}: {text!r}"

    # MUST NOT be caught. A lint that fires on correct prose gets deleted, and each of these was a
    # real false positive or a near-miss: `DependsOn` is a validity gate in milestone-history.md and
    # not a shipping gate at all, and it only matched because a blanket re.I made `[a-z]` match
    # capitals too.
    ignored = {
        "a correct slug citation": f"see gate `{sorted(slugs)[0]}` for the rule.",
        "an unrelated capitalised gate name": "the validity gate `DependsOn` walks ancestors.",
        "a hyphenated ordinal": "the gate-7 branch is unrelated.",
        "the bare word": "this Gate is a different thing entirely.",
    }
    for label, text in ignored.items():
        faults = completeness.gate_citation_faults({"planted.md": text}, slugs)
        assert not faults, f"the lint fires on {label}: {text!r} -> {faults}"

    return (f"{len(slugs)} slugs; {len(caught)} fault shapes caught, "
            f"{len(ignored)} correct shapes ignored")


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
    # against the count `register_models()` computes.
    #
    # The population is every python file in this pair of tools, INCLUDING this one, which is where
    # the live instances were: this file's own docstring said "six assertions" while more were
    # registered. Those quoted counts are what `is_citation` is exercised on -- three of them, across
    # two comments, and if it misclassified any one the assert below would fire.
    #
    # SCOPE, because the report says "nothing transcribes a count" and that is a claim about these
    # two patterns, not about the tree: only `<n> steps`, `<n> assertions` and `today: <n>` are
    # searched. Prose that transcribes a computed value in any other shape is invisible here. Two
    # such were found by a reviewer and fixed by CITING the expression instead -- which is the actual
    # remedy, since no pattern list will ever cover English.
    files = sorted(f for d in LINT_DIRS for f in d.glob("*.py"))
    assert files, "no tooling files found -- the population is empty"
    steps = len(re.findall(r"^def step\d", completeness.read("tools/audit-completeness/completeness.py"), re.M))
    # `completeness.read` returns "" for a missing path, so a rename or a typo makes this 0 in
    # silence -- and the first real transcription it ever caught would be reported as "claims 9
    # steps; 0 are defined". The same "population that silently shrinks" this file checks for
    # elsewhere.
    assert steps, ("no `def stepN` functions found in completeness.py -- the value this lint "
                   "compares against is not being computed, so it cannot judge any claim")
    fence_count = len(register_models())
    words = {"one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7,
             "eight": 8, "nine": 9, "ten": 10, "eleven": 11, "twelve": 12}

    def claimed(tok):
        tok = tok.lower()
        return words.get(tok, int(tok) if tok.isdigit() else None)

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
    # A table rather than a run of asserts, so the count in the population line is COUNTED. Written
    # as `4 code_tokens polarities` it was already wrong one edit later, in the file that lints for
    # exactly that -- and the lint's patterns do not cover the word "polarities", so it stayed green.
    #
    # `same=True` means the two inputs must be indistinguishable to the instrument (prose changed);
    # `same=False` means it must tell them apart (code changed). Both directions, because a
    # `code_tokens` that returned [] for everything would satisfy only the first kind.
    polarities = [
        ("comment text is invisible", "x = 1  # comment\n", "x = 1  # different comment\n", True),
        ("docstring text is invisible", '"""doc a."""\nx = 1\n', '"""doc b."""\nx = 1\n', True),
        ("a real code change is visible", "x = 1\n", "x = 2\n", False),
        # The defect it was written for: a string literal a USER sees is code, not prose, however it
        # is quoted. Triple-quoted and not in a docstring slot, so quote style cannot classify it.
        ("a triple-quoted VALUE is code", 'x = """v1"""\n', 'x = """v2"""\n', False),
        # The KNOWN blind spot, pinned in the direction it actually behaves so the docstring's stated
        # limitation is checked rather than merely claimed. If a future edit starts keeping
        # NEWLINE/INDENT/DEDENT this fails, and the docstring has to be corrected with it.
        ("block structure is NOT visible (known gap)",
         "if x:\n    y = 1\nz = 2\n", "if x:\n    y = 1\n    z = 2\n", True),
    ]
    for label, a, b, same in polarities:
        if same:
            assert code_tokens(a) == code_tokens(b), f"code_tokens: {label} -- it distinguished them"
        else:
            assert code_tokens(a) != code_tokens(b), f"code_tokens: {label} -- it missed the change"

    try:
        control_arm(lambda: False, lambda: None, lambda: None, describe="deliberately red baseline")
    except AssertionError:
        pass
    else:
        raise AssertionError("control_arm reported a result on a RED baseline -- its whole purpose")
    return (f"{len(polarities)} code_tokens polarities "
            f"({sum(1 for p in polarities if not p[3])} must discriminate) "
            "+ control_arm's red baseline")


@check("the record-once checker fires on restatement and not on pointers")
def _recordonce_discriminates():
    """Both polarities, because a checker that never fires reads exactly like a clean repo.

    Fed constructed diffs rather than the live one: the live diff is whatever today's branch happens
    to contain, so asserting on it would go green or red for reasons unrelated to the checker.
    """
    rec = load(ROOT / "tools" / "audit-completeness" / "recordonce.py", "_selfcheck_recordonce")

    restated = {
        "src/A.cs": [["// #901 says the vendor refuses the write.", "// It does not, measured."]],
        "docs/B.md": [["#901 records that the vendor does not refuse.", "Measured against the CLI."]],
    }
    assert rec.violations(restated), (
        "two files each explaining #901 was accepted -- the exact shape this exists for, and "
        "accepting it makes the checker decorative")

    # The control that matters: the FIX for the above must pass, or the checker tells people to
    # delete their links rather than their duplication.
    canonical = {
        "src/A.cs": [["// See #901."]],
        "docs/B.md": [["#901 records that the vendor does not refuse.", "Measured against the CLI."]],
    }
    assert not rec.violations(canonical), (
        "one explanation plus one pointer was rejected -- that is the shape record-once ASKS for")

    suppressed = {
        "src/A.cs": [["// #901 explained here.", "// record-once-ok: #901 canonical is docs/B.md"]],
        "docs/B.md": [["#901 explained here too.", "Second line."]],
    }
    assert not rec.violations(suppressed), "record-once-ok did not suppress its own issue"

    # And one escape must not silence everything.
    wrong_issue = {
        "src/A.cs": [["// #902 explained here.", "// record-once-ok: #901 unrelated"]],
        "docs/B.md": [["#902 explained here too.", "Second line."]],
    }
    assert rec.violations(wrong_issue), "record-once-ok for #901 silenced #902"

    return "4 polarities (restated / canonical / suppressed / wrong-issue suppression)"


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
        except Exception as e:  # noqa: BLE001 -- see below
            # Not just AssertionError. A check can raise FileNotFoundError (a bindings.json that was
            # never written), JSONDecodeError, or SystemExit(2) from argparse if a flag it names is
            # removed. Any of those used to abort the whole run before the remaining checks and
            # before the summary -- the exit code stayed non-zero, so never a false pass, but the
            # file's whole premise is that a failure says what failed.
            FAILURES.append(f"{name}: {type(e).__name__}: {e}")
            print(f" !! {name}\n      raised {type(e).__name__}: {e}\n"
                  f"      (a raise, not a failed assertion -- the check itself is broken)")
        else:
            if not population:
                # An assertion that reports no population cannot be distinguished from one that
                # examined nothing, which is the whole defect class here.
                FAILURES.append(f"{name}: reported no population")
                print(f" !! {name}\n      passed without reporting a population -- it cannot be "
                      "told apart from a check that compared nothing")
            else:
                print(f" OK {name}")
                print(f"      {population}")

    if FAILURES:
        print(f"\n{len(FAILURES)} failing assertion(s).")
        return 1
    # (checks are registered above; see _recordonce_discriminates)
    print(f"\nAll {len(CHECKS)} assertions hold.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
