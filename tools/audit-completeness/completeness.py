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
  4 citations live every #NNN cited near staleness language -> that issue is not actually CLOSED
                     (needs `gh`; SKIPPED, not failed, when it is unavailable -- see step4_stale_citations)
  5 what changed   every measured finding -> an architectural implication, or "no impact" + why
  6 design         every decision on disk -> reviewed, amended, superseded, or unaffected + why
  7 milestones     every open milestone   -> re-checked against the changes
  8 build plan     every design decision  -> a sequenced piece of work (not implemented -- judgement,
                     not a join; see main())

This script recomputes what is mechanically recomputable (populations, and which members carry a
disposition) and prints what it CANNOT check, because a completeness checker that hides its own
blind spots is the thing it exists to prevent.

    pixi run audit-completeness

SCOPE
-----
**Corrected 2026-07-26.** This used to say "one-time instrument for #527, retire it if it ever
fails, do not extend it." It was run cold nine days later and failed: 11 decisions (0031-0041) and
2 vendor-verify checks (effort.claude-value-set, effort.agy-value-set) had accumulated with no
disposition, invisible because nothing was re-running the check. That is the exact failure this
project's docs/decisions/README.md gate exists to catch, caught by this tool instead, which is
direct evidence the "let it die" instruction was wrong -- a completeness check whose population
keeps growing (decisions/, vendor-verify checks) needs to keep running, not be frozen at the
population it was born with.

