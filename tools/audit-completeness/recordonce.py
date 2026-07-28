"""Fail a change that explains the same issue in more than one place.

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

# At most one file may EXPLAIN an issue -- carry it on two or more added lines. Everywhere else the
# reference has to fit on one line, which is what a pointer looks like.
MAX_EXPLANATORY_SITES = 1

# And a cap on pointer sprawl, because one-line restatements in eight files is still the drift this
# exists to stop, just spelled differently.
MAX_FILES = 4


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
    sites: dict[str, dict[str, int]] = collections.defaultdict(dict)
    for path, hunks in by_file.items():
        flat = [line for hunk in hunks for line in hunk]
        suppressed = {m.group(1) for line in flat for m in SUPPRESS.finditer(line)}

        # An issue's weight in a file is the size of the largest added block that mentions it.
        widest: dict[str, int] = {}
        for hunk in hunks:
            mentioned = {i for line in hunk for i in ISSUE.findall(line)}
            for issue in mentioned:
                widest[issue] = max(widest.get(issue, 0), len(hunk))

        for issue, n in widest.items():
            if issue not in suppressed:
                sites[issue][path] = n

    problems = []
    for issue, per_file in sorted(sites.items(), key=lambda kv: int(kv[0])):
        explanatory = {p: n for p, n in per_file.items() if n >= 2}
        if len(explanatory) > MAX_EXPLANATORY_SITES:
            where = "\n".join(f"      {p} ({n} lines)" for p, n in sorted(explanatory.items()))
            problems.append(
                f"  #{issue} is explained in {len(explanatory)} files:\n{where}\n"
                f"      Keep the explanation in one of them; reduce the rest to `(#{issue})`.")
        elif len(per_file) > MAX_FILES:
            where = "\n".join(f"      {p}" for p in sorted(per_file))
            problems.append(
                f"  #{issue} is newly referenced in {len(per_file)} files:\n{where}\n"
                f"      That is restatement even at one line each.")
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
