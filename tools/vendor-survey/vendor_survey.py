"""Mirror both vendors' documentation locally and harvest the sentences that change decisions.

WHY THIS EXISTS
---------------
`docs/vendor-capabilities.md` was first built by probing binaries and help text while both vendors
publish documentation. Reading ~250 pages by hand is not repeatable, and fetching them through a
summarizing layer is lossy -- that is what produced the first (wrong) reading of claude's `defer`.

The observation this tool is built on: every finding that changed an AER decision was ONE sentence
inside a large page --

    "Bare mode skips OAuth and keychain reads."
    "Hooks load from the current working directory's .claude/ folder with no parent-directory fallback."
    "`defer` only works when Claude makes a single tool call in the turn."

-- and those sentences share a grammar: skips, only, cannot, must, requires, before v, will become.
Harvesting that class across the whole corpus gives 100% page coverage at ~1% of the bytes, so
breadth stops competing with depth for reader attention. Depth then goes only where constraints
cluster on a question AER actually has open.

USAGE
-----
    pixi run vendor-survey            # fetch (cached) + index + harvest
    pixi run vendor-survey --refetch  # force re-download, e.g. after a version bump

Outputs under `--out` (default `.vendor-survey/`, git-ignored):
    corpus/         mirrored pages, read from source with no summarizer in between
    constraints/    per-topic constraint sentences, deduped, with page:line provenance
    worklist.md     pages ranked by constraint density x open questions touched
    ledger.tsv      EVERY page with a disposition, so coverage is checkable not asserted

Run this on every vendor version bump. `VendorProbeStalenessTests` already fails when a CLI moves;
this is what makes re-establishing documentation coverage cheap rather than a fresh manual read.
"""
from __future__ import annotations

import argparse
import hashlib
import html
import io
import json
import os
import re
import subprocess
import sys
from collections import defaultdict
from datetime import datetime, timezone

CLAUDE_INDEX = "https://code.claude.com/docs/llms.txt"
AGY_SITEMAP = "https://antigravity.google/sitemap.xml"

# The Model Context Protocol specification. NOT an optional third vendor: AER ships its own MCP
# server, and the strongest gate primitive this audit measured -- `_meta` with
# `anthropic/requiresUserInteraction` -- is a VENDOR EXTENSION to this spec. Reading only Anthropic's
# description of it means never learning which parts are the protocol (and so portable, and stable
# across vendors) and which parts are one vendor's addition that can move without notice. Same
# llms.txt + per-page .md shape as claude.
MCP_INDEX = "https://modelcontextprotocol.io/llms.txt"

# Pages the `/docs/` filter would drop, and the vendor's own release notes. These are not optional
# extras: claude's `changelog` is the single densest page in the whole corpus (200+ constraints),
# and agy's equivalents sit OUTSIDE its /docs/ tree, so a docs-only sweep never sees them. `terms`
# is where the third-party-access clauses live. Release notes on GitHub are a separate surface again
# -- an open issue there documented an agy hooks bug this audit had misdiagnosed twice.
EXTRA_SOURCES = [
    ("agy__changelog.md", "https://antigravity.google/changelog", "html"),
    ("agy__terms.md", "https://antigravity.google/terms", "html"),
    ("agy__pricing.md", "https://antigravity.google/pricing", "html"),
    ("agy__product__antigravity-cli.md", "https://antigravity.google/product/antigravity-cli", "html"),
    ("agy__product__antigravity-sdk.md", "https://antigravity.google/product/antigravity-sdk", "html"),
    ("agy__github-CHANGELOG.md",
     "https://raw.githubusercontent.com/google-antigravity/antigravity-cli/main/CHANGELOG.md", "raw"),
]

