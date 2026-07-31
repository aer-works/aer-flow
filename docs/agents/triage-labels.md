# Triage labels

The label strings `/triage` applies for the five canonical triage roles. Defaults kept as-is —
`triage/SKILL.md` hardcodes these same strings, so the workflow holds even if this file is never
read.

| Role               | Label in this repo | Meaning                                       |
| ------------------ | ------------------ | --------------------------------------------- |
| `needs-triage`     | `needs-triage`     | Maintainer needs to evaluate this issue       |
| `needs-info`       | `needs-info`       | Waiting on the reporter for missing detail    |
| `ready-for-agent`  | `ready-for-agent`  | Fully specified, ready for an AFK agent       |
| `ready-for-human`  | `ready-for-human`  | Needs a human — judgment, access, or a merge  |
| `wontfix`          | `wontfix`          | Declined; closed without action               |

State transitions: unlabelled → `needs-triage` → one of `needs-info`, `ready-for-agent`,
`ready-for-human`, `wontfix`. `needs-info` returns to `needs-triage` once the reporter replies.

## These labels do not exist on GitHub yet

`gh label list` on `aer-works/baton` shows `type/*`, `layer/*`, `platform/*`,
`triage/design-checked`, and `next-up` — none of the five above. `gh issue edit --add-label` fails
on a label that does not exist, so the first `/triage` run must create them:

```sh
gh label create needs-triage    --description "Maintainer needs to evaluate this issue"
gh label create needs-info      --description "Waiting on the reporter for missing detail"
gh label create ready-for-agent --description "Fully specified, ready for an AFK agent"
gh label create ready-for-human --description "Needs a human — judgment, access, or a merge"
gh label create wontfix         --description "Declined; closed without action"
```

Note these five are deliberately *un*-namespaced, unlike every other label in the repo. The
namespaced alternative (`triage/needs-triage`, …) was considered and rejected: it only works when
the model follows the pointer from `CLAUDE.md` to this table, and applying a label that does not
exist fails loudly at the wrong moment. `triage/design-checked` is unrelated — it records scope
verified against the design corpus, not a triage state.
