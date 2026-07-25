"""Recompute the audit's completeness ledger, so "we did all of it" is checkable rather than claimed.

WHY THIS EXISTS
---------------
`docs/documentation-lessons.md` rule 15 says any claim of completeness ships with the artifact that
lets someone check it. This is that artifact for the #527 audit chain.

The chain has eight steps, and each one has a population that can be ENUMERATED and a disposition
that must exist for every member:

  1 sources        every source considered -> included, or excluded with a reason
  2 corpus read    every mirrored page    -> a ledger disposition
  3 gaps verified  every backlog row      -> a vendor-verify check, or a stated reason it cannot run
  4 fixed/filed    every defect found     -> a commit or an issue number
  5 what changed   every measured finding -> an architectural implication, or "no impact" + why
  6 design         every decision 0001-28 -> reviewed, amended, superseded, or unaffected + why
  7 milestones     every open milestone   -> re-checked against the changes
  8 build plan     every design decision  -> a sequenced piece of work

This script recomputes what is mechanically recomputable (populations, and which members carry a
disposition) and prints what it CANNOT check, because a completeness checker that hides its own
blind spots is the thing it exists to prevent.

    pixi run audit-completeness
"""
from __future__ import annotations

import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def read(path):
    p = os.path.join(ROOT, path)
    if not os.path.exists(p):
        return ""
    with open(p, encoding="utf-8", errors="replace") as f:
        return f.read()


def rule(title):
    print("\n" + "=" * 78)
    print(title)
    print("=" * 78)


def line(label, got, expected=None, note=""):
    ok = "  " if expected is None else ("OK" if got == expected else "!!")
    exp = "" if expected is None else f"  (expected {expected})"
    print(f" {ok} {label:<46} {got}{exp}{note and '  -- ' + note}")
    return expected is None or got == expected


def step1_sources():
    rule("STEP 1 -- every doc source considered has a disposition")
    survey = read("tools/vendor-survey/vendor_survey.py")
    included = {
        "claude docs (llms.txt)": "CLAUDE_INDEX" in survey,
        "agy docs (sitemap.xml)": "AGY_SITEMAP" in survey,
        "MCP specification (llms.txt)": "MCP_INDEX" in survey,
        "vendor CLI --help (both)": "fetch_cli_help" in survey,
        "agy changelog / terms / pricing / product": "EXTRA_SOURCES" in survey,
        "agy GitHub CHANGELOG": "github-CHANGELOG" in survey,
        "both vendors' issue trackers": "ISSUE_REPOS" in survey,
    }
    ok = True
    for name, present in included.items():
        ok &= line(name, "included" if present else "MISSING", "included")
    print("\n Excluded, with reason (each must be a deliberate call, not an omission):")
    for name, why in [
        ("vendor CLI runtime logs", "manual surface; read directly when a run misbehaves"),
        ("SDK package source", "both SDKs are API-key-only and were rejected (Rule 4)"),
        ("anything behind vendor auth", "not reachable from an agent session"),
        ("Anthropic API docs (docs.claude.com)", "AER spawns CLIs, never the API -- Rule 4"),
    ]:
        print(f"    - {name:<42} {why}")
    return ok


def step2_corpus():
    rule("STEP 2 -- every mirrored page carries a ledger disposition")
    ledger = read(".vendor-survey/ledger.tsv")
    if not ledger.strip():
        print("    !! no ledger found -- run `pixi run vendor-survey` first")
        return False
    rows = [r for r in ledger.splitlines()[1:] if r.strip()]
    corpus_dir = os.path.join(ROOT, ".vendor-survey", "corpus")
    pages = len([f for f in os.listdir(corpus_dir)]) if os.path.isdir(corpus_dir) else 0
    dispositions = {}
    for r in rows:
        parts = r.split("\t")
        if len(parts) >= 2:
            dispositions[parts[-1].strip()] = dispositions.get(parts[-1].strip(), 0) + 1
    ok = line("pages mirrored", pages)
    ok &= line("pages with a ledger row", len(rows), pages,
               "every page must have a disposition")
    for d, n in sorted(dispositions.items()):
        print(f"      {d:<20} {n}")
    return ok


def step3_gaps():
    rule("STEP 3 -- every verification-backlog row resolves")
    coverage = read("docs/vendor-coverage.md")
    verify = read("tools/vendor-verify/verify.py")
    checks = re.findall(r'^@check\("([^"]+)"', verify, re.M)
    struck = len(re.findall(r"~~", coverage)) // 2
    open_rows = len(re.findall(r"\*\*open\*\*", coverage))
    line("vendor-verify checks registered", len(checks))
    line("backlog rows struck (verified/corrected)", struck)
    line("rows explicitly marked open", open_rows)
    print("\n Registered checks:")
    for c in sorted(checks):
        print(f"    - {c}")
    # Not mechanically checkable: whether every struck row maps to a check. Say so.
    print("\n NOT auto-checkable here: that each struck row names the check that re-runs it.")
    print(" Verified by reading; the checks above are the population it must draw from.")
    return True