# AER's OPEN questions, deliberately not generic keywords: a page matters in proportion to how much
# undecided design it touches. Update this when a decision closes or a new one opens.
#
# EVERY topic must carry BOTH vendors' vocabulary. The first version was written from claude's docs
# and, as a result, scored 31 of agy's 77 pages as NO-SIGNAL -- including `cli/commands/permissions`,
# a page whose entire subject is the permission model. agy says allowlist/denylist/asklist, "Request
# Review", "Scope Picker", run_command; claude says allow/deny/ask rules, permission mode, Bash. An
# instrument built from one vendor's words silently under-reads the other, which is precisely the
# knowledge asymmetry docs/vendor-coverage.md warns about -- baked into the tooling instead of the
# notes. When adding a term, add its counterpart.
TOPICS: dict[str, str] = {
    # claude terms first, agy terms after the || marker in each comment.
    "gate": r"\b(PreToolUse|canUseTool|permission[- ]?prompt|permissionOverrides|force_ask|"
            r"deny rule|ask rule|allow rule|exit code 2|requiresUserInteraction|approve|approval|"
            r"allowlist|denylist|asklist|Request Review|Always Proceed|toolPermission|"
            r"ask_permission|list_permissions|auto[- ]execut\w+|Scope Picker|permission rule|"
            r"permissions engine|Deny list|Allow list)\b",
    "durability": r"\b(--resume|--continue|fork-session|/branch|checkpoint|persist(ed|ence)?|"
                  r"transcript|crash|survive|reconnect|respawn|keep-workers|defer|"
                  r"conversation|trajectory|snapshot|--conversation|knowledge item)\b",
    "routing": r"\b(stream-json|--output-format|structured[- ]?output|json-schema|parent_tool_use_id|"
               r"tool_use_id|system/init|stream_event|partial-messages|injectSteps|"
               r"artifact|stepIdx|toolCall|Pydantic|schema)\b",
    "multiworker": r"\b(subagent|sub-agent|agent team|teammate|background (session|agent|task)|--bg|"
                   r"concurren\w+|invoke_subagent|send_message|spawn|"
                   r"Cascade|define_subagent|manage_subagents|parallel|fan[- ]out)\b",
    "attention": r"\b(notification|push|idle|fullyIdle|waiting|presence|PermissionRequest|"
                 r"CLAUDE_CLIENT_PRESENCE_FILE|notify|status bar|terminal bell|chime|awaiting)\b",
    "cost": r"\b(total_cost_usd|token usage|usage limit|quota|rate limit|credits?_required|credits|"
            r"cost|AI credits|baseline quota|overage)\b",
    "config": r"\b(--settings|settings\.json|hooks\.json|managed settings|precedence|"
              r"CLAUDE_CONFIG_DIR|env(ironment)? variable|--add-dir|additionalDirectories|"
              r"\.agents/|customization directory|rules|workflow|Project scope|Shared|Global|"
              r"settings file|configuration scope)\b",
    "lifecycle": r"\b(SIGTERM|exit(s|ed)? with (code|status)|timeout|terminat\w+|daemon|supervisor|"
                 r"grace period|--bare|MinimalOverhead|lock|background daemon|headless|sandbox)\b",
    "auth": r"\b(OAuth|keychain|keyring|API key|ANTHROPIC_API_KEY|GEMINI_API_KEY|apiKeyHelper|"
            r"subscription|login|credential|silent authentication|Credential Manager|sign in|logout)\b",
}

# The grammar of a design-changing sentence.
CONSTRAINT = re.compile(
    r"\b(cannot|can't|can not|must (?:be|not|come|have)|only (?:works|available|applies|the|fires)|"
    r"not supported|isn't supported|never|skips?|ignores?|does not|doesn't|"
    r"requires?|will become|deprecat\w+|before v\d|as of v\d|no longer|"
    r"unavailable|disabled|bypass\w*|silently|instead of|except)\b", re.I)

# Prose scaffolding, not a constraint on behaviour.
NOISE = re.compile(r"^(see also|for more|learn more|next steps|read the|refer to)\b", re.I)


