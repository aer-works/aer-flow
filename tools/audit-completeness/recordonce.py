"""Fail a change that writes the same passage into more than one file (#671).

`record-once` is the gate with the worst compliance record in this repo, and the half of it that
concerns restatement had no checker. It is prose enforcing prose, so it fails the way prose does:
one change restated a single corrected fact into five files, and CI was green throughout.

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
  * A copy of text that ALREADY EXISTS in the tree. The population is added lines, so both copies
    have to be written in the same change. Pasting a paragraph out of CLAUDE.md into a new doc is
    invisible -- which is the dominant real shape of the violation. #674.
  * Which change introduced a duplication. `git diff` emits a modified line as `+`, so touching two
    files that already shared a passage reads the same as writing it twice. #674.
  * A comment the change FALSIFIED without touching -- absent from the diff by definition. #636's.
  * The same fact PARAPHRASED. Shingles match text, not meaning: nine consecutive words have to
    match, so one substituted word inside the window defeats it.
  * Prose that carries no comment leader in a code file -- `/* */` bodies, Python docstrings, string
    literals. Only leader-led lines are read. #675.
  * Whether the surviving copy is the right one. It finds duplicates; it does not rank them.
"""
from __future__ import annotations

import collections
import re
import subprocess
import sys

# Escapes one file, for a second copy that is genuinely right -- a decision record and the code it
# governs. Naming an issue is required so it reads as a decision rather than a mute. The unit is the
# whole file for this change, which is coarser than the passage it should cover: #676.
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
# lines are read; markdown is prose apart from the exclusions below.
PROSE_EVERYWHERE = (".md",)
COMMENT = re.compile(r"^\s*(///|//|/\*|\*|#|--|<!--)")

# Text whose duplication `record-once` PRESCRIBES, and which therefore cannot be evidence against it.
#
#   * A markdown table row. The decision-index row repeats the record's own title verbatim, and
#     `docs/plan.md` repeats it again -- so adding any decision record produced a three-file group.
#     That is the register working: the record is canonical, the rows are the links to it.
#   * A fenced block inside markdown. Two runbooks showing the same `pixi run` invocation are
#     showing the same command, not restating a fact.
#   * A generated file. Its single source is a string literal in the generator, invisible here; the
#     copies are derived. Rewording the banner re-emits it into every generated file at once, and
#     those files cannot carry a suppression marker -- Aer.Architecture.Tests fails hand edits.
PATH_PREFIX = re.compile(r"^b/")
TABLE_ROW = re.compile(r"^\s*\|")
FENCE = re.compile(r"^\s*(```|~~~)")
GENERATED = re.compile(r"GENERATED FILE", re.IGNORECASE)


def prose_runs(path: str, hunks: list[list[str]]) -> list[list[str]]:
    """The added prose of one file as CONTIGUOUS runs, each a normalised word stream.

    Runs, not one stream per file, because a shingle that spans a break is evidence of a sentence
    nobody wrote. Measured, twice, before this was changed:

      * A `.py` file whose real comment `# the gate refuses a payload it cannot judge` was followed
        by an unrelated docstring line `# a hash inside a docstring` (read because `COMMENT` matches
        a leading `#` with no notion of context) produced FIVE shingles, every one of them a word
        sequence present in no line of the file.
      * Two `///` comments 400 lines apart in one `.cs` file -- two hunks, handed over adjacent with
        nothing marking the gap -- produced five more. This one needs no docstring and no Python: it
        is the ordinary shape of any change that edits two places in a file.

    Both could be reported as the `e.g. "..."` sample under a real finding, and both could make two
    files share a shingle neither of them contains. A checker whose evidence can be fabricated is
    not one anybody should act on.

    A run breaks at a hunk boundary, at a non-prose line, and at a prose line carrying no words --
    an empty `///` or a blank markdown line. That last one is a deliberate choice rather than a
    side effect: it is a paragraph break, two paragraphs are two passages, and a sentence cannot
    wrap across one. It can only ever shrink the shingle set, never invent a match.
    """
    if any(GENERATED.search(line) for hunk in hunks for line in hunk[:8]):
        return []

    markdown = path.endswith(PROSE_EVERYWHERE)
    runs: list[list[str]] = []
    for lines in hunks:
        current: list[str] = []
        fenced = False
        for line in lines:
            words: list[str] = []
            if markdown and FENCE.match(line):
                fenced = not fenced
            elif fenced or (markdown and TABLE_ROW.match(line)):
                pass
            elif markdown or COMMENT.match(line):
                words = normalise(line)

            if words:
                current.extend(words)
            elif current:
                runs.append(current)
                current = []
        if current:
            runs.append(current)
    return runs


def normalise(line: str) -> list[str]:
    text = LEADER.sub("", line)
    text = MARKUP.sub(" ", text)
    text = NOISE.sub(" ", text)          # the issue number is not the fact
    text = re.sub(r"[^a-z0-9 ]+", " ", text.lower())
    return text.split()


