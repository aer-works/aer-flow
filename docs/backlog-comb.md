# Backlog comb — every open issue judged against 0012–0018

**Status: proposal. This document closes nothing.** It reads all 58 open issues against the ground-up
design ([0012](decisions/0012-what-aer-flow-is.md)–[0018](decisions/0018-attention-is-the-primary-signal.md))
and the `#472` vendor probe, and says what each one *becomes*. Executing any row — closing, splitting,
re-milestoning — is a separate owner-approved step.

The redesign rips out **the UI layers only**; engine, adapters, daemon and protocol stay. That single
fact decides most rows: a bug in a surface being deleted is not a bug to fix, it is a **requirement on
the replacement** — but only if the requirement is written down somewhere before the issue closes.
Closing them silently is how the replacement inherits the same defects.

## Verify before acting

Two of my own first-pass calls were wrong, both caught by reading the issue and the code rather than
the title:

- **#266 is not a clean close.** Its Bézier edges and hover-tracing die with the freeform canvas
  ([0014](decisions/0014-shapes-are-a-list-not-a-canvas.md)), but the same issue carries **vendor brand
  marks on nodes** and folds in **#208's motion/skeletons** — both of which survive. It splits.
- **#355 is not fixed.** `#463` fixed the status *marks*; `TaskProjectionLoader.cs:121` still builds the
  fleet item from `state.Status.ToString()`, so a failed and a successful workflow still read
  `Terminal`. Verified in the tree, still live.

## Close — delivered

| Issue | Call |
|---|---|
| #471 | Decision records + trust index. Delivered in PR #473. Close on merge. |
| #472 | Vendor probe. Delivered; findings in [`vendor-capabilities.md`](vendor-capabilities.md). One step remains and is **human-only**: `agy --remote-control` needs an interactive OAuth consent AER cannot complete. Close, carry that step forward. |

## Elevate — the probe unblocked these

`#472` proved a blocking MCP tool holds a turn open **on both vendors**. Everything that was waiting
on "can a worker even ask?" is now buildable, and [0015](decisions/0015-three-kinds-of-needs-you.md) is
`accepted` rather than `proposed`.

| Issue | What changed |
|---|---|
| #445 | **Unblocked and specified.** The mechanism is demonstrated, not hypothesised. Implementation notes — spawned twice by `claude` so keep it stateless; persist the gate at ask-time; `agy` returns the resume key in call metadata — are in `vendor-capabilities.md`. This is now the critical path for the permission kind. |
| #390 | Depends on #445; feasible now. |
| #434 | Predates the third pause kind. Rescope to **three** kinds, not two. |
| #424 | Adjacent to #445 — both host MCP. Probe shows MCP works on both vendors, so an AER-hosted context source is vendor-neutral. Worth planning together to avoid two servers. |

## Elevate — these turned out to *be* the principle

Not obsolete. These two were discovered live on a real device, and they independently found the exact
failure [0018](decisions/0018-attention-is-the-primary-signal.md) was amended for after a power cut
killed a working session: **a surface asserting calm it cannot justify.**

| Issue | Why it matters more now |
|---|---|
| #326 | A raw `401` sitting directly above "No tasks or sessions yet" — an empty state asserting a fact the app cannot know, because it received no data at all. This is 0018's freshness rule, found empirically before the rule existed. |
| #346 | "Disconnected" showing a raw Dart exception, offering only the action that cannot succeed, while the rest of the screen keeps pretending. Same class. |

Both survive the mobile rebuild **as requirements on it**, and both should be cited by 0018 as its
evidence rather than closed as bugs in deleted code.

## Rescope — still valid, but a decision changed the shape