**It is now a standing check.** Still not wired into CI (steps 2/3/5 read local docs, cheap and
fine there, but step 1's population is a judgement call nobody should make unattended) -- run it
before a PR touching `docs/decisions/`, `docs/vendor-*.md`, or `tools/vendor-verify/verify.py`
ships, per CLAUDE.md gate 8.

Every check here still verifies that a REASON WAS WRITTEN DOWN -- never that the reason is any
good. That limit is real and stays true whether this runs once or every PR: it catches an omission,
not a wrong judgement. Extend it when a population grows (a new decision file, a new vendor-verify
check, a new step worth enumerating) -- not for open-ended rigour with no named failure behind it.
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

    # `PENDING-DEPTH` in the ledger is the HARVEST's recommendation ("worth a depth read"), not an
    # outcome -- vendor_survey.py runs before anyone reads anything. Counting it as a disposition
    # let this step report full coverage while 137 pages sat flagged, one of which (SEP-1036,
    # URL-mode elicitation) changed decision 0029. Same defect as a title column passing for a
    # reason in step 6: the check was weaker than the claim it certified.
    #
    # So the read-state is COMPUTED here, by joining the recommendation against whether the page is
    # actually cited in the audit prose. Citation is the strongest evidence available without a
    # human attestation, and it is recomputed on every run rather than recorded once and trusted.
    for d, n in sorted(dispositions.items()):
        print(f"      {d:<20} {n}")
    flagged = [r.split("\t") for r in rows if r.split("\t")[-1].strip() == "PENDING-DEPTH"]
    cited, uncited = [], []
    for p in flagged:
        (cited if page_is_cited(p[1], p[0]) else uncited).append(p)
    line("depth-flagged pages", len(flagged))
    # "carries a disposition", NOT "produced a finding". A page read and found inapplicable is
    # finished; requiring a finding would make out-of-scope pages permanently outstanding and
    # reward writing one up. The reason still has to be in the prose -- see the disposition table
    # in vendor-doc-audit.md, which is what closes this population.
    ok &= line("  ... carrying a disposition in the audit prose", len(cited), len(flagged),
               "a page with no disposition anywhere is genuinely unread")
    if uncited:
        print("\n    Depth-flagged and NOT cited anywhere -- the real outstanding population:")
        for p in sorted(uncited, key=lambda r: -int(r[4]))[:40]:
            print(f"      relevance {int(p[4]):>5}   {p[0]}/{p[1]}")
    return ok


AUDIT_PROSE = ["docs/vendor-doc-audit.md", "docs/vendor-capabilities.md", "docs/vendor-coverage.md",
               "docs/decisions/0029-the-gate-is-three-mechanisms.md",
               "docs/decisions/0030-aer-is-its-own-notifier.md",
               "docs/decisions/0015-three-kinds-of-needs-you.md"]
_prose = None


def page_is_cited(name, vendor):
    """Does the audit prose reference this mirrored page by name?

    Slugs are hierarchical (`agent-sdk__typescript` -> `agent-sdk/typescript`) and the docs cite
    them three ways: as a URL path, as `page.md:line` provenance, or as a backticked name. All
    three count. A bare English word that merely happens to match does NOT -- the leaf must appear
    with a delimiter around it, or `mcp` would match every sentence containing the word.

    A page's identity is vendor + name: `claude/mcp` and `agy/mcp` are different pages, and the
    ledger keeps the vendor in its own column. The fully-qualified form is always accepted, which
    is what makes short leaves like `mcp` citable at all.
    """
    global _prose
    if _prose is None:
        _prose = "".join(read(d) for d in AUDIT_PROSE).lower()
    # _prose is lowercased, so the patterns must be too -- ledger names carry original case
    # (`github-CHANGELOG`), and matching case-sensitively silently reported a dispositioned page
    # as unread.
    slug = name.replace("__", "/").lower()
    qualified = f"{vendor.lower()}/{slug}"
    if re.search(re.escape(qualified), _prose):
        return True
    leaf = slug.split("/")[-1]
    if len(leaf) < 4:
        # Too short to match safely on its own -- "mcp" appears in half the prose. The qualified
        # form above is the only route for these, which is how the disposition table writes them.
        return False
    return any(re.search(p, _prose) for p in
               (re.escape(slug), re.escape(name.lower()), re.escape(leaf) + r"\.md",
                r"[/`]" + re.escape(leaf) + r"[`\s)\].,:]"))


def step3_gaps():
    rule("STEP 3 -- every registered check is reachable from the backlog")
    coverage = read("docs/vendor-coverage.md")
    verify = read("tools/vendor-verify/verify.py")
    checks = re.findall(r'^@check\("([^"]+)"', verify, re.M)
    struck = len(re.findall(r"~~", coverage)) // 2
    open_rows = len(re.findall(r"\*\*open\*\*", coverage))
    line("vendor-verify checks registered", len(checks))
    line("backlog rows struck (verified/corrected)", struck)
    line("rows explicitly marked open", open_rows)

    # This function used to print those three counts, disclaim that the mapping was "verified by
    # reading", and `return True` unconditionally -- so step 3 could not fail while the ledger
    # advertised it as recomputed. Third instance of the rule this audit had already written down:
    # a checker whose passing condition is weaker than the claim it certifies is worse than none,
    # because it converts an open question into a false answer.
    #
    # The assertion: every registered check must be findable in the audit prose. A check nobody
    # can trace back to the gap it closes is an orphan -- it still runs, but no reader can tell
    # what question it answers, which is how a check survives the claim it was written for.
    prose = coverage + read("docs/vendor-doc-audit.md") + read("docs/architecture-impact.md")
    orphans = [c for c in checks if c not in prose]
    ok = line("checks traceable to a documented gap", len(checks) - len(orphans), len(checks),
              "a check no document references is an orphan")
    for c in sorted(orphans):
        print(f"      ORPHAN: {c}")
    print("\n Still NOT auto-checkable: that a struck row was struck for the RIGHT reason.")
    print(" This proves the mapping exists in both directions, never that the finding is correct.")
    return ok


# A sweep row must land on one of these words. Free prose is not a disposition: "0015 was
# considered" passes a substring test while saying nothing. Fixed vocabulary makes the sweep
# answerable -- and makes a decision that got no real look impossible to hide.
DISPOSITIONS = ("unaffected", "amended", "superseded", "rewritten")

# `Rests on` became mandatory for every decision dated on or after this day (#527, decisions/README.md).
# A hardcoded file list here is exactly the bug an independent reviewer found: the first version of
# this constant named only 0029/0030 (the two records that INTRODUCED the rule), so the checker could
# never notice a later decision -- 0027, then 0031-0041 -- shipping without one. The population is
# now derived from each file's own `Date:` header, which is the only source that can't go stale by
# omission: a new decision either has a date past the cutoff or it doesn't.
RESTS_ON_CUTOFF = (2026, 7, 25)


def decisions_requiring_rests_on():
    """Every decision file dated on or after RESTS_ON_CUTOFF, by parsing its own Date: header --
    not a maintained list. A file with no parseable date is treated as requiring it (fail loud, not
    silently exempt) rather than skipped.
    """
    d = os.path.join(ROOT, "docs", "decisions")
    files = sorted(f for f in os.listdir(d) if re.match(r"^\d{4}-", f)) if os.path.isdir(d) else []
    required = []
    for f in files:
        m = re.search(r"^Date:\s*(\d{4})-(\d{2})-(\d{2})", read(f"docs/decisions/{f}"), re.M)
        if m is None or tuple(int(x) for x in m.groups()) >= RESTS_ON_CUTOFF:
            required.append(f)
    return required


def step5_impact():
    rule("STEP 5 -- every measured check has a recorded architectural impact")
    verify = read("tools/vendor-verify/verify.py")
    checks = re.findall(r'^@check\("([^"]+)"', verify, re.M)
    impact = read("docs/architecture-impact.md")
    if not impact.strip():
        print("    !! docs/architecture-impact.md not written yet -- step 5 incomplete")
        return False
    # A check counts only if the impact doc names it AND says something after it. The row format is
    # `| `check.name` | result | impact |`, so the name must appear in a table row with two more
    # populated cells -- the same rule step 6 applies to decisions, for the same reason: a bare
    # mention is not a disposition.
    missing = []
    for c in checks:
        row = next((ln for ln in impact.splitlines()
                    if ln.strip().startswith("|") and c in ln), None)
        if row is None:
            missing.append((c, "no row"))
            continue
        cells = [x.strip() for x in row.strip().strip("|").split("|")]
        if len(cells) < 3 or len(cells[2]) < 25:
            missing.append((c, "row has no impact stated"))
    ok = line("checks with an impact row", len(checks) - len(missing), len(checks),
              "including 'no impact' + why, which is a real disposition")
    for c, why in missing:
        print(f"      MISSING: {c:<48} {why}")
    return ok


def step8_cited_checks_exist():
    """Every vendor-verify check name cited anywhere in the tree is actually registered.

    STEP 5 runs registered -> documented. This runs the inverse, citation -> registered, and the
    inverse is the one that had never been checked.

    Paid for by #554: a doc comment cited `agy.add-dir-grants-files-not-config` three times as the
    measurement the agy permission gate rested on. No such check exists. The real neighbouring check
    (`gate.add-dir-loads-no-config`) is claude-scoped and states the OPPOSITE. Nothing caught it --
    not the build, not the tests, not this script, not the author re-reading his own diff. An
    independent reviewer found it by running a grep nobody had thought to automate.

    A fabricated citation is worse than an uncited claim: it reads as evidence, it survives review by
    looking exactly like the real names around it, and the next person to trust it inherits a
    conclusion that was never measured. Note the ordering that makes this non-optional -- CLAUDE.md
    gate 1 had ALREADY been extended that same day with "run `verify.py --list` before claiming a
    vendor fact is unmeasured", by the same author who then fabricated the name hours later. Prose
    did not hold. This is the population gate 8 describes as earning a checker: enumerable, and
    invisible when omitted.
    """
    rule("STEP 8 -- every cited vendor-verify check name actually exists")
    verify = read("tools/vendor-verify/verify.py")
    registered = set(re.findall(r'^@check\("([^"]+)"', verify, re.M))
    if not registered:
        print("    !! no @check registrations found -- cannot judge citations")
        return False

    # Derive the prefixes from the registrations rather than hardcoding them, so a new check group
    # is covered the day it is added rather than the day someone remembers to update this list.
    prefixes = sorted({name.split(".", 1)[0] for name in registered if "." in name})
    # Every real check name carries at least one hyphen in its suffix; requiring that keeps prose
    # like "the agy.hook family" and identifiers like `System.Text` out of the population.
    pattern = re.compile(
        r"\b(?:" + "|".join(re.escape(p) for p in prefixes) + r")\.[a-z0-9]+(?:-[a-z0-9]+)+\b")

    # Names deliberately written down BECAUSE they do not resolve -- prose describing the #554
    # fabrication. Kept as an explicit list with a reason rather than a looser regex, so the escape
    # hatch is enumerable too: anything added here is a claim that a name is meant to dangle, and a
    # reader can check that claim. Never add a name here to silence a real citation.
    INTENTIONALLY_UNRESOLVED = {
        "agy.add-dir-grants-files-not-config":
            "the #554 fabrication itself, named in the prose that records it",
    }

    roots = ["src", "tools", "docs", "tests", "spec", "CLAUDE.md"]
    exts = {".cs", ".py", ".md", ".json", ".ps1", ".sh"}
    bad = {}
    for root in roots:
        if os.path.isfile(root):
            paths = [root]
        else:
            paths = [os.path.join(d, f)
                     for d, _, fs in os.walk(root) for f in fs
                     if os.path.splitext(f)[1] in exts
                     and "bin" not in d.split(os.sep) and "obj" not in d.split(os.sep)]
        for path in paths:
            for cited in set(pattern.findall(read(path))):
                if cited not in registered and cited not in INTENTIONALLY_UNRESOLVED:
                    bad.setdefault(cited, []).append(path.replace("\\", "/"))

    ok = line("cited check names that resolve", "all" if not bad else f"{len(bad)} DO NOT",
              "all", "a citation naming nothing reads as evidence and is not")
    for cited, where in sorted(bad.items()):
        print(f"      NOT REGISTERED: {cited}")
        for w in sorted(where)[:4]:
            print(f"          cited in {w}")
    return ok


def step7_milestones():
    rule("STEP 7 -- the milestone approach re-verified against the audit")
    plan = read("docs/plan.md")
    marker = "What the vendor audit (#527) changes about this sequence"
    ok = line("plan.md carries the audit's milestone amendment",
              "yes" if marker in plan else "NO", "yes")
    # The amendment is only real if it names milestones. A section that says "nothing changed"
    # without naming what it checked is the assertion this whole exercise exists to replace.
    named = [m for m in ("M26", "M27", "M28") if plan.count(m) and marker in plan]
    ok &= line("milestones named in the amendment", len(named), 3)
    return ok


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
    required = decisions_requiring_rests_on()
    missing_rests_on = [f for f in required
                        if not re.search(r"^## Rests on", read(f"docs/decisions/{f}"), re.M)]
    ok &= line("decisions dated >= 2026-07-25 carrying `Rests on`",
              len(required) - len(missing_rests_on), len(required),
              "population derived from each file's own Date: header, not a maintained list")
    for f in missing_rests_on:
        print(f"      MISSING `Rests on`: {f}")
    return ok


# Multi-word phrases only, and matched with word boundaries -- a first version used bare "open" and
# "unknown" and got 43 hits, nearly all false: "reopened", "opened", milestone-history.md's routine
# "M25 (closed)... which opened..." prose. A single word is too common in ordinary English to signal
# staleness; these phrases are specific enough that a false hit is itself worth a look.
STALENESS_PHRASES = (
    "still open", "still unknown", "remains open", "not yet landed", "not yet resolved",
    "not yet probed", "unprobed", "highest-value open", "no issue owns", "TODO",
)
# Excluded: archive/ is explicitly superseded material (docs/archive/README.md); milestone-history.md
# is explicitly provenance ("what a past milestone did," README.md) and routinely narrates closed
# issues in prose that legitimately contains words like "open" -- neither is drift.
CITATION_DIRS = ("docs", "spec")
CITATION_EXCLUDE = ("docs/archive/", "docs/decisions/README.md", "docs/milestone-history.md")
ISSUE_RE = re.compile(r"#(\d{2,5})\b")


def step4_stale_citations():
    rule("STEP 4 -- no doc cites a closed issue as though it were still open")
    gh = _shutil_which("gh")
    if gh is None:
        print("    SKIPPED -- `gh` not on PATH. This step needs it; it does not fail without it.")
        return None
    try:
        out = subprocess.run(
            ["gh", "issue", "list", "--repo", "aer-works/aer-flow", "--state", "all",
             "--limit", "1000", "--json", "number,state"],
            capture_output=True, text=True, cwd=ROOT, timeout=30)
    except (OSError, subprocess.TimeoutExpired):
        print("    SKIPPED -- `gh` did not respond (offline, or not authenticated).")
        return None
    if out.returncode != 0:
        print(f"    SKIPPED -- `gh issue list` failed: {out.stderr.strip()[:200]}")
        return None
    import json
    try:
        issues = {i["number"]: i["state"] for i in json.loads(out.stdout)}
    except (ValueError, KeyError):
        print("    SKIPPED -- could not parse `gh issue list` output.")
        return None
    if not issues:
        print("    SKIPPED -- `gh` returned zero issues; treating as not-actually-queryable.")
        return None

    findings = []
    for base in CITATION_DIRS:
        for dirpath, _, filenames in os.walk(os.path.join(ROOT, base)):
            for fn in filenames:
                if not fn.endswith(".md"):
                    continue
                rel = os.path.relpath(os.path.join(dirpath, fn), ROOT).replace("\\", "/")
                if any(rel.startswith(x) or rel == x for x in CITATION_EXCLUDE):
                    continue
                for lineno, text in enumerate(read(rel).splitlines(), start=1):
                    lowered = text.lower()
                    if not any(re.search(r"\b" + re.escape(w) + r"\b", lowered)
                               for w in STALENESS_PHRASES):
                        continue
                    for m in ISSUE_RE.finditer(text):
                        n = int(m.group(1))
                        if issues.get(n) == "CLOSED":
                            findings.append((rel, lineno, n, text.strip()[:100]))

    ok = line("closed issues cited as open/unresolved", len(findings), 0,
              "each is a doc that has not caught up with GitHub")
    for rel, lineno, n, snippet in findings:
        print(f"      {rel}:{lineno}  cites #{n} (CLOSED)  -- {snippet}")
    return ok


def _shutil_which(name):
    import shutil
    return shutil.which(name)


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
    results = [step1_sources(), step2_corpus(), step3_gaps(), step4_stale_citations(),
               step5_impact(), step6_decisions(), step7_milestones(), step8_cited_checks_exist()]
    git_state()
    rule("WHAT THIS SCRIPT CANNOT CHECK")
    for x in [
        "That a finding's architectural implication is CORRECT -- only that one was recorded.",
        "Step 8's population is the build plan, whose completeness is a judgement, not a join.",
        "That the vendor-verify checks still pass -- run `pixi run vendor-verify` for that.",
        "Whether a source nobody thought of exists. Enumeration cannot find its own blind spot.",
        "Whether a 'no impact' or 'unaffected' call was the RIGHT call. It checks that a reason",
        "  was given, never that the reason is good.",
        "Step 4 only catches a citation near a staleness WORD -- a doc that calls a closed issue",
        "  \"resolved\" while still describing the old, wrong behaviour reads clean to this check.",
    ]:
        print(f"    - {x}")
    print()
    # step4 returns None when `gh` is unavailable -- that is "not checked", not "passed", so it
    # is excluded from the pass/fail roll-up rather than counted either way.
    checked = [r for r in results if r is not None]
    return 0 if all(checked) else 1


if __name__ == "__main__":
    sys.exit(main())