# A sweep row must land on one of these words. Free prose is not a disposition: "0015 was
# considered" passes a substring test while saying nothing. Fixed vocabulary makes the sweep
# answerable -- and makes a decision that got no real look impossible to hide.
DISPOSITIONS = ("unaffected", "amended", "superseded", "rewritten")

# Decisions the audit's own findings put in question. Each MUST carry its own `Rests on` table,
# counted per file -- summing across files lets one table satisfy the requirement for two.
MUST_REST_ON = (
    "0015-three-kinds-of-needs-you.md",
    "0018-attention-is-the-primary-signal.md",
)


def step6_decisions():
    rule("STEP 6 -- every decision record has a disposition in the audit sweep")
    d = os.path.join(ROOT, "docs", "decisions")
    files = sorted(f for f in os.listdir(d) if re.match(r"^\d{4}-", f)) if os.path.isdir(d) else []
    sweep = read("docs/decision-audit.md")
    line("decision records on disk", len(files))
    if not sweep.strip():
        print("    !! docs/decision-audit.md not written yet -- step 6 incomplete")
        return False

    # A row is only a disposition if it names the decision AND one of the vocabulary words AND
    # gives a reason. Mentioning "0015" somewhere in a table is not a disposition.
    #
    # The reason is read from a FIXED COLUMN, not from "whatever prose is left in the row".
    # Measuring the whole row would let any wide column satisfy it -- a title column alone
    # ("A pause asks for one of three things") clears any length bar while saying nothing about
    # why the decision survived the audit. Required shape, three columns exactly:
    #
    #     | 0015 | rewritten | <why, in the auditor's own words> |
    rows = {}
    for raw in sweep.splitlines():
        cells = [c.strip() for c in raw.strip().strip("|").split("|")]
        if len(cells) < 3 or not re.fullmatch(r"\[?(\d{4})\]?.*", cells[0]):
            continue
        num = re.match(r"\[?(\d{4})", cells[0]).group(1)
        verb = next((v for v in DISPOSITIONS if v == cells[1].strip("* ").lower()), None)
        rows[num] = (verb, len(cells[2]) >= 25)

    bad = []
    for f in files:
        num = f[:4]
        verb, reason = rows.get(num, (None, False))
        if verb is None:
            bad.append((f, "no disposition row" if num not in rows
                        else f"row names none of {'/'.join(DISPOSITIONS)}"))
        elif not reason:
            bad.append((f, f"'{verb}' asserted with no reason given"))
    line("decisions with a vocabulary disposition + reason", len(files) - len(bad), len(files))
    for f, why in bad:
        print(f"      INCOMPLETE: {f:<52} {why}")

    ok = not bad
    for name in MUST_REST_ON:
        n = len(re.findall(r"^## Rests on", read(f"docs/decisions/{name}"), re.M))
        ok &= line(f"`Rests on` in {name[:4]}", n, 1, "counted per file, never summed")
    return ok


def git_state():
    rule("REPO STATE")
    for label, cmd in [("branch", ["git", "rev-parse", "--abbrev-ref", "HEAD"]),
                       ("commits ahead of main", ["git", "rev-list", "--count", "origin/main..HEAD"]),
                       ("uncommitted files", ["git", "status", "--porcelain"])]:
        try:
            out = subprocess.run(cmd, capture_output=True, text=True, cwd=ROOT).stdout.strip()
        except OSError:
            out = "?"
        if label == "uncommitted files":
            out = str(len([x for x in out.splitlines() if x.strip()]))
        line(label, out)


def main() -> int:
    print(__doc__.split("USAGE")[0].strip().splitlines()[0])
    results = [step1_sources(), step2_corpus(), step3_gaps(), step6_decisions()]
    git_state()
    rule("WHAT THIS SCRIPT CANNOT CHECK")
    for x in [
        "That a finding's architectural implication is CORRECT -- only that one was recorded.",
        "Steps 4, 5, 7 and 8, whose populations are prose findings rather than files on disk.",
        "That the vendor-verify checks still pass -- run `pixi run vendor-verify` for that.",
        "Whether a source nobody thought of exists. Enumeration cannot find its own blind spot.",
    ]:
        print(f"    - {x}")
    print()
    return 0 if all(results) else 1


if __name__ == "__main__":
    sys.exit(main())
