"""Generate the decisions index from the records themselves (#952).

`docs/decisions/README.md`'s index table used to be hand-written -- a second copy of every
record's title, kept consistent by a three-way set test while `audit-recordonce` simultaneously
required the copies to be *worded differently* so they would not read as duplication. One checker
mandated the copies, another policed them. The structural fix: the table is generated from the
records (number, title from the `# NNNN -- Title` heading, status from the `Status:` line), so
there is no second hand-written copy to keep in sync.

`completeness.py` STEP 12 runs the check half in gates and CI: a stale region fails the build and
names the regeneration command. The write half is `pixi run gen-register`.

USAGE
  python genregister.py            rewrite the generated region of docs/decisions/README.md
  python genregister.py --check    exit 1 if the region is stale or the markers are missing
"""
import os
import re
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
README = os.path.join(ROOT, "docs", "decisions", "README.md")
BEGIN = "<!-- generated: decisions-index (pixi run gen-register; edits here are overwritten) -->"
END = "<!-- /generated: decisions-index -->"

# The em dash is load-bearing: every record's H1 is `# NNNN — Title`. A record that does not parse
# fails the run rather than being skipped -- an index silently missing a record is the drift class
# this generator exists to end.
TITLE_RE = re.compile(r"^# (\d{4}) — (.+)$")
STATUS_RE = re.compile(r"^Status:\s*(.+)$", re.M)


def parse_records() -> list[tuple[str, str, str, str]]:
    """(number, filename, title, status) per record, sorted; loud on any malformed record."""
    d = os.path.join(ROOT, "docs", "decisions")
    rows = []
    for f in sorted(f for f in os.listdir(d) if re.match(r"^\d{4}-.*\.md$", f)):
        with open(os.path.join(d, f), encoding="utf-8") as fh:
            text = fh.read()
        first = text.lstrip("﻿").splitlines()[0] if text.strip() else ""
        m = TITLE_RE.match(first)
        if not m:
            raise SystemExit(f"!! {f}: first line is not `# NNNN — Title`: {first!r}")
        if m.group(1) != f[:4]:
            raise SystemExit(f"!! {f}: heading number {m.group(1)} does not match its filename")
        s = STATUS_RE.search(text)
        if not s:
            raise SystemExit(f"!! {f}: no `Status:` line")
        rows.append((m.group(1), f, m.group(2).strip(), s.group(1).strip()))
    if not rows:
        raise SystemExit("!! no decision records found -- wrong ROOT?")
    return rows


def generated_block() -> str:
    lines = [BEGIN, "", "| # | Title | Status |", "|---|---|---|"]
    lines += [f"| [{n}]({f}) | {title} | {status} |" for n, f, title, status in parse_records()]
    lines += ["", END]
    return "\n".join(lines)


def split_readme() -> tuple[str, str]:
    """The text before BEGIN and after END; loud if the markers are missing or misordered."""
    with open(README, encoding="utf-8") as fh:
        text = fh.read()
    b, e = text.find(BEGIN), text.find(END)
    if b < 0 or e < 0 or e < b:
        raise SystemExit("!! docs/decisions/README.md has no intact generated-region markers -- "
                         "restore them (see genregister.py BEGIN/END) before regenerating")
    return text[:b], text[e + len(END):]


def main(argv: list[str]) -> int:
    before, after = split_readme()
    fresh = before + generated_block() + after
    with open(README, encoding="utf-8") as fh:
        current = fh.read()
    if "--check" in argv:
        if current != fresh:
            print("!! docs/decisions/README.md's generated index is stale against the records.\n"
                  "   Regenerate it: pixi run gen-register")
            return 1
        print(f"OK generated decisions index is fresh ({len(parse_records())} records)")
        return 0
    if current == fresh:
        print("generated index already fresh; nothing written")
        return 0
    with open(README, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(fresh)
    print(f"wrote docs/decisions/README.md index ({len(parse_records())} records)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
