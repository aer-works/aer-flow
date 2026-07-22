# Decision records

Numbered, immutable-ish records of decisions that shape the product. One decision per file.

They exist because intent was scattered across issue comments, chat transcripts, spec prose and
three competing planning documents — and scattered intent is what produced a six-destination product
where four surfaces show the same objects and none reconciles with the others.

## How they relate to everything else

| Artefact | Answers | Lives |
|---|---|---|
| **Decision record** | *why* we chose this | here, numbered, cited by the rest |
| **Journey** | *what the product promises* | `spec/`, see #312 |
| **Behavioural spec** | *what the engine does* | `spec/` |
| **Issue** | *what to change* | GitHub, cites a journey |

The spec cites decisions. Issues cite journeys. **#283 is the index that links both** — it is not
another document competing with them.

## Format

Front matter, then the record:

```
# NNNN — Title
Status: proposed | accepted | superseded by NNNN
Date: YYYY-MM-DD
```

Then: **Context** (what forced the decision, with evidence), **Decision** (what we chose, stated
plainly), **Consequences** (what this makes easy, what it makes hard, what it obliges us to do).

## Rules

- **Never edit a decision to change its meaning.** Supersede it with a new record and set the old
  one's status. The reasoning that was wrong is as useful as the reasoning that was right — three
  findings in the evaluation that produced these records were confidently wrong before they were
  checked, and knowing that is what stops them being re-derived.
- **Cite evidence, not preference.** "Chat and codebase sessions produce byte-identical bindings"
  beats "these feel redundant."
- A decision that no issue or spec section cites is either not a decision or not yet applied.

## Index

| # | Title | Status |
|---|---|---|
| [0001](0001-two-nouns-workflow-and-session.md) | Two nouns: workflow and session | accepted |
| [0002](0002-one-vocabulary.md) | One vocabulary, no translation map | accepted |
| [0003](0003-templates-collapse-to-three-shapes.md) | Templates collapse to three shapes | accepted |
| [0004](0004-permission-scopes.md) | Permissions scope by project, session and step | accepted |
| [0005](0005-seam-milestones.md) | Capability milestones alternate with seam milestones | accepted |
| [0006](0006-visual-direction-quiet.md) | Visual direction is "Quiet" | accepted |
