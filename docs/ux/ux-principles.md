# UX Principles

M19 Phase 1 (#186). These principles govern every surface Phases 2–5 build. They constrain
presentation only — the engine's behavior, the durable contracts, and every §6 prohibition are
untouched by anything here. Where a principle cites a spec section, that citation is for the
implementer; the whole point of several of these principles is that the *user* never sees one.

## 1. Task-first, plain language everywhere

The user thinks in work, not in machinery: "my design review", "waiting on me", "send it back".
Every user-facing string speaks that language. Spec vocabulary (`supersede`, `PausePoint`,
`snapshot`, section numbers) never appears as primary UI text — it survives in tooltips and
disclosure surfaces, so §12's transparency obligation (the UI must be explainable in the spec's
terms) is met without making the spec the interface.

### The vocabulary map

Authoritative for Phases 2–5. Left column: the engine/spec term (what tooltips, logs, and tests
say). Right column: what the human reads.

| Engine / spec term | User-facing language |
|---|---|
| `PausePoint` | **Review gate** ("This step pauses for your review") |
| Paused (awaiting decision) | **Waiting for your review** |
| `Resume` decision | **Approve** (continue the workflow as-is) |
| `RetryWithRevision` | **Retry this step** — "with changes" when a feedback file is attached |
| `Supersede` | **Send back** ("Send back to *architect* with feedback") |
| `SupersedeTargets` | **Steps you can send back to** |
| Supplementary artifact / `supply` | **Feedback file** (attached to a send-back or retry) |
| Supplementary execution | **A revised attempt** (the re-run caused by a send-back) |
| Execution / attempt | **Attempt** ("Attempt 2 of *critic*") |
| Artifact | **Output** / **file** (named by its actual filename: "critique") |
| Artifact lineage | **History** ("earlier versions") |
| Terminal (`Succeeded`/`Failed`) | **Finished** / **Failed** |
| Staleness | **Out of date** ("produced before *architect* was revised") |
| Snapshot / Event Store | Never surfaced; collectively "**this task**" |
| Workflow template | **Workflow** / "**the plan**" |
| Worker bindings | "**Who runs each step**" |
| Worker / adapter | **Runner** — normally named by vendor ("Claude", "Gemini") |
| `PermissionScope` | **What this step may do** ("run commands, read and write files") |
| Instantiate | **Start** |
| Task directory | **Task** (its path is detail, one disclosure away) |

Two rules keep the map honest:

* **The map is total for primary text.** A spec term appearing in a label, button, status line,
  or empty state is a defect (Phase 6's review checklist includes greping the new views for the
  left column).
* **The map never renames semantics.** "Send back" *is* supersede — same mandatory feedback
  artifact (§ "Act on a paused workflow"), same target constraints, same recorded decision. If a
  plain word would imply behavior the engine doesn't have, the plain word is wrong, not the
  engine.

## 2. Progressive disclosure

Advanced detail exists, is honest, and is never the entry path. Each surface leads with the one
thing the user needs (the artifact to review, the plain status, the primary action) and puts the
precise machinery — exact engine state, execution ids, absolute paths, the spec-term tooltip —
one deliberate step away (expander, "details" pane, tooltip). Nothing is hidden in the sense of
unavailable; §12 transparency means the full truth is always reachable, just not mandatory.

Defaults follow the same rule: the send-back's worker/output names, the bindings pre-fill, the
task directory location all get sensible defaults with the override visible behind disclosure —
prefilled, never locked (a pre-filled picker is convenience, not remembered authority; §4).

## 3. Ids are handles, never strings a human copies

Execution ids, step names as keys, absolute paths: the UI carries them on controls (M15's
established pattern) and the user clicks the thing, never transcribes it. Tasks reopen from Home
cards; artifacts open from the row that names them; a decision acts on the step it's rendered
on. Any flow that requires copying an identifier out of one surface into another is a defect.

## 4. The screen is organized by what needs the human next

CyboFlow's structural insight, and the reason the decision inbox is Home's centerpiece: paused
steps *are* the product's unit of attention. Whatever else a view shows, work that is waiting on
the human outranks work that is running, which outranks work that is finished. Review surfaces
sit one click from the decision they inform — the inbox item leads with the artifact to review,
not with navigation.

## 5. Keyboard-first, mouse-complete

The Raycast/Linear bar (design-language.md): every primary flow is drivable from the keyboard —
navigate the inbox, open a task, approve/send back, move through steps — with visible shortcut
hints, while remaining fully mouse-usable. Phases 2–4 wire real keyboard handling as surfaces
land (not a Phase 5 retrofit); Phase 5 polishes the affordances.

## 6. Honest states, including empty and failed ones

Every view has a designed empty state that says what to do next, not a blank panel. Failure is
forensic data, never dressed up: a failed attempt shows what it produced before failing (M18's
partial-transcript stance, generalized). Loading is acknowledged (skeleton/progress per
design-language.md), never a frozen frame.

## 7. The UI never asks for expertise the preset already has

Wherever the walkthrough demanded knowledge (non-expert-audit.md's twelve rows), the knowledge
moves into the product — vendor presets carry permission scopes and prompt-template branches,
validation explains itself inline, paths resolve internally. The user contributes intent
("Claude drafts, Gemini critiques, I review"); the product contributes the mechanics.
