# Documentation trust index

> **Status: in force (M25), and acted on.** It answers one question the owner raised during the
> 2026-07-24 design pause — *"I don't like not knowing if I can trust plan or architecture or journey
> documents in the repo"* — by classifying every doc against the ground-up design recorded in
> decisions [0012](decisions/0012-what-aer-flow-is.md)–[0018](decisions/0018-attention-is-the-primary-signal.md).
>
> It began as a proposal. The owner's call was that recording an acceptance and then opening a second
> PR to act on it is ceremony, so **the actions are executed in the same change** — see
> [What this proposed, and what happened](#what-this-proposed-and-what-happened) at the foot. Two
> items are deliberately *not* done, with the reason stated there: they are rewrites of documents that
> describe a UI which does not exist yet, and writing those now would be fiction that becomes the next
> stale document.

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
| 📦 **Archived** | Moved to `docs/archive/`. Was ⛔ or 🕓; now out of the live tree entirely so it cannot be mistaken for current. |

**Method note.** Records, the plan, the specs' fronts, both `journeys.md`, and every doc touched by
the redesign were read directly. A few were classified by their stated role and header rather than a
full re-read (noted inline). Where that is the basis, the rating is deliberately conservative.

## The index

### Decisions and the living plan — the spine

| Document | Role | Rating | Note / what was done |
|---|---|---|---|
| `docs/decisions/0001–0018` + `README.md` | Why we chose what we chose | ✅ Trust | The source of truth. `0001` is **amended** by `0013` (room becomes the user-facing noun). `0015` was **promoted proposed → accepted** on 2026-07-24 once the `#472` probe ran: the blocking-MCP mechanism is demonstrated on **both** vendors, and the probe *disproved* the record's original premise that `claude` headless auto-approves. `0015` and `0018` both carry crash-durability amendments from the same day. Keep. |
| `docs/plan.md` | The living, gated plan | ✅ Trust | Updated in this change to carry 0012–0018 and the UI-only rebuild scope. Gated by `Aer.Plan.Tests`. Keep. |
| `docs/milestone-history.md` | Milestone history + decisions of record | ✅ Trust *(as history)* | Append-only and self-labelled as history, not the current plan. Its M19–M24 summaries use pre-`0001` vocabulary (`task`, `session`) — correct *for their date*. Keep; do not back-edit. |
| `docs/vendor-capabilities.md` | What each worker CLI can actually do | ✅ Trust *(with an expiry)* | Every row observed in a live run or read from the shipped binary (`#472`, 2026-07-24). It **corrects two assumptions this milestone was designing against** — `claude -p` does not auto-approve, and MCP is not Claude-only. Caveat: both CLIs self-update, so it is true of `claude` 2.1.219 / `agy` 1.1.6 and should be re-probed after a vendor bump. Keep. |

### Journeys — the completion bar

| Document | Role | Rating | Note / what was done |
|---|---|---|---|
| `spec/journeys.md` | What the product promises a person | ✅ **Trust** | **Upgraded from 🟡 on a re-read — this is the healthiest document in the repo, not a stale one.** A journey is a promise to a person across surfaces ("approve it from your phone"), never a description of a screen, and that altitude is immune to a UI rebuild — which is exactly why the UX docs died and these did not. J2 already uses the **room** noun and already describes a session as a multi-participant conversation spawning children; it reached `0012`'s model before `0012` existed. **J7 is the executable form of the crash/disconnect principle `0018` was amended for** ("a truthful state, not a raw exception… no dead-end button that can't succeed") — the same failure #326 and #346 found on a real device. Guarded by `ReconcileTests`, which keeps spec and registry byte-identical. Seven of nine currently read **Fails**: that is an honest scoreboard, not rot. **Remaining work is additive, not corrective** — three of `0012`'s demonstrations (cross-vendor contradiction, no-key two-vendor run, cross-vendor fact reuse) have no journey yet. |
| `docs/runbooks/journey-tests.md` | How to *run* the journey tests | ✅ Trust | Not a duplicate of the above — it is the test runbook (`#313`). Procedure, current. **Done:** renamed from `journeys.md` to end the basename collision with `spec/journeys.md`; all six inbound references updated. |

### Behavioural specs

| Document | Role | Rating | Note / what was done |
|---|---|---|---|
| `spec/aer-flow-behavioral-spec-v1.0.md` | What the **engine** does | 🟡 Trust with caveat | The engine is explicitly *not* being rebuilt (plan scope). Its semantics stand; its nouns (`task`/`session`) shift with the `#443` rename. Keep; revise vocabulary only, under `#315`/`#443`. |
| `docs/archive/spec/aer-flow-ui-behavioral-spec-v1.0.md` | What the **UI** does | 📦 Archived | The most stale document in the repo. It specifies the projection/authoring/control UI being **torn out** (plan scope), in the **deleted** `task` vocabulary, around a **freeform DAG** that `0014` replaces with a list. **Done:** archived, with a banner naming the three axes it is superseded on. **Not** rewritten — a UI contract written against a UI that does not exist yet is fiction. The replacement lands with the rebuilt surface (`#367`, `#314`). |
| `spec/AER Overview.md` | High-level product overview | 🟡 Trust with caveat | **Done:** name-scrubbed, vendor naming corrected (`agy` is the Gemini CLI's successor), and §2 reconciled to `0012` — the pipeline is the machinery, the room is what the person meets. Rating stays 🟡 because its later sections still carry pre-room vocabulary (`#443`). |

### UX documents (all M19-era)

| Document | Role | Rating | Note / what was done |
|---|---|---|---|
| `docs/archive/ux/information-architecture.md` | Where each capability lives | 📦 Archived | Already self-labelled with an M25 banner pointing at `plan.md` and `0001` — it correctly calls itself the M19 baseline. Its three-view split (`Home`/`Task`/`Author`) is superseded by the single switcher shell (`#336`) and `0014`. Name-scrubbed, then **archived**. |
| `docs/archive/ux/ux-principles.md` | Presentation principles + vocabulary map | 📦 Archived | M19 Phase 1. Its vocabulary map still maps to `task`/`session` (pre-`0001`) and predates the room noun. The *principles* (plain language, needs-you-first) largely survive; the *map* does not. **Done:** the vocabulary map is marked retired in-file (it predates the room noun and the three-kind pause), then the whole doc archived. Surviving principles fold into the spec rewrite (`#367`). |
| `docs/archive/ux/design-language.md` | The M19 visual bar + reference set | 📦 Archived | Superseded on visual direction by `0006` (Quiet). One live **conflict**: it points at n8n for "Task view DAG… a canvas that stays calm at scale," which `0014` (list-not-canvas) reverses. **Done:** the `0006`/`0014` supersession is noted inline — the n8n row is struck through — then archived. The reference set stays owner-supplied history. |
| `docs/archive/ux/non-expert-audit.md` | A point-in-time usability audit | 📦 Archived | Classified by role/header, not a full re-read. An audit is dated evidence, not a spec. **Archived.** |

### Walkthroughs and runbooks

| Document | Role | Rating | Note / what was done |
|---|---|---|---|
| `docs/archive/walkthroughs/first-real-workflow.md` | "How do I actually use this" (M17) | 📦 Archived | The send-back **mechanics** still run, but the flow is framed in `task`/DAG-authoring terms and "you are the relay" — pre-room-model. **Done:** archived with a banner separating the mechanics (still real) from the framing (superseded by 0012). Rewrite lands with the rebuilt UI (`#367`). |
| `docs/runbooks/live-*.md`, `tailscale-cross-network-proof.md` | Live-vendor smoke + transport proofs | ✅ Trust | Procedures against the engine/daemon/adapters, none of which the rebuild touches. ⚠️ `live-claude-smoke.md` records `claude` as **auto-approving**, which the `#472` probe **disproved** — both vendors fail closed. Correct it against [`vendor-capabilities.md`](vendor-capabilities.md). |

### Repo-level and generated

| Document | Role | Rating | Note / what was done |
|---|---|---|---|
| `CLAUDE.md` | Build, conventions, architecture rules | ✅ Trust | Authoritative and current (invoked throughout 0012–0018). Keep. |
| `README.md` (root) | Project entry point | 🟡 Trust with caveat | **Done:** documentation section rewritten to lead with the decision records and this index, and to mark the walkthroughs historical and the UI spec archived. |
| `CHANGELOG.md`, `src/*/CHANGELOG.md` | Release history | ✅ Trust | Tool-generated (`release-please`). Historical by nature, accurate. Keep. |
| `src/Aer.Mobile/README.md`, `tests/Aer.Journeys.Tests/README.md`, `tools/ui-harness/README.md` | Component docs | 🟡 Trust with caveat | Classified by role, not re-read. Component-local; low blast radius. Revisit if the rebuild changes their component's surface. |

## Two structural risks, and where they already stand

- **Competing plan documents** — the exact rot this whole effort exists to kill — are already
  guarded: `IMPLEMENTATION_PLAN.md` is deleted and `Aer.Plan.Tests` fails the build if it returns.
  No action needed; noted so the guarantee is visible here too.
- **Doc rot generally** is gated only for `docs/plan.md` and `docs/decisions/` (set equality) and for
  relative links in `plan.md` + `milestone-history.md`. Everything rated 🕓 or ⛔ above sits
  **outside** any gate — which is precisely why they can read as current while being stale. The Phase
  5 rewrite (`#367`) plus the spec-in-CI gate (`#314`) and vocabulary lint (`#315`) are what would
  bring them inside a gate; this index is the interim map until they do.

## What this proposed, and what happened

It began as a proposal; the owner's call was to execute in the same change rather than open a second
PR. Seven items, five done and two deliberately deferred.

| # | Item | Status |
|---|---|---|
| 1 | Rewrite the UI behavioural spec against 0012–0018 | **Deferred, deliberately** — archived with a banner instead. See below. |
| 2 | Reconcile `spec/journeys.md`'s nine journeys against `0012`'s demonstrations | **Reframed — the premise was wrong.** A re-read found the journeys *aligned*, not stale (J2 already says "room"; J7 already specifies the disconnect principle 0018 was amended for). Nothing needs retiring. What remains is **adding** journeys for three of 0012's demonstrations — a statement about what the product promises, and a coordinated spec + registry change under `ReconcileTests`. Own piece of work, not a docs cleanup. |
| 3 | Retire `ux-principles.md`'s vocabulary map | **Done** — marked retired in-file, then archived. |
| 4 | Note the `0006`/`0014` supersession in `design-language.md` | **Done** — inline, n8n row struck through, then archived. |
| 5 | Rewrite `first-real-workflow.md` | **Done as far as is honest** — archived with the mechanics/framing split called out; the rewrite needs the rebuilt UI. |
| 6 | Vocabulary pass over `AER Overview.md`, root `README.md`, engine spec | **Done** for the Overview and README. Engine spec deferred to `#443` — its nouns are technical, and changing them ahead of the code rename would desynchronise doc from source. |
| 7 | Rename `docs/runbooks/journeys.md` → `journey-tests.md` | **Done** — six inbound references updated. |

Beyond the original seven, the M19 UX set, the walkthroughs and the UI spec were **moved into
`docs/archive/`** rather than left in the live tree with banners. A banner asks the reader to notice
it; a directory named `archive` cannot be misread.

### Why two are deferred, and what would be wrong with doing them now

**The UI spec rewrite (1) describes a surface that does not exist yet.** The rebuild has not
happened. Writing it now means inventing the behaviour of screens nobody has built, which is not
documentation — it is a design proposal wearing a spec's clothes, and it would be the *next* thing
this index has to rate ⛔. The decision records already carry the intent; the spec is written when
there is something to specify.

**(2) turned out to rest on a false premise, and the correction is worth keeping.** The journeys were
rated 🟡 on the assumption that they predated 0012–0018 and needed reconciling. Reading them showed
the reverse: they are written one level above the UI — as outcomes a person gets, not screens they
touch — so the rebuild does not reach them. Two are ahead of the records rather than behind
(J2's room, J7's disconnect truthfulness). The work left is **additive**: three of 0012's
demonstrations have no journey, and adding one is a coordinated `spec/journeys.md` + registry change
under `ReconcileTests`, plus a statement about what the product promises. That earns its own review,
which is why it is not slipped into this PR — but it is *growth*, not repair.

## What is now gated, and what still is not

- **Competing plan documents** — the exact rot this effort exists to kill — remain guarded:
  `IMPLEMENTATION_PLAN.md` is deleted and `Aer.Plan.Tests` fails the build if it returns.
- **Link rot** in `docs/plan.md` and `docs/milestone-history.md` is gated. That gate was checked
  before the archive move: neither file markdown-links into anything that moved.
- **Still ungated:** everything in `docs/archive/` (by design — it is frozen), and the vocabulary of
  the live docs. `#315` (vocabulary lint) and `#314` (spec structure in CI) are what would close
  that, and until they land this index plus the archive boundary are the mechanism.
