# Documentation trust index

> **Status: proposal (M25). This document changes nothing on disk.** It answers one question the
> owner raised during the 2026-07-24 design pause — *"I don't like not knowing if I can trust plan or
> architecture or journey documents in the repo"* — by classifying every doc against the ground-up
> design recorded in decisions [0012](decisions/0012-what-aer-flow-is.md)–[0018](decisions/0018-attention-is-the-primary-signal.md).
> It **recommends**; it does not delete, move, or rewrite anything. Executing any row is a separate,
> owner-approved step, and every execution lands in Phase 5's doc-rewrite work (`#367`, `#314`,
> `#315`), not here.

## Why this exists

After a ground-up redesign, the danger is not the new records — those are fresh. It is the *older*
documents that still read as authoritative but now describe a product being torn out, in vocabulary
being deleted. A reader cannot tell trustworthy from stale by looking, because both are just markdown
in the same tree. This index makes that judgment explicit, once, so it does not have to be
re-derived per document per reader.

## How each doc is rated

| Rating | Meaning |
|---|---|
| ✅ **Trust** | Current and authoritative for its scope. Gated, or freshly written against 0012–0018. |
| 🟡 **Trust with caveat** | Authoritative for what it covers, but a named part will shift — usually vocabulary (the `#443` rename) or reconciliation against 0012's demonstrations. Read it; know the caveat. |
| 🕓 **Historical** | True as a record of a past decision or milestone. Safe to read *as history*; do **not** read as the current target. |
| ⛔ **Stale for target** | Describes the surface being rebuilt or a deleted vocabulary. Actively misleading if read as current. Slated for rewrite. |

**Method note.** Records, the plan, the specs' fronts, both `journeys.md`, and every doc touched by
the redesign were read directly. A few were classified by their stated role and header rather than a
full re-read (noted inline). Where that is the basis, the rating is deliberately conservative.

## The index

### Decisions and the living plan — the spine

| Document | Role | Rating | Note / proposed action |
|---|---|---|---|
| `docs/decisions/0001–0018` + `README.md` | Why we chose what we chose | ✅ Trust | The source of truth. `0001` is **amended** by `0013` (room becomes the user-facing noun); `0015` is **proposed**, not accepted — its permission kind is gated on `#445` plus an unrun probe. Both states are marked in-file and in the index. Keep. |
| `docs/plan.md` | The living, gated plan | ✅ Trust | Updated in this change to carry 0012–0018 and the UI-only rebuild scope. Gated by `Aer.Plan.Tests`. Keep. |
| `docs/decisions-of-record.md` | Milestone history + decisions of record | ✅ Trust *(as history)* | Append-only and self-labelled as history, not the current plan. Its M19–M24 summaries use pre-`0001` vocabulary (`task`, `session`) — correct *for their date*. Keep; do not back-edit. |

### Journeys — the completion bar

| Document | Role | Rating | Note / proposed action |
|---|---|---|---|
| `spec/journeys.md` | What the product promises a person | 🟡 Trust with caveat | The *form* (promises as cross-surface outcomes) is exactly what `0012` endorses. The *specific* nine journeys predate 0012–0018 and its four named demonstrations (cross-vendor contradiction, no-key two-vendor run, cross-vendor fact reuse, desk-to-phone continuity). **Propose:** reconcile the journey set against `0012` during `#367`/`#314`; it stays structurally live (the plan gate references its `J`-ids). |
| `docs/runbooks/journeys.md` | How to *run* the journey tests | ✅ Trust | Not a duplicate of the above — it is the test runbook (`#313`). Procedure, current. Keep. *(The two files sharing a basename is a mild footgun; **propose** renaming the runbook to `journey-tests.md` when convenient — cosmetic, not urgent.)* |

### Behavioural specs

