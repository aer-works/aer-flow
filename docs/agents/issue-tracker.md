# Issue tracker: GitHub

Issues and specs for this repo live as GitHub issues in **`aer-works/aer-flow`**. Use the `gh` CLI.
`gh` infers the repo from `git remote -v` when run inside a clone.

## The repo's own rules come first

`CLAUDE.md` § "Git conventions" is authoritative for branches, commits, PR bodies, and the
one-issue-one-PR boundary. This file does not restate it — read it there. What follows is only what
`CLAUDE.md` does not cover: the mechanics of talking to the tracker.

## Creating an issue

**Every issue gets a project-board entry, a label, and a milestone at creation.** The board is
project number **1** in the **`aer-works`** org, titled **AER Roadmap**. PRs are *not* boarded.

```sh
gh issue create --title "fix(adapters): Capitalized description" \
  --body-file <path> --label type/bug --label layer/dispatch --milestone "M26: The room works" \
  --project "AER Roadmap"
```

`gh issue create -p/--project` takes the board's **title**, not its number (`gh issue create --help`:
"Add the issue to projects by title"), and needs the `project` OAuth scope — `gh auth refresh -s
project` if it errors.

**Never pass `--body` with an inline string.** Multi-line bodies assembled in a shell reliably
mangle backticks, `$`, and newlines. Write the body to a file with the Write tool and pass
`--body-file`. The same applies to `gh pr create` and `gh issue comment`.

Label namespaces in use: `type/*` (bug, feature, chore, ci, test, docs), `layer/*` (dispatch, flow,
store, infra, ui), `platform/*`, plus `triage/design-checked` and `next-up`. Run `gh label list`
rather than assuming — the set grows.

## Reading and updating

- **Read**: `gh issue view <number> --comments`
- **List**: `gh issue list --state open --json number,title,body,labels,comments --jq '[.[] | {number, title, body, labels: [.labels[].name], comments: [.comments[].body]}]'`
- **Comment**: `gh issue comment <number> --body-file <path>`
- **Label**: `gh issue edit <number> --add-label "..."` / `--remove-label "..."`
- **Close**: `gh issue close <number> --comment "..."` — but note issues normally close via `Closes #n`
  in a PR body, and a branch made with `gh issue develop` auto-closes its issue on *any* PR merge.

Before acting on an issue's claim ("unused", "missing", "broken"), verify it against the code. Issue
bodies are claims about the repo as it was the day they were written — see the `common-sense` gate.

## Pull requests as a triage surface

**PRs as a request surface: no.** _(Set to `yes` if this repo treats external PRs as feature
requests; `/triage` reads this flag.)_

When set to `yes`, PRs run through the same labels and states as issues, using the `gh pr`
equivalents: `gh pr view <n> --comments`, `gh pr diff <n>`, `gh pr comment`, `gh pr edit
--add-label`, `gh pr close`. List external PRs with `gh pr list --state open --json
number,title,body,labels,author,authorAssociation,comments`, keeping only `authorAssociation` of
`CONTRIBUTOR`, `FIRST_TIME_CONTRIBUTOR`, or `NONE`.

GitHub shares one number space across issues and PRs, so a bare `#42` may be either — resolve with
`gh pr view 42` and fall back to `gh issue view 42`.

## When a skill says "publish to the issue tracker"

Create a GitHub issue, with board, label, and milestone as above.

## When a skill says "fetch the relevant ticket"

Run `gh issue view <number> --comments`.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a single issue with **child** issues as tickets. Map and
children are still boarded and milestoned like any other issue.

- **Map**: an issue labelled `wayfinder:map`, holding the Notes / Decisions-so-far / Fog body.
- **Child ticket**: an issue linked to the map as a GitHub sub-issue (`gh api` on the sub-issues
  endpoint). Where sub-issues aren't enabled, add the child to a task list in the map body and put
  `Part of #<map>` at the top of the child body. Labels: `wayfinder:<type>`
  (`research`/`prototype`/`grilling`/`task`). Once claimed, assign to the driving dev.
- **Blocking**: GitHub's native issue dependencies. Add an edge with `gh api --method POST
  repos/aer-works/aer-flow/issues/<child>/dependencies/blocked_by -F issue_id=<blocker-db-id>`,
  where `<blocker-db-id>` is the blocker's numeric **database id**
  (`gh api repos/aer-works/aer-flow/issues/<n> --jq .id`, *not* the `#number` or `node_id`).
  GitHub reports open blockers in `issue_dependencies_summary.blocked_by`.
- **Frontier query**: list the map's open children, drop any with an open blocker or an assignee;
  first in map order wins.
- **Claim**: `gh issue edit <n> --add-assignee @me`.
- **Resolve**: `gh issue comment <n> --body-file <path>`, `gh issue close <n>`, then append a
  context pointer to the map's Decisions-so-far.
