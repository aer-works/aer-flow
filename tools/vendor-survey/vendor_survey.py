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
import html
import io
import os
import re
import subprocess
import sys
from collections import defaultdict

CLAUDE_INDEX = "https://code.claude.com/docs/llms.txt"
AGY_SITEMAP = "https://antigravity.google/sitemap.xml"

# AER's OPEN questions, deliberately not generic keywords: a page matters in proportion to how much
# undecided design it touches. Update this when a decision closes or a new one opens.
TOPICS: dict[str, str] = {
    "gate": r"\b(PreToolUse|canUseTool|permission[- ]?prompt|permissionOverrides|force_ask|"
            r"deny rule|ask rule|allow rule|exit code 2|requiresUserInteraction|approve|approval)\b",
    "durability": r"\b(--resume|--continue|fork-session|/branch|checkpoint|persist(ed|ence)?|"
                  r"transcript|crash|survive|reconnect|respawn|keep-workers|defer)\b",
    "routing": r"\b(stream-json|--output-format|structured[- ]?output|json-schema|parent_tool_use_id|"
               r"tool_use_id|system/init|stream_event|partial-messages|injectSteps)\b",
    "multiworker": r"\b(subagent|sub-agent|agent team|teammate|background (session|agent|task)|--bg|"
                   r"concurren\w+|invoke_subagent|send_message|spawn)\b",
    "attention": r"\b(notification|push|idle|fullyIdle|waiting|presence|PermissionRequest|"
                 r"CLAUDE_CLIENT_PRESENCE_FILE|notify)\b",
    "cost": r"\b(total_cost_usd|token usage|usage limit|quota|rate limit|credits?_required|credits|cost)\b",
    "config": r"\b(--settings|settings\.json|hooks\.json|managed settings|precedence|"
              r"CLAUDE_CONFIG_DIR|env(ironment)? variable|--add-dir|additionalDirectories)\b",
    "lifecycle": r"\b(SIGTERM|exit(s|ed)? with (code|status)|timeout|terminat\w+|daemon|supervisor|"
                 r"grace period|--bare|MinimalOverhead|lock)\b",
    "auth": r"\b(OAuth|keychain|keyring|API key|ANTHROPIC_API_KEY|GEMINI_API_KEY|apiKeyHelper|"
            r"subscription|login|credential)\b",
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


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default=".vendor-survey")
    ap.add_argument("--refetch", action="store_true", help="force re-download (use after a version bump)")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    fetch_corpus(args.out, args.refetch)
    survey(args.out)
    print(f"\nwrote {args.out}/{{corpus,constraints,worklist.md,ledger.tsv}}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
