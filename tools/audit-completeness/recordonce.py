"""Fail a change that explains the same issue in more than one place (#671).

`record-once` is the gate with the worst compliance record in this repo and the only one with no
checker. It is prose enforcing prose, so it fails the way prose does: one change restated a single
corrected fact into five files, and CI was green throughout. Every other recurring failure here got
mechanised once it was understood; this is that, for this one.

Operates on the DIFF, never the tree. The tree legitimately accumulates many references to `#529`
over years -- that is the register working. The signal is one change writing the same explanation in
several places at once.

    pixi run audit-recordonce            # against origin/main
    pixi run audit-recordonce -- <base>  # against any other base

WHAT IT CANNOT CHECK, stated because the obvious reading of the rule is wider than this:
  * A comment the change FALSIFIED without touching. That is the harder half of the same problem and
    it is #636's, not this one's -- an untouched line is absent from the diff by definition.
  * Whether the one explanatory site is the RIGHT one. It counts sites; it does not rank them.
  * A fact restated without citing an issue number. The reference is the only mechanical handle on
    "this is the same fact", and a restatement that cites nothing is invisible here.
"""
from __future__ import annotations

import collections
import re
import subprocess
import sys

# Three digits or more: `#12` is far more likely to be a heading level, an ordinal, or a colour than
# an issue in a repo whose numbering is well past 600.
ISSUE = re.compile(r"#(\d{3,})")

# Escapes one file for one issue, for the cases where two explanatory sites are genuinely right --
# a decision record and the code it governs, say. Naming the issue is required so the escape cannot
# be pasted around as a blanket silencer.
SUPPRESS = re.compile(r"record-once-ok:\s*#(\d{3,})")

# How many files one change may newly reference an issue in.
#
# Deliberately a count of PLACES, not an attempt to tell an explanation from a pointer. Two proxies
# for that distinction were tried and both were wrong in a way that shipped: counting lines that
# mention the issue scores a two-line explanation as one, and weighting by hunk size scores a
# one-line pointer inside a three-line config block as an explanation. The second one fired on this
# file's own change, which is how it was caught.
#
# So this is coarse on purpose. It catches the shape that actually happened -- one corrected fact
# typed into five files -- and it does not catch the same fact explained twice. A checker that is
# honestly blunt beats one whose precision is imagined.
MAX_FILES = 3


def added_hunks_by_file(base: str) -> dict[str, list[list[str]]]:
    """Every contiguous block of added lines, keyed by the file it lands in.

    Hunks, not loose lines, because the unit that matters is *how much was written around the
    reference*. An explanation mentions an issue once and surrounds it with prose; a pointer is a
    single added line. Counting lines that literally contain `#NNN` gets that backwards -- it scores
    a two-line explanation as one, which is how the first version of this checker passed its own
    restatement case.

    `--unified=0` so no untouched context line is mistaken for an addition.
    """
    out = subprocess.run(
        ["git", "diff", "--unified=0", f"{base}...HEAD"],
        capture_output=True, text=True, check=True).stdout

    by_file: dict[str, list[list[str]]] = collections.defaultdict(list)
    current = None
    for line in out.splitlines():
        if line.startswith("+++ b/"):
            current = line[6:]
        elif line.startswith("@@") and current:
            by_file[current].append([])
        elif line.startswith("+") and not line.startswith("+++") and current:
            if not by_file[current]:
                by_file[current].append([])
            by_file[current][-1].append(line[1:])
    return by_file


def violations(by_file: dict[str, list[list[str]]]) -> list[str]:
    sites: dict[str, set[str]] = collections.defaultdict(set)
    for path, hunks in by_file.items():
        flat = [line for hunk in hunks for line in hunk]
        suppressed = {m.group(1) for line in flat for m in SUPPRESS.finditer(line)}
        for issue in {i for line in flat for i in ISSUE.findall(line)}:
            if issue not in suppressed:
                sites[issue].add(path)

    problems = []
    for issue, files in sorted(sites.items(), key=lambda kv: int(kv[0])):
        if len(files) > MAX_FILES:
            where = "\n".join(f"      {p}" for p in sorted(files))
            problems.append(
                f"  #{issue} is newly referenced in {len(files)} files:\n{where}\n"
                f"      Keep the explanation in one; reduce the rest to `(#{issue})`, or suppress a\n"
                f"      deliberate second site with `record-once-ok: #{issue}`.")
    return problems


def main(argv: list[str]) -> int:
    base = argv[1] if len(argv) > 1 else "origin/main"
    try:
        by_file = added_hunks_by_file(base)
    except subprocess.CalledProcessError as exc:
        # Fail closed and say which half is missing: a shallow clone cannot see the base, and a
        # checker that silently passes on an unreadable diff is the thing this file exists to stop.
        print(f"!! cannot diff against '{base}' -- {exc.stderr.strip()}", file=sys.stderr)
        print("   CI needs actions/checkout with fetch-depth: 0 for this to work.", file=sys.stderr)
        return 1

    problems = violations(by_file)
    print(f"record-once: {len(by_file)} changed file(s) against {base}")
    if not problems:
        print(" OK no issue reference is explained in more than one place")
        return 0

    print(f" !! {len(problems)} restated fact(s)\n", file=sys.stderr)
    for p in problems:
        print(p, file=sys.stderr)
    print("\n   record-once: a fact is stated once, in one canonical record; every other location"
          "\n   links to it. Suppress a deliberate second site with `record-once-ok: #<n>`.",
          file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
