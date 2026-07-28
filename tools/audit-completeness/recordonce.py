"""Fail a change that writes the same passage into more than one file (#671).

`record-once` is the gate with the worst compliance record in this repo and the only one with no
checker. It is prose enforcing prose, so it fails the way prose does: one change restated a single
corrected fact into five files, and CI was green throughout.

Operates on the DIFF, never the tree, and on PROSE, not on issue references.

**Counting references was tried first and does not work.** Measured against the merged PR this was
written for: it flagged the one real restatement (5 files) and also flagged the issue that PR
*implements* (30 files), plus two legitimate cross-references to prior art (7 and 4 files). A
reference proliferating is the register working. Worse, no threshold separates them -- the true
positive sat below the false ones.

What the defect actually looked like was one *sentence* in four files. So: normalise added prose,
shingle it, and fail when the same shingle lands in two files at once. Thirty mentions of one issue
share no shingles; one sentence written twice shares all of them. This also catches restatement that
cites no issue at all, which the reference design could not see by construction.

    pixi run audit-recordonce            # against origin/main
    pixi run audit-recordonce -- <base>  # against any other base

WHAT IT CANNOT CHECK:
  * A comment the change FALSIFIED without touching -- absent from the diff by definition. #636's.
  * The same fact PARAPHRASED. Shingles match text, not meaning.
  * Whether the surviving copy is the right one. It finds duplicates; it does not rank them.
"""
from __future__ import annotations

import collections
import re
import subprocess
import sys

# Escapes one file, for a second copy that is genuinely right -- a decision record and the code it
# governs. Naming an issue is required so it reads as a decision rather than a mute.
SUPPRESS = re.compile(r"record-once-ok:\s*#(\d{3,})")

# Long enough that ordinary phrasing does not collide by accident, short enough to catch a restated
# clause rather than only a whole restated paragraph.
SHINGLE = 9

# Comment leaders, markup and citation noise, so one sentence matches across a `///` C# comment, a
# `#` Python one and a markdown paragraph -- which is how the measured case was spread.
LEADER = re.compile(r"^\s*(///|//|/\*|\*/|\*|#+|--|<!--|-->|-|\d+\.)\s*")
MARKUP = re.compile(r"</?[a-zA-Z][^>]*>|[`*_\[\]()<>]|&\w+;")
NOISE = re.compile(r"#\d{3,}|https?://\S+")


# Prose only. Duplicated *code* across files is ordinary -- two tests legitimately open with the same
# `var grant = new PermissionGrant(...)` and the same `using var stderr = new StringWriter()`, and
# flagging those was the second false-positive class this check produced. In a code file only comment
# lines are read; markdown is prose throughout.
PROSE_EVERYWHERE = (".md",)
COMMENT = re.compile(r"^\s*(///|//|/\*|\*|#|--|<!--)")


def is_prose(path: str, line: str) -> bool:
    return path.endswith(PROSE_EVERYWHERE) or bool(COMMENT.match(line))


def normalise(line: str) -> list[str]:
    text = LEADER.sub("", line)
    text = MARKUP.sub(" ", text)
    text = NOISE.sub(" ", text)          # the issue number is not the fact
    text = re.sub(r"[^a-z0-9 ]+", " ", text.lower())
    return text.split()


def added_lines_by_file(base: str) -> dict[str, list[str]]:
    """Every line this change adds, keyed by file. `--unified=0` so no context line is counted."""
    out = subprocess.run(
        ["git", "diff", "--unified=0", f"{base}...HEAD"],
        capture_output=True, text=True, check=True).stdout

    by_file: dict[str, list[str]] = collections.defaultdict(list)
    current = None
    for line in out.splitlines():
        if line.startswith("+++ b/"):
            current = line[6:]
        elif line.startswith("+") and not line.startswith("+++") and current:
            by_file[current].append(line[1:])
    return by_file


def violations(by_file: dict[str, list[str]]) -> list[str]:
    # Each file's added text is shingled as one stream rather than per line: the measured
    # restatement wrapped mid-sentence in every file it landed in.
    where: dict[tuple[str, ...], set[str]] = collections.defaultdict(set)
    for path, lines in by_file.items():
        if any(SUPPRESS.search(line) for line in lines):
            continue
        words = [w for line in lines if is_prose(path, line) for w in normalise(line)]
        for i in range(len(words) - SHINGLE + 1):
            where[tuple(words[i:i + SHINGLE])].add(path)

    # One entry per set of files, not per shingle: a restated paragraph produces dozens of
    # overlapping shingles and printing each would bury the finding.
    by_group: dict[tuple[str, ...], list[tuple[str, ...]]] = collections.defaultdict(list)
    for shingle, files in where.items():
        if len(files) > 1:
            by_group[tuple(sorted(files))].append(shingle)

    # A restated passage spanning four files also produces a group for every pair and triple within
    # it. Reporting only the maximal sets turns two dozen entries back into the handful of passages
    # a person actually has to fix.
    maximal = [f for f in by_group
               if not any(other != f and set(f) < set(other) for other in by_group)]

    problems = []
    for files in sorted(maximal):
        shingles = by_group[files]
        sample = " ".join(sorted(shingles)[0])
        problems.append(
            f"  the same wording was added to {len(files)} files:\n"
            + "\n".join(f"      {p}" for p in files)
            + f"\n      e.g. \"{sample}\"\n"
            + "      Keep it in one; link from the rest. A deliberate second copy needs\n"
            + "      `record-once-ok: #<issue>` in the file that keeps it.")
    return problems


def main(argv: list[str]) -> int:
    base = argv[1] if len(argv) > 1 else "origin/main"
    try:
        by_file = added_lines_by_file(base)
    except subprocess.CalledProcessError as exc:
        # Fail closed and say which half is missing: a shallow clone cannot see the base, and a
        # checker that silently passes on an unreadable diff is the thing this file exists to stop.
        print(f"!! cannot diff against '{base}' -- {exc.stderr.strip()}", file=sys.stderr)
        print("   CI needs actions/checkout with fetch-depth: 0 for this to work.", file=sys.stderr)
        return 1

    problems = violations(by_file)
    print(f"record-once: {len(by_file)} changed file(s) against {base}")
    if not problems:
        print(" OK no wording was added to more than one file")
        return 0

    print(f" !! {len(problems)} restated passage(s)\n", file=sys.stderr)
    for p in problems:
        print(p, file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