# A real historical change this must still fire on, pinned by SHA and by exact result (`--prove`).
#
# Fixtures are not enough and that is measured, not cautionary: two earlier designs of this checker
# passed every fixture written for them and were useless against the diff they existed to catch. The
# first counted issue references and flagged the issue its own PR implemented; the second read
# duplicated test setup as restatement. Both looked healthy in `selfcheck.py`.
#
# fc884cd is the #666 merge, which restated one corrected fact across several files.
#
# The pin is the file-sets, not how many there are. A count only ever moves in one direction:
# `SHINGLE = 3` would satisfy `>= n` while making the tool unusable, and any false positive the pin
# happened to include would become mandatory -- fixing it would break the pin. Pinning the sets
# means a change to WHICH passages are found has to be adjudicated line by line, which is the only
# reading of this list that is worth anything. Each entry below was read; none is boilerplate.
PROVEN_SHA = "fc884cd6dac19f16d803c28246e101e1c9fef493"
PROVEN_GROUPS = (
    ('docs/decisions/0004-permission-scopes.md', 'src/Aer.Adapters/IWorkerAdapter.cs'),
    ('docs/decisions/0029-the-gate-is-three-mechanisms.md', 'docs/documentation-lessons.md',
     'src/Aer.Adapters/ClaudeWorkerAdapter.cs', 'tests/Aer.Cli.Tests/HookCheckCommandTests.cs'),
    ('docs/decisions/0029-the-gate-is-three-mechanisms.md',
     'tests/Aer.Cli.Tests/HookCheckCommandTests.cs'),
    ('docs/documentation-lessons.md', 'src/Aer.Adapters/ClaudeWorkerAdapter.cs'),
    ('docs/documentation-lessons.md', 'tests/Aer.Cli.Tests/HookCheckCommandTests.cs'),
    ('docs/runbooks/live-claude-smoke.md',
     'tests/Aer.Cli.SmokeTests/LiveReadOnlyReviewerSmokeTest.cs'),
    ('docs/vendor-doc-audit.md', 'src/Aer.Cli/HookCheckCommand.cs'),
    ('src/Aer.Adapters/ClaudeWorkerAdapter.cs', 'tests/Aer.Adapters.Tests/ClaudeWorkerAdapterTests.cs'),
    ('src/Aer.Adapters/IncoherentPermissionGrantException.cs',
     'src/Aer.Adapters/WorkerBindingResolver.cs'),
    ('src/Aer.Adapters/WorkerBindingResolver.cs',
     'tests/Aer.Adapters.Tests/WorkerBindingResolverTests.cs'),
    ('src/Aer.Cli/HookCheckCommand.cs', 'src/Aer.Cli/OutboxPath.cs',
     'tests/Aer.Cli.Tests/OutboxWriteExemptionTests.cs'),
    ('src/Aer.Cli/HookCheckCommand.cs', 'tests/Aer.Cli.Tests/HookCheckCommandTests.cs'),
    ('src/Aer.Cli/HookCheckCommand.cs', 'tests/Aer.Cli.Tests/OutboxWriteExemptionTests.cs'),
    ('src/Aer.Cli/OutboxPath.cs', 'tests/Aer.Cli.Tests/OutboxWriteExemptionTests.cs'),
    ('tests/Aer.Ui.Tests/SessionAnswerWithoutOutputFileTests.cs',
     'tests/Aer.Ui.Tests/TestSupport/SessionTurnStubAdapter.cs'),
)


def prove(sha: str, expected: tuple[tuple[str, ...], ...]) -> tuple[bool, list[str]]:
    """Run against a recorded historical change and report whether it finds the same passages."""
    try:
        by_file = added_lines_by_file(f"{sha}^", head=sha)
    except subprocess.CalledProcessError as exc:
        return False, [f"cannot read {sha[:7]} -- {exc.stderr.strip()}"]

    found = {tuple(g) for g in groups(by_file)[0]}
    want = {tuple(g) for g in expected}
    if found == want:
        return True, [f"{len(found)} passage(s) in {sha[:7]}, all as pinned"]

    detail = [f"no longer finds in {sha[:7]}:  {g}" for g in sorted(want - found)]
    detail += [f"now finds in {sha[:7]}, unpinned:  {g}" for g in sorted(found - want)]
    return False, detail


def added_lines_by_file(base: str, head: str = "HEAD") -> dict[str, list[list[str]]]:
    """Every line this change adds, keyed by file and SPLIT BY HUNK.

    `--unified=0` so no context line is counted. The split matters and is not bookkeeping: two hunks
    are two places in the file, and text joined across them is text nobody wrote. See `prose_runs`.
    """
    out = subprocess.run(
        ["git", "diff", "--unified=0", f"{base}...{head}"],
        capture_output=True, text=True, check=True).stdout

    by_file: dict[str, list[list[str]]] = collections.defaultdict(list)
    current = None
    hunk: list[str] | None = None
    for line in out.splitlines():
        if line.startswith("+++"):
            # git quotes a path holding non-ASCII or shell-special characters: `+++ "b/docs/café.md"`.
            # Matching only `+++ b/` left `current` pointing at the previous file, so that file's
            # added lines were appended to a stream belonging to a different path.
            path = line[4:].strip()
            current = None if path == "/dev/null" else PATH_PREFIX.sub("", path.strip('"'), count=1)
            hunk = None
        elif line.startswith("@@"):
            hunk = None
        elif line.startswith("+") and current:
            if hunk is None:
                hunk = []
                by_file[current].append(hunk)
            hunk.append(line[1:])
    return by_file


