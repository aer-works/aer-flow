# Runbook — flipping the repo from `aer-works/aer-flow` to `aer-works/baton`

This is the runbook for the GitHub-side flip itself. #823 prepared every live reference in the
tree for it (see that issue and
[0045](../decisions/0045-the-product-is-baton-the-journal-is-the-ledger.md) for why); this
document is only the ordered steps for the rename and what to check after. It is the operator's
action, not an agent's — run it from a machine with `gh` authenticated and push access to the
repo.

## Before you run it

- Confirm #823 (or its successor PR) has merged to `main`. Every live reference in the tree
  should already read `aer-works/baton` — if `git grep -n "aer-works/aer-flow" -- ':!CHANGELOG.md'
  ':!**/CHANGELOG.md' ':!docs/archive' ':!patches' ':!scripts/patch-tailscale-dart.sh'
  ':!docs/decisions/00*' ':!tools/audit-completeness/completeness.py'` still returns live hits,
  stop and find out why before renaming out from under them. (`completeness.py` is expected to
  show up here until step 2 below — see why in that section.)
- Note every local clone of this repo on this machine. Worktrees added with `git worktree add`
  (this layout has several — `aer-flow`, `aer-flow-w799`, `aer-flow-w823`, `aer-flow-w833`, plus
  any agent worktrees under `.claude/worktrees/`) all point at **one shared `.git` directory**, and
  `git remote` configuration lives in that shared directory, not per-worktree. **One `git remote
  set-url` fixes all of them at once** — you do not need to repeat it per worktree, and repeating
  it is harmless (last write wins).

## The flip

**Step 1 — the rename itself, on GitHub's side:**

```sh
gh repo rename baton --repo aer-works/aer-flow
```

`gh` will ask for confirmation once; there is no dry-run.

**Step 2 — flip the one hardcoded reference that had to stay on the old name until now.**
`tools/audit-completeness/completeness.py`'s STEP 4 (`step4_stale_citations`) calls `gh issue
list --repo aer-works/aer-flow ...` to check docs don't cite closed issues as open. #823 could not
point this at `aer-works/baton` in advance the way it did every markdown link, because this call
is live: `gh issue list` is GraphQL-backed, and GraphQL resolves the *current* repo name rather
than following the REST rename redirect markdown links get for free. Pointed at the not-yet-existing
name, the call fails and the `except`/`returncode != 0` branch prints `SKIPPED` — which `main()`
excludes from the pass/fail rollup, so the check goes silently inert instead of failing loud. Now
that the rename has happened, flip it:

```sh
# in tools/audit-completeness/completeness.py, step4_stale_citations():
#   "--repo", "aer-works/aer-flow"  ->  "--repo", "aer-works/baton"
```

Commit that one-line change (conventional commit, e.g. `chore(tools): Point completeness.py's
stale-citation check at the renamed repo`) as part of this same flip, not as a follow-up — every
run of `pixi run gates` between the rename and that commit has STEP 4 silently skipping.

## What GitHub carries automatically — no action needed

- **The web URL.** `https://github.com/aer-works/aer-flow` permanently redirects to
  `https://github.com/aer-works/baton` (GitHub's documented rename redirect, indefinite).
- **Git clone/fetch/push URLs.** `https://github.com/aer-works/aer-flow.git` continues to resolve
  and push correctly against the renamed repo — this is *why* local `origin` URLs are not
  functionally broken by the rename, only stale (see below for why to fix them anyway).
- **Cross-repo `owner/repo#n` references and issue/PR permalinks**, including every historical
  citation this repo carries in CHANGELOGs, decision records (`docs/decisions/00*.md`), archived
  design docs (`docs/design/`), and the tailscale patch/script that cite
  `aer-works/aer-flow#303` — all of these resolve through the redirect. That is the reason #823
  left them untouched: rewriting them to match a later name is exactly what the "provenance, never
  authority" rule (`docs/plan.md`, milestone-history convention) forbids, and it is not needed for
  correctness either way.
- **Branch protection rules, issue/PR history, labels, milestones, Actions run history, and
  webhooks** attached to the repo — GitHub carries these across a rename in place. Verify anyway
  (see below); "GitHub says it preserves this" is not the same as "confirmed after this specific
  rename."

## What GitHub does NOT carry — action needed

- **This machine's local `origin` remote.** `git remote -v` still shows the old URL after the
  flip. It will keep working (redirects cover it), but it should be corrected rather than left
  stale indefinitely. From any one worktree of this clone:

  ```sh
  git remote set-url origin https://github.com/aer-works/baton.git
  git remote -v   # confirm both fetch and push now show .../baton.git
  ```

  Because remotes live in the shared `.git` directory, this needs to run **once per clone**, not
  once per worktree. If there are multiple independent clones on this or other machines (a CI
  runner's checkout, a teammate's laptop, a second `git clone` elsewhere), each is a separate
  clone and needs its own `set-url`.
- **Anything outside GitHub's own graph**: browser bookmarks, chat links, external dashboards,
  README badges pointing at absolute URLs on third-party services, CI secrets or webhook
  configuration stored *outside* GitHub (e.g. a Slack app's saved webhook target) that hardcoded
  the old path. GitHub's redirect only covers requests that reach `github.com`; anything with the
  old owner/repo baked into a non-GitHub system needs a manual update.
- **Anything that pins the repo path as a literal string used in a live call, rather than a
  redirect-covered link.** Two are known: the sidecar's `go.mod` module path (renamed in #823,
  since it's a local path with no network resolution, so nothing would have caught a stale one),
  and `tools/audit-completeness/completeness.py`'s STEP 4, which stayed on the old name until
  step 2 above specifically because it's a live `gh` call that a redirect does not rescue. Both
  needed a real code change, not just a doc edit — check for others with the same shape (a literal
  `aer-works/...` string passed to something that executes, rather than one a person reads) before
  assuming the flip is complete.

## Verification, after

Run these from a worktree of the local clone, after `git remote set-url`:

```sh
git remote -v                                   # both origin URLs show .../baton.git
git fetch origin                                # succeeds against the new URL
gh repo view aer-works/baton --json name,url     # confirms the rename landed and gh resolves it
gh api repos/aer-works/baton/branches/main/protection --jq .required_status_checks   # protection intact
gh issue view 823 --repo aer-works/baton          # old-numbered issues resolve under the new path
```

Also spot-check that the old path still redirects rather than 404ing, since that is the property
every untouched historical reference in this repo depends on:

```sh
gh api repos/aer-works/aer-flow --jq .full_name   # expect: "aer-works/baton" (gh follows the redirect)
```

Finally, confirm step 2 above actually landed:

```sh
pixi run audit-completeness   # STEP 4 must NOT print "SKIPPED" — that means it's still on the old name
```

Whether `gh issue list --repo <old-name>` itself would have kept working after the flip (i.e.
whether GraphQL follows the rename the way the REST endpoint above does) was not tested from
inside #823 — there is no renamed repo to test it against yet. This check is what catches it
either way: if step 2 was skipped or wrong, STEP 4 SKIPs and this command's output says so.

If any of these fail, do not re-run the rename — `gh repo rename` is not idempotent against a
repo that's already been renamed once (the source name `aer-works/aer-flow` no longer exists to
rename). Diagnose the specific failure (auth scope, propagation delay, protection rule that
genuinely didn't carry) instead.

## Last: close out #823

#823 itself stays open through all of the above by design (see its description) — it is the
tracking issue for the flip, not for the prep work. Close it once the verification commands above
all pass.
