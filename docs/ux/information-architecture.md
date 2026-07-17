# Information Architecture

M19 Phase 1 (#186). The structure Phases 2–4 build: three views under one navigation shell,
replacing today's single window of stacked sections. Everything the UI does today survives —
this document says *where each capability lives*, not what it does; behavior and the durable
contracts are unchanged (the plan's first shaping fact). All user-facing wording follows
`ux-principles.md`'s vocabulary map.

## The shape

```
Shell (window chrome, navigation, theme)
├── Home        — task cards + the decision inbox; where a session starts
├── Task        — one open task: DAG primary, per-step drill-in; where work is followed and decided
└── Author      — create/edit a workflow and who runs it; where a workflow is born
```

Navigation is flat: three top-level destinations, always reachable, current one evident.
Task is parameterized by the open task (opened from a Home card or by picking a folder —
picking is the fallback, cards are the path). Author is reachable both bare ("New workflow")
and from a task ("edit this workflow" — which authors a new version, per the template
version-increment decision of record).

## Home

The screen that answers "what needs me?" in one glance — the CyboFlow-inspired centerpiece.

* **Decision inbox** (top): every step across recent tasks waiting for the human's review, one
  item per paused step. An item leads with the thing to review — the artifact preview
  (non-expert-audit rows 7, 12) — beside plain-language actions (Approve / Retry / Send back).
  Acting from the inbox is the same mutation path as acting anywhere else; the inbox is a
  projection over recent task directories, rebuilt from durable contents (§3.1, §11), with its
  scan scope a Phase 2 open question (open task only vs. all recents). Empty state: "Nothing is
  waiting on you", with running/finished counts so empty doesn't read as broken.
* **Task cards**: recent task directories (today's Recents list — remains Local UI
  Configuration) as live status cards: workflow name, plain status ("Running — *critic* is
  working", "Waiting for your review", "Finished"), last activity. Click opens Task view. A
  card whose directory is gone is pruned/greyed per §3's stale-list rule, never an error.
* **Start actions**: "New workflow" (→ Author) and "Open a task…" (folder picker fallback).

## Task view

One task, the DAG as the primary surface (today's StepsPanel becomes real rendering; n8n is
the reference for node/edge styling). Steps carry the one status→color/icon system
(design-language.md). Live-follow on by default — the view tracks engine progress without a
manual refresh ritual.

* **Selecting a step** opens its drill-in panel: everything that today sprawls as separate
  stacked sections, scoped to that step —
  * **Attempts** (execution history for the step; today's HistoryPanel + SupplementaryPanel)
  * **Outputs** (artifacts + preview + history/lineage; today's LineagePanel + ArtifactPreviewBox)
  * **Conversation** (M18's view as a tab, behavior unchanged; today's ConversationPanel)
  * **Decisions** (recorded decisions touching the step; today's DecisionsPanel)
* **A paused step** shows its decision actions inline in plain words — Approve, Retry, Send
  back to *X* — with the feedback file a picker, defaults prefilled, precise names behind
  disclosure (principles §2). Same M15 round trips underneath.
* **Task-level surfaces**: run/stop (today's Run/Stop row — Run only where the task's state
  allows it), plain overall status with the exact engine state one disclosure away, and
  "Compare to workflow version" (today's DiffPanel) under a task-level details drill-in rather
  than a pasted path — the compare target is picked, not typed.

## Author view

Where "never hand-edit a config file" is delivered (Phase 4; Dagster Launchpad and Stately's
visual↔config sync are the references). One flow authors both durable files — the workflow
("the plan"; today's template editor) and who runs each step (today's bindings editor) —
form-first with the DAG preview in lockstep, validation inline (today's check-against-template
becomes always-on guidance), vendor presets carrying permission scopes and prompt-template
knowledge (non-expert-audit rows 3–5), and the dialogue step a first-class step type writing
its sidecar per the §4 amendment. Ends in **Run**: author → start without leaving the flow,
which lands the user in Task view watching the run they just started.

Files remain the durable truth and their locations remain visible (behind disclosure) — the
Author view is an editor over them, not a replacement for them.

## The re-home map (Phase 2's checklist)

Every existing surface, its new home, one migration each — behavior-preserving, all round
trips re-pointed:

| Today (stacked section / control) | New home |
|---|---|
| TaskDirectoryPathBox + Open/Refresh | Home "Open a task…" fallback; refresh becomes live-follow + explicit refresh in Task |
| RecentsPanel | Home task cards |
| Run/Stop row + RunStatusText | Task view (task-level bar); "Run" also the end of Author's flow |
| Template editor (path box, New/Open/Save, AddStep, status) | Author view |
| Bindings editor (path box, New/Open/Save, AddEntry, Check, MissingBindingsPanel) | Author view (same flow, one surface) |
| StatusText + StepsPanel | Task view: DAG + plain status |
| Cancel controls + CancelStatusText | Task view, on the running step / task bar |
| Decision controls + DecisionStatusText | Task view, inline on the paused step; also actionable from Home's inbox |
| HistoryPanel | Task view → step drill-in → Attempts |
| ConversationExecutionsPanel + ConversationPanel | Task view → step drill-in → Conversation |
| DecisionsPanel | Task view → step drill-in → Decisions |
| SupplementaryPanel | Task view → step drill-in → Attempts (revised attempts inline in the attempt list) |
| LineagePanel + ArtifactPreviewBox | Task view → step drill-in → Outputs |
| TemplateComparePathBox + CompareButton + DiffPanel | Task view → task-level details ("Compare to workflow version", picker-driven) |

Nothing in the left column is dropped; nothing in the right column requires new engine
capability. The shell, Home, and the re-home land in Phase 2; the Task drill-in redesign is
Phase 3; Author's guided flow is Phase 4; all of it styled to the bar in Phase 5.