def curl(url: str, dest: str) -> bool:
    subprocess.run(["curl", "-sL", "--max-time", "45", "-o", dest, url], check=False)
    return os.path.exists(dest) and os.path.getsize(dest) > 0


def html_to_text(raw: str) -> str:
    """agy publishes no .md variant, but its pages are server-rendered.

    Flattening to prose would lose exactly what matters -- code blocks, tables, heading structure --
    so those are preserved rather than stripped.
    """
    m = re.search(r"(?is)<main\b[^>]*>(.*?)</main>", raw)
    body = m.group(1) if m else raw
    body = re.sub(r"(?is)<(script|style|svg|noscript)\b[^>]*>.*?</\1>", " ", body)
    body = re.sub(r"(?is)<(nav|header|footer)\b[^>]*>.*?</\1>", " ", body)

    body = re.sub(r"(?is)<pre\b[^>]*>(.*?)</pre>",
                  lambda m: "\n```\n" + html.unescape(re.sub(r"(?s)<[^>]+>", "", m.group(1))).strip("\n") + "\n```\n",
                  body)
    body = re.sub(r"(?is)<code\b[^>]*>(.*?)</code>",
                  lambda m: "`" + re.sub(r"(?s)<[^>]+>", "", m.group(1)) + "`", body)
    for lvl in range(1, 7):
        body = re.sub(rf"(?is)<h{lvl}\b[^>]*>(.*?)</h{lvl}>",
                      lambda m, l=lvl: "\n\n" + "#" * l + " " + re.sub(r"(?s)<[^>]+>", "", m.group(1)).strip() + "\n",
                      body)
    body = re.sub(r"(?is)</t[dh]>\s*<t[dh][^>]*>", " | ", body)
    body = re.sub(r"(?is)<tr\b[^>]*>", "\n| ", body)
    body = re.sub(r"(?is)</tr>", " |", body)
    body = re.sub(r"(?is)<li\b[^>]*>", "\n- ", body)
    body = re.sub(r"(?is)</(p|div|section|ul|ol|table|blockquote)>", "\n", body)
    body = re.sub(r"(?is)<br\s*/?>", "\n", body)

    text = html.unescape(re.sub(r"(?s)<[^>]+>", "", body))
    text = re.sub(r"[ \t]+", " ", text)
    return re.sub(r"\n\s*\n\s*\n+", "\n\n", text).strip()


def fetch_cli_help(corpus: str, refetch: bool) -> None:
    """Harvest each CLI's own `--help`, top level and every subcommand.

    This is the only source in the survey that describes the EXACT binary installed, rather than
    whatever version the website documents. It is also the only one that cannot go stale relative
    to the thing AER actually spawns.

    It earned its place: `claude auth login` is a real subcommand that appears here, and finding it
    overturned a recorded conclusion that AER could not give a worker its own config root. The
    published docs describe `/login` as a TUI slash command; only `--help` shows the CLI form.

    Unlike the web sources this is captured, not fetched, so it is always re-run -- a CLI can
    self-update at any time and a cached copy would silently describe the wrong binary.
    """
    env = {k: v for k, v in os.environ.items() if not k.upper().startswith("CLAUDE")}

    def helptext(argv):
        try:
            p = subprocess.run(argv, capture_output=True, text=True, encoding="utf-8",
                               errors="replace", timeout=60, env=env, stdin=subprocess.DEVNULL)
            return (p.stdout or "") + (p.stderr or "")
        except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
            return ""

    for binary in ("claude", "agy"):
        top = helptext([binary, "--help"])
        if not top.strip():
            print(f"{binary} --help: binary not present, skipped")
            continue
        version = helptext([binary, "--version"]).strip().splitlines()
        version = version[0] if version else "unknown"
        # Subcommand names are the first token of each indented line in the Commands: block.
        subs, in_block = [], False
        for line in top.splitlines():
            if re.match(r"^\s*Commands:", line):
                in_block = True
                continue
            if in_block:
                if not line.strip():
                    break
                m = re.match(r"^\s+([a-z][a-z0-9-]*)", line)
                if m and m.group(1) not in subs:
                    subs.append(m.group(1))
        parts = [f"SOURCE: `{binary} --help` as captured locally\nVERSION: {version}\n",
                 f"===== {binary} --help =====\n{top}"]
        for s in subs:
            body = helptext([binary, s, "--help"])
            if body.strip():
                parts.append(f"===== {binary} {s} --help =====\n{body}")
        io.open(os.path.join(corpus, f"{binary}__cli-help.md"), "w",
                encoding="utf-8", newline="").write("\n\n".join(parts))
        print(f"{binary} --help: {len(subs)} subcommands captured ({version})")