| Issue | Reshaped by |
|---|---|
| #266 | **Split.** Canvas polish (Bézier, hover-tracing) → close against [0014](decisions/0014-shapes-are-a-list-not-a-canvas.md). Brand marks on nodes + #208 motion/skeletons → keep as a new M27 issue. |
| #327 | [0014](decisions/0014-shapes-are-a-list-not-a-canvas.md) — the shape editor is an ordered list that *renders* as a graph. "Hidden behind Advanced, with a second vocabulary" stops being the problem; the whole surface is redrawn. |
| #342 | [0017](decisions/0017-vendor-model-effort-are-three-choices.md) — vendor / model / effort are three separate choices. A single "vendor picker listing raw adapter ids" is the wrong control, not a broken one. Probe confirms effort is a real flag on both CLIs. |
| #442 | [0016](decisions/0016-memory-is-room-owned.md) **answers the question the issue asks.** Demote from open question to implementation: the room owns memory, shared across vendors, visible and editable. |
| #443 | [0013](decisions/0013-room-is-the-user-facing-noun.md) — the rename target is now **room**, and it resolves a genuine collision (`invocation.SessionId` vs `.aer/session.json`). Larger than originally scoped. |
| #282 | [0018](decisions/0018-attention-is-the-primary-signal.md) — notifications **inform, never decide**. No approve/deny in the payload; a notification carries what changed plus a link into the room. |
| #337 | [0018](decisions/0018-attention-is-the-primary-signal.md) + the owner's call that **rooms, not "needs you", is the phone's landing screen**. |
| #339 | [0014](decisions/0014-shapes-are-a-list-not-a-canvas.md) — templates are authored as shapes; three shapes with presets fits, but the authoring surface is the list. |
| #378 | Reshaped by the rebuild and by 0018's ordering. Re-scope after #337 lands rather than porting the current surface. |
| #405 | Survives, and the owner explicitly wants it — playful verb for a *generic* wait, specific action text whenever the action is known, always switchable. |

## Fold into the rebuild — bugs in surfaces being deleted

Each is real and each was observed. None should be *fixed* in code about to be removed, and none
should be closed until its requirement is captured in the rebuild issue named beside it.

| Issue | Fold into | Requirement it contributes |
|---|---|---|
| #319 | #337 | The inbox must not be scoped to one open room. |
| #320 | #336 / #434 | A gate must open on the step it belongs to, and never summarise the prompt away. |
| #323 | #337 | Progress events need separators — output is read, not concatenated. |
| #350 | #336 | Live refresh must not rebuild every row per tick; hover and focus survive updates. |
| #383 | #336 | Selection uses the brand accent, never the "needs input" amber. |
| #467 | #336 | The shell must never report "no room open" while one is open — the defect that started this pause. |
| #468 | #336 | A tab must not deny the content rendered directly beneath it. |

## Survives untouched — the rebuild does not reach these

Engine, adapters, daemon, protocol, CI, docs. Judged still-valid; no decision touches them.

**Engine / runtime / dispatch:** #368 (scoped warmth), #385 (advisor participant), #386 (skills),
#395 (token-based context management), #465 (dialogue prompt duplicated and growing — real, observed),
#466 (non-ASCII mangled on Windows).

**Daemon / infra:** #382 (TailnetGateway bypassed), #398 (`flow.jsonl` Windows CI race), #432
(Program.cs endpoint grouping), #446 (per-session WebSocket subscription), #447 (MainWindowViewModel
out of the daemon), #469 (`pixi run mobile-build` runs under WSL bash — already root-caused).

**Quality / docs / CI:** #314, #315 (vocabulary lint — *more* important now that 0013 renames the
user-facing noun), #325, #367 (now has the trust index as its input), #371, #438.

**Still-live UI work that the rebuild needs anyway:** #267 (markdown/code — owner's call that code
blocks sit behind expanders), #268 (keyboard-first triage), #338 (Settings, folding Remote in),
#340 (derived rooms), #355 (**verified still live**), #377 (visual diff), #404 (activity drill-in),
#448 (concurrency cap — its "queued" is a band in 0018), #455 (copy affordance), #462 (never block
the operator), #208 / #376 / #459 (M27 polish, unaffected).

## The one genuinely open question

**#336 / PR #464.** The switcher shell is built, green, and deliberately unmerged — it is the old
surface, and the rebuild may not keep it. Merging costs nothing but adds code we may delete; closing
it discards work that already fixed real defects (the vanished-room bug, cross-product ordering
divergence). This needs an owner call, not a default.

Related: [0012](decisions/0012-what-aer-flow-is.md)–[0018](decisions/0018-attention-is-the-primary-signal.md),
[`documentation-trust-index.md`](documentation-trust-index.md), [`vendor-capabilities.md`](vendor-capabilities.md), `#472`.