| Document | Role | Rating | Note / proposed action |
|---|---|---|---|
| `spec/aer-flow-behavioral-spec-v1.0.md` | What the **engine** does | 🟡 Trust with caveat | The engine is explicitly *not* being rebuilt (plan scope). Its semantics stand; its nouns (`task`/`session`) shift with the `#443` rename. Keep; revise vocabulary only, under `#315`/`#443`. |
| `spec/aer-flow-ui-behavioral-spec-v1.0.md` | What the **UI** does | ⛔ Stale for target | The most stale document in the repo. It specifies the projection/authoring/control UI being **torn out** (plan scope), in the **deleted** `task` vocabulary, around a **freeform DAG** that `0014` replaces with a list. **Propose:** mark superseded and rewrite in Phase 5 (`#367`, `#314`); until then, read only as a record of the outgoing UI. |
| `spec/AER Overview.md` | High-level product overview | 🟡 Trust with caveat | Just name-scrubbed in this change (an inspiration product's name removed). Not yet reconciled to the room noun and 0012–0018. **Propose:** verify against `0012`/`0013` during `#367`. |

### UX documents (all M19-era)

| Document | Role | Rating | Note / proposed action |
|---|---|---|---|
| `docs/ux/information-architecture.md` | Where each capability lives | 🕓 Historical | Already self-labelled with an M25 banner pointing at `plan.md` and `0001` — it correctly calls itself the M19 baseline. Its three-view split (`Home`/`Task`/`Author`) is superseded by the single switcher shell (`#336`) and `0014`. Name-scrubbed here. Keep as baseline; do not read as target. |
| `docs/ux/ux-principles.md` | Presentation principles + vocabulary map | 🕓 Historical | M19 Phase 1. Its vocabulary map still maps to `task`/`session` (pre-`0001`) and predates the room noun. The *principles* (plain language, needs-you-first) largely survive; the *map* does not. Name-scrubbed here. **Propose:** fold the surviving principles into the Phase 5 spec rewrite; retire the stale map with `#315`. |
| `docs/ux/design-language.md` | The M19 visual bar + reference set | 🕓 Historical *(partly superseded)* | Superseded on visual direction by `0006` (Quiet). One live **conflict**: it points at n8n for "Task view DAG… a canvas that stays calm at scale," which `0014` (list-not-canvas) reverses. Keep the reference set as owner-supplied history; **propose** noting the `0006`/`0014` supersession inline. |
| `docs/ux/non-expert-audit.md` | A point-in-time usability audit | 🕓 Historical | Classified by role/header, not a full re-read. An audit is dated evidence, not a spec. Keep as history. |

### Walkthroughs and runbooks

| Document | Role | Rating | Note / proposed action |
|---|---|---|---|
| `docs/walkthroughs/first-real-workflow.md` | "How do I actually use this" (M17) | 🕓 Historical | The send-back **mechanics** still run, but the flow is framed in `task`/DAG-authoring terms and "you are the relay" — pre-room-model. **Propose:** rewrite against the room noun and the three shapes when the rebuilt UI lands; until then it teaches the outgoing flow. |
| `docs/runbooks/live-*.md`, `tailscale-cross-network-proof.md` | Live-vendor smoke + transport proofs | ✅ Trust | Procedures against the engine/daemon/adapters, none of which the rebuild touches. `live-claude-smoke.md` also carries the verified `claude` auto-approve / `agy` fail-closed findings `0015` leans on. Keep. |

### Repo-level and generated

| Document | Role | Rating | Note / proposed action |
|---|---|---|---|
| `CLAUDE.md` | Build, conventions, architecture rules | ✅ Trust | Authoritative and current (invoked throughout 0012–0018). Keep. |
| `README.md` (root) | Project entry point | 🟡 Trust with caveat | Classified by role. Likely carries pre-room vocabulary in places. **Propose:** vocabulary pass under `#315`. |
| `CHANGELOG.md`, `src/*/CHANGELOG.md` | Release history | ✅ Trust | Tool-generated (`release-please`). Historical by nature, accurate. Keep. |
| `src/Aer.Mobile/README.md`, `tests/Aer.Journeys.Tests/README.md`, `tools/ui-harness/README.md` | Component docs | 🟡 Trust with caveat | Classified by role, not re-read. Component-local; low blast radius. Revisit if the rebuild changes their component's surface. |

## Two structural risks, and where they already stand

- **Competing plan documents** — the exact rot this whole effort exists to kill — are already
  guarded: `IMPLEMENTATION_PLAN.md` is deleted and `Aer.Plan.Tests` fails the build if it returns.
  No action needed; noted so the guarantee is visible here too.
- **Doc rot generally** is gated only for `docs/plan.md` and `docs/decisions/` (set equality) and for
  relative links in `plan.md` + `decisions-of-record.md`. Everything rated 🕓 or ⛔ above sits
  **outside** any gate — which is precisely why they can read as current while being stale. The Phase
  5 rewrite (`#367`) plus the spec-in-CI gate (`#314`) and vocabulary lint (`#315`) are what would
  bring them inside a gate; this index is the interim map until they do.

## What this proposes, in one list

Nothing here is executed. For the owner to accept or reject:

1. Rewrite `spec/aer-flow-ui-behavioral-spec-v1.0.md` against 0012–0018 (mark superseded meanwhile). — `#367`
2. Reconcile `spec/journeys.md`'s nine journeys against `0012`'s demonstrations. — `#367`/`#314`
3. Retire `ux-principles.md`'s vocabulary map and fold surviving principles into the spec rewrite. — `#315`/`#367`
4. Note the `0006`/`0014` supersession inline in `design-language.md`.
5. Rewrite `first-real-workflow.md` against the room noun and three shapes once the rebuilt UI lands.
6. Vocabulary pass over `spec/AER Overview.md`, root `README.md`, and the engine spec. — `#315`/`#443`
7. Rename `docs/runbooks/journeys.md` → `journey-tests.md` to end the basename collision. *(cosmetic)*