def fetch_corpus(out: str, refetch: bool) -> None:
    corpus = os.path.join(out, "corpus")
    os.makedirs(corpus, exist_ok=True)

    index = os.path.join(out, "llms.txt")
    if refetch or not os.path.exists(index):
        curl(CLAUDE_INDEX, index)
    urls = sorted(set(re.findall(r"https://code\.claude\.com/docs/en/[a-z0-9/-]+\.md",
                                 io.open(index, encoding="utf-8", errors="replace").read())))
    print(f"claude: {len(urls)} pages")
    for u in urls:
        name = "claude__" + u.split("/docs/en/")[1].replace("/", "__")
        dest = os.path.join(corpus, name)
        if refetch or not os.path.exists(dest):
            curl(u, dest)

    mcp_index = os.path.join(out, "mcp-llms.txt")
    if refetch or not os.path.exists(mcp_index):
        curl(MCP_INDEX, mcp_index)
    mcp_urls = sorted(set(re.findall(r"https://modelcontextprotocol\.io/[a-z0-9/._-]+\.md",
                                     io.open(mcp_index, encoding="utf-8", errors="replace").read())))
    print(f"mcp: {len(mcp_urls)} pages")
    for u in mcp_urls:
        name = "mcp__" + u.split("modelcontextprotocol.io/")[1].replace("/", "__")
        dest = os.path.join(corpus, name)
        if refetch or not os.path.exists(dest):
            curl(u, dest)

    sitemap = os.path.join(out, "sitemap.xml")
    if refetch or not os.path.exists(sitemap):
        curl(AGY_SITEMAP, sitemap)
    locs = re.findall(r"<loc>([^<]+)</loc>", io.open(sitemap, encoding="utf-8", errors="replace").read())
    doc_urls = sorted({u for u in locs if "/docs/" in u})
    print(f"agy: {len(doc_urls)} pages")
    for u in doc_urls:
        name = "agy__" + u.split("/docs/")[1].replace("/", "__") + ".md"
        dest = os.path.join(corpus, name)
        if not refetch and os.path.exists(dest):
            continue
        tmp = dest + ".html"
        if curl(u, tmp):
            io.open(dest, "w", encoding="utf-8", newline="").write(
                "SOURCE: " + u + "\n\n" + html_to_text(io.open(tmp, encoding="utf-8", errors="replace").read()))
            os.remove(tmp)

    fetch_cli_help(corpus, refetch)

    print(f"extra sources: {len(EXTRA_SOURCES)}")
    for name, url, kind in EXTRA_SOURCES:
        dest = os.path.join(corpus, name)
        if not refetch and os.path.exists(dest):
            continue
        if kind == "raw":
            tmp = dest + ".tmp"
            if curl(url, tmp):
                body = io.open(tmp, encoding="utf-8", errors="replace").read()
                io.open(dest, "w", encoding="utf-8", newline="").write("SOURCE: " + url + "\n\n" + body)
                os.remove(tmp)
        else:
            tmp = dest + ".html"
            if curl(url, tmp):
                io.open(dest, "w", encoding="utf-8", newline="").write(
                    "SOURCE: " + url + "\n\n" + html_to_text(io.open(tmp, encoding="utf-8", errors="replace").read()))
                os.remove(tmp)