def groups(by_file: dict[str, list[list[str]]]) -> tuple[dict[tuple[str, ...], list[tuple[str, ...]]],
                                                          list[str]]:
    """File-sets that share at least one shingle, plus the files a marker took out of the run."""
    # Shingled across each contiguous run rather than per line, because the measured restatement
    # wrapped mid-sentence in every file it landed in -- and no further than a run, because text
    # joined across a break is text nobody wrote. See `prose_runs`.
    where: dict[tuple[str, ...], set[str]] = collections.defaultdict(set)
    suppressed = []
    for path, hunks in by_file.items():
        if any(SUPPRESS.search(line) for hunk in hunks for line in hunk):
            suppressed.append(path)
            continue
        for words in prose_runs(path, hunks):
            for i in range(len(words) - SHINGLE + 1):
                where[tuple(words[i:i + SHINGLE])].add(path)

    # One entry per set of files, not per shingle: a restated paragraph produces dozens of
    # overlapping shingles and printing each would bury the finding.
    by_group: dict[tuple[str, ...], list[tuple[str, ...]]] = collections.defaultdict(list)
    for shingle, files in where.items():
        if len(files) > 1:
            by_group[tuple(sorted(files))].append(shingle)
    return by_group, sorted(suppressed)


def violations(by_file: dict[str, list[list[str]]]) -> list[str]:
    by_group, _ = groups(by_file)

    # A restated passage spanning four files also produces a group for every pair and triple within
    # it, and collapsing those turns two dozen entries back into the handful a person has to fix.
    # Collapse only when the smaller group's shingles are also the larger's: two unrelated passages
    # that happen to nest would otherwise leave one of them unprinted and undiscoverable.
    maximal = [f for f in by_group
               if not any(other != f and set(f) < set(other)
                          and set(by_group[f]) <= set(by_group[other])
                          for other in by_group)]

    problems = []
    for files in sorted(maximal):
        shingles = by_group[files]
        sample = " ".join(sorted(shingles)[0])
        problems.append(
            f"  the same wording was added to {len(files)} files:\n"
            + "\n".join(f"      {p}" for p in files)
            + f"\n      e.g. \"{sample}\"\n"
            + "      Keep it in one; link from the rest. A deliberate second copy needs\n"
            + "      `record-once-ok: #<issue>` in the file holding that copy -- which exempts\n"
            + "      the whole of that file for this change, and says so in the output.")
    return problems


def main(argv: list[str]) -> int:
    if len(argv) > 1 and argv[1] == "--prove":
        ok, detail = prove(PROVEN_SHA, PROVEN_GROUPS)
        if not ok:
            print("!! the checker no longer finds what it was built to find.", file=sys.stderr)
            for line in detail:
                print(f"   {line}", file=sys.stderr)
            print("   Adjudicate each line before repinning: a passage that stopped being found is\n"
                  "   a regression, and one newly found has to be a real restatement.",
                  file=sys.stderr)
            return 1
        print(f"record-once --prove: {detail[0]}")
        print(" OK still fires on real historical data, not only on its fixtures")
        return 0

    base = argv[1] if len(argv) > 1 else "origin/main"
    try:
        by_file = added_lines_by_file(base)
    except subprocess.CalledProcessError as exc:
        # Fail closed and say which half is missing: a shallow clone cannot see the base, and a
        # checker that silently passes on an unreadable diff is the thing this file exists to stop.
        print(f"!! cannot diff against '{base}' -- {exc.stderr.strip()}", file=sys.stderr)
        print("   CI needs actions/checkout with fetch-depth: 0 for this to work.", file=sys.stderr)
        return 1

    print(f"record-once: {len(by_file)} changed file(s) against {base}")
    if not by_file:
        # An empty population passing looks exactly like a real pass, which is the failure this
        # tool's neighbours exist to prevent. Say which one it was. On a push to `main`,
        # `origin/main...HEAD` is empty and only `--prove` carries the job.
        print(" -- nothing to compare: no file differs from the base")
        return 0

    _, suppressed = groups(by_file)
    for path in suppressed:
        print(f" -- suppressed by `record-once-ok`, not compared: {path}")

    problems = violations(by_file)
    if not problems:
        print(" OK no wording was added to more than one file")
        return 0

    print(f" !! {len(problems)} group(s) of files sharing added wording\n", file=sys.stderr)
    for p in problems:
        print(p, file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