ISSUE_REPOS = ["google-antigravity/antigravity-cli", "anthropics/claude-code"]

# Bug reports state what the documentation will not: that a feature is broken, partial, or silently
# ignored. Two findings this audit had to be corrected on came from here, not from the docs --
# agy's hooks path misalignment, and that --permission-prompt-tool is -p-only. Searching per open
# question keeps it bounded; anthropics/claude-code alone has ~78k issues, so a full mirror is not
# the goal and would drown the corpus.
ISSUE_QUERIES = [
    "hooks", "PreToolUse", "permission prompt", "permission-prompt-tool",
    "requiresUserInteraction", "headless print mode", "subagent permission",
    "resume session", "background session", "settings.json ignored",
]


def fetch_issues(out: str, refetch: bool) -> None:
    """Search both vendors' trackers for AER's open questions and fold results into the corpus.

    Degrades to a warning if `gh` is unavailable -- the survey is still useful without it, and this
    should never be the reason a run fails.
    """
    corpus = os.path.join(out, "corpus")
    dest = os.path.join(corpus, "issues__trackers.md")
    if not refetch and os.path.exists(dest):
        return

    if subprocess.run(["gh", "--version"], capture_output=True).returncode != 0:
        print("issues: `gh` unavailable — skipping tracker search")
        return

    lines = ["SOURCE: GitHub issue trackers (searched per AER open question)", ""]
    total = 0
    for repo in ISSUE_REPOS:
        lines.append(f"\n# {repo}\n")
        for q in ISSUE_QUERIES:
            p = subprocess.run(
                ["gh", "api", f"search/issues?q=repo:{repo}+{q.replace(' ', '+')}+in:title&per_page=15",
                 "--jq", '.items[]? | "- [\\(.state)] #\\(.number) \\(.title)"'],
                capture_output=True, text=True)
            hits = [l for l in (p.stdout or "").splitlines() if l.strip()]
            if hits:
                lines.append(f"\n## query: {q}\n")
                lines.extend(hits)
                total += len(hits)
    io.open(dest, "w", encoding="utf-8", newline="").write("\n".join(lines) + "\n")
    print(f"issues: {total} titles across {len(ISSUE_REPOS)} trackers")


def report_blind_spots(out: str, pages, seen_count: int) -> None:
    """Print what this run could NOT see, every time.

    The instrument reported only what it found, so its blind spots were discovered by
    interrogation instead of being visible. Stating them on every run is the difference between
    coverage that is claimed and coverage that is characterised.
    """
    corpus = os.path.join(out, "corpus")
    topic_any = re.compile("|".join(f"(?:{p})" for p in TOPICS.values()), re.I)
    visible = invisible = 0
    for fn in os.listdir(corpus):
        for line in io.open(os.path.join(corpus, fn), encoding="utf-8", errors="replace"):
            s = line.strip()
            if len(s) < 40 or s.startswith(("```", ">")) or not topic_any.search(s):
                continue
            if CONSTRAINT.search(s):
                visible += 1
            else:
                invisible += 1

    no_signal = [p for p in pages if not p["relevance"]]
    print("\n--- blind spots (what this run could NOT see) ---")
    pct = invisible / (visible + invisible) * 100 if (visible + invisible) else 0
    print(f"  {invisible:,} lines carry topic vocabulary but state things PLAINLY (no constraint")
    print(f"    word) and are invisible to the harvest — {pct:.0f}% of topic-relevant lines.")
    print(f"    Mitigation: depth-read the {sum(1 for p in pages if p['score'] >= 10)} PENDING-DEPTH pages.")
    print(f"  {len(no_signal)} pages match NO topic vocabulary at all. If a vendor ships a concept")
    print("    AER has no word for yet, it scores zero here. Re-read this list on any redesign:")
    for p in no_signal[:8]:
        print(f"      {p['vendor']}/{p['name']}")
    print("  Not covered by this tool at all: vendor CLI logs, SDK package source, and anything")
    print("    behind auth. Those are manual surfaces. `--help` IS covered now (*__cli-help.md)")
    print("    and is the only source describing the EXACT installed binary rather than the")
    print("    version the website documents.")
    print("  Every claim here is DOCUMENTED, not verified. Running the CLI is a separate step.")


def report_drift(out: str) -> None:
    """Say which pages appeared, changed, or vanished since the last run.

    Without this the tool is a snapshot: it can tell you what the docs say today but not what
    changed, so "re-run after a vendor version bump" means re-reading everything. With a content
    hash per page, a bump becomes a short diff -- and a page that changed is exactly where a
    previously-verified claim may have quietly stopped being true.

    Most useful with --refetch, since cached pages are not re-downloaded and so cannot change.
    """
    corpus = os.path.join(out, "corpus")
    manifest_path = os.path.join(out, "manifest.json")

    current = {}
    for fn in sorted(os.listdir(corpus)):
        p = os.path.join(corpus, fn)
        if os.path.isfile(p):
            current[fn] = hashlib.sha256(io.open(p, "rb").read()).hexdigest()[:16]

    previous = {}
    if os.path.exists(manifest_path):
        try:
            previous = json.load(io.open(manifest_path, encoding="utf-8")).get("pages", {})
        except (ValueError, OSError):
            previous = {}

    added = sorted(set(current) - set(previous))
    removed = sorted(set(previous) - set(current))
    changed = sorted(k for k in set(current) & set(previous) if current[k] != previous[k])

    if not previous:
        print("\ndrift: no previous manifest — baseline recorded")
    elif not (added or removed or changed):
        print("\ndrift: none — every page byte-identical to the last run")
    else:
        print(f"\ndrift: {len(added)} added, {len(changed)} changed, {len(removed)} removed")
        for k in added:
            print(f"  + {k}")
        for k in changed:
            print(f"  ~ {k}   <-- re-read; claims verified against the old text are now unverified")
        for k in removed:
            print(f"  - {k}")

    json.dump({"generatedAt": datetime.now(timezone.utc).isoformat(), "pages": current},
              io.open(manifest_path, "w", encoding="utf-8", newline=""), indent=2, sort_keys=True)


def survey(out: str) -> None:
    corpus = os.path.join(out, "corpus")
    pages, constraints, seen = [], defaultdict(list), set()

    for fn in sorted(os.listdir(corpus)):
        path = os.path.join(corpus, fn)
        if not os.path.isfile(path):
            continue
        text = io.open(path, encoding="utf-8", errors="replace").read()
        vendor, _, name = fn.partition("__")
        name = name[:-3] if name.endswith(".md") else name

        topic_hits = {t: len(re.findall(p, text, re.I)) for t, p in TOPICS.items()}
        topic_hits = {t: n for t, n in topic_hits.items() if n}

        n_constraints = 0
        for lineno, line in enumerate(text.splitlines(), 1):
            s = line.strip()
            if not s or s.startswith("```"):
                continue

            # Table rows are NOT skipped. Both vendors state load-bearing constraints inside
            # tables -- permission-mode semantics, agy's `toolPermission` presets, managed-policy
            # limits. An earlier version dropped any line starting with "|" and lost 340
            # constraint-bearing rows, ~27% on top of what it found. Split cells into candidate
            # sentences instead, and drop the separator rows.
            if s.startswith("|"):
                if re.fullmatch(r"[|\s:-]+", s):
                    continue
                candidates = [c.strip() for c in s.strip("|").split("|")]
            else:
                candidates = [s]

            for chunk in candidates:
                if len(chunk) < 40:
                    continue
                for sent in re.split(r"(?<=[.!?])\s+", chunk):
                    sent = sent.strip()
                    if len(sent) < 40 or NOISE.match(sent) or not CONSTRAINT.search(sent):
                        continue
                    matched = [t for t, p in TOPICS.items() if re.search(p, sent, re.I)]
                    if not matched:
                        continue
                    n_constraints += 1
                    key = re.sub(r"\W+", "", sent.lower())[:120]
                    if key in seen:
                        continue
                    seen.add(key)
                    for t in matched:
                        constraints[t].append((vendor, name, lineno, sent))

        pages.append({"vendor": vendor, "name": name, "bytes": len(text), "topics": topic_hits,
                      "relevance": sum(topic_hits.values()), "constraints": n_constraints,
                      "score": n_constraints * (1 + len(topic_hits))})

    cdir = os.path.join(out, "constraints")
    os.makedirs(cdir, exist_ok=True)
    for topic, rows in constraints.items():
        with io.open(os.path.join(cdir, f"{topic}.md"), "w", encoding="utf-8", newline="") as f:
            f.write(f"# Constraint sentences — {topic} ({len(rows)})\n\n")
            for vendor, name, lineno, sent in sorted(rows):
                f.write(f"- **{vendor}/{name}**:{lineno} — {sent}\n")

    pages.sort(key=lambda p: -p["score"])
    with io.open(os.path.join(out, "worklist.md"), "w", encoding="utf-8", newline="") as f:
        f.write("# Depth worklist — constraint density x open questions touched\n\n")
        f.write("| # | vendor | page | KB | constraints | topics |\n|---|---|---|---|---|---|\n")
        for i, p in enumerate(pages[:50], 1):
            t = ", ".join(f"{k}:{v}" for k, v in sorted(p["topics"].items(), key=lambda x: -x[1])[:5])
            f.write(f"| {i} | {p['vendor']} | `{p['name']}` | {p['bytes']//1024} | {p['constraints']} | {t} |\n")

    def disposition(p):
        """What the HARVEST concluded about a page -- a recommendation, never an outcome.

        `PENDING-DEPTH` means "this page scored high enough to deserve a depth read", not "a depth
        read is outstanding". Nothing here can know whether one happened, because this script runs
        before anyone reads anything and is re-run from scratch on every version bump.

        That distinction was not written down, and it cost something: `tools/audit-completeness`
        counted a `PENDING-DEPTH` row as a page with a disposition, so it reported full coverage
        while 137 pages sat flagged. Among them was SEP-1036 (URL-mode elicitation), which changed
        decision 0029. **The read-state is computed there**, by joining this recommendation against
        whether the page is actually cited in the audit prose -- see `step2_corpus`.
        """
        return "PENDING-DEPTH" if p["score"] >= 10 else ("SCAN-ONLY" if p["relevance"] else "NO-SIGNAL")

    with io.open(os.path.join(out, "ledger.tsv"), "w", encoding="utf-8", newline="") as f:
        f.write("vendor\tpage\tbytes\tconstraints\trelevance\tdisposition\n")
        for p in pages:
            f.write(f"{p['vendor']}\t{p['name']}\t{p['bytes']}\t{p['constraints']}\t{p['relevance']}\t{disposition(p)}\n")

    print(f"\npages indexed: {len(pages)}  bytes: {sum(p['bytes'] for p in pages):,}")
    print(f"unique constraint sentences: {len(seen)}")
    for d in ("PENDING-DEPTH", "SCAN-ONLY", "NO-SIGNAL"):
        print(f"  {d}: {sum(1 for p in pages if disposition(p) == d)}")
    print("topics: " + ", ".join(f"{t}:{len(r)}" for t, r in sorted(constraints.items(), key=lambda x: -len(x[1]))))
    report_blind_spots(out, pages, len(seen))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default=".vendor-survey")
    ap.add_argument("--refetch", action="store_true", help="force re-download (use after a version bump)")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    fetch_corpus(args.out, args.refetch)
    fetch_issues(args.out, args.refetch)
    report_drift(args.out)
    survey(args.out)
    print(f"\nwrote {args.out}/{{corpus,constraints,worklist.md,ledger.tsv}}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
