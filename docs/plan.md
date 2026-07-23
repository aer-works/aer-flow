# The plan

The living plan for AER — versioned with the code, reviewed in PRs, and **gated so it can't rot**.
Its predecessor was a GitHub issue (#283) that went stale in five places while nothing caught it;
that is the exact failure this milestone exists to kill, so the plan now lives where the discipline
does.

## The bar

AER replaces Claude Code (terminal + mobile) and Antigravity (desktop) **entirely** — full parity
between desktop and mobile, talking to either vendor from either surface, staying as easy to
understand and orchestrate as either standalone product. Any work is judged against that goal
directly, not as an isolated screen.

## How this plan stays honest

This document owns **durable structure** — the bar, the phases, the dependency order, the decisions
in force. It does **not restate status**, because restated status is what rots. Status is deferred
to the sources that already keep it, each with its own gate:

| For… | Look at | Kept honest by |
|---|---|---|
| *why* we chose something | [`docs/decisions/`](decisions/) | numbered, immutable — superseded, never edited |
| what the product *promises*, and whether it's met | [`spec/journeys.md`](../spec/journeys.md) | the journey tests (#313) + the reconcile gate (#314) |
| what the *engine* does | [`spec/`](../spec/) behavioural specs | the test suite |
| an issue's live state | the **[M25 milestone](https://github.com/aer-works/aer-flow/milestone/18)** / project board | GitHub |

**The gate.** `tests/Aer.Plan.Tests` runs in default CI and fails the build if this file drifts from
those sources — every decision it names must exist in `docs/decisions/` and match the index, and
every journey it references must exist in `spec/journeys.md`. A plan that can lie about a decision or
a promise is a plan that rots; this is the check that stops it, the same way #314 stops the
journeys' statuses from rotting.

## Decisions in force

Recorded in [`docs/decisions/`](decisions/) (#316), never edited to change meaning — superseded.

| # | Decision |
|---|---|
| [0001](decisions/0001-two-nouns-workflow-and-session.md) | Two nouns: **workflow** and **session** — "task" is deleted from the product. **A session is a *room*** (amended): a multi-participant conversation that spawns child sessions into a tree. |
| [0002](decisions/0002-one-vocabulary.md) | One vocabulary — retire the translation map, enforce by lint (#315). |
| [0003](decisions/0003-templates-collapse-to-three-shapes.md) | Templates collapse to **three shapes with presets**. |
| [0004](decisions/0004-permission-scopes.md) | Permissions scope by **project ∩ session ∩ step**, failing closed. |
| [0005](decisions/0005-seam-milestones.md) | Capability milestones alternate with **seam milestones**. |
| [0006](decisions/0006-visual-direction-quiet.md) | Visual direction is **Quiet** — status colour is a ramp separate from the brand accent. |
| [0007](decisions/0007-background-work-inline-and-dedicated.md) | Background work surfaces at **three levels**: glance inline, expand in place, dedicated surface for depth (#360). |
| [0008](decisions/0008-runtime-streaming-over-append-log.md) | Runtime is **live streaming over a durable append log** — worker lifetime is a swappable policy (default cold; scoped warmth is #368). Per-turn cost is intrinsic, so a cross-vendor usage view is the real cost lever (J9). |
| [0009](decisions/0009-session-lifecycle-and-retention.md) | **Count the top of the tree, not the tree** — children ephemeral by default, worker spawning bounded by a depth/count ceiling that doubles as J6's safety rail. |

## The completion bar: journeys

A milestone is done when its **[journeys](../spec/journeys.md)** pass — a promise driven against the
*real* surface a person uses, not an isolated screen. Nine are defined (J1–J9); their statuses are
machine-kept, so this document links them rather than repeating them. Journey tests are the answer
to the milestone's sharpest finding: *not one completion gate touched a UI, so a product could pass
every gate it had with no working client — and very nearly did.*

## The work, by phase

Ordered by dependency, not visibility. Per-issue state lives on the
[milestone board](https://github.com/aer-works/aer-flow/milestone/18); this is the structure and the
reasoning, which change rarely.

### Phase 0 — make verification possible *(landed)*
The infrastructure everything after it is unverifiable without: **#328** UI driving harness · **#317**
`AerPaths` seam · **#318** test isolation · **#312** journeys as a spec artifact · **#313** journey
tests driving the real desktop and mobile UI. This phase ships nothing a user sees; it is the reason
the rest can be trusted.

### Phase 1 — safety and correctness on the core path
**#331** enforce permissions and fail closed *(J6 — a shell-denied session ran `hostname` and got the
real value; every permission surface is decorative until this lands)* · **#321** bind a directory ·
**#330** broadcast desktop-started state · **#348** phone-started work never opens · **#347** a restart
strands paired phones · **#349** "Forget pairing" leaves the token valid.

*Independent of every IA decision; none of it waits on the rethink.* Before scoping **#335**,
re-confirm its `ConcurrencyGuard` assumption against **#341** (a send accepted, then the decision
silently dropped) — the two interact on the core path. Remote is broken in **both** directions (#330
desktop→phone, #348 phone's own work), and pairing itself is unreliable (#347) and un-revocable
(#349) — walked live 2026-07-22.

### Phase 2 — contract gaps the UI can't work around
**#322** timestamps · **#324** empty 400 body · **#319** inbox scoping.

### Phase 3 — the rethink proper (the room model)
The object model unifies on two nouns and a session becomes a **room** — a multi-participant
conversation that spawns children into a tree (decisions [0001](decisions/0001-two-nouns-workflow-and-session.md)
/ [0008](decisions/0008-runtime-streaming-over-append-log.md) /
[0009](decisions/0009-session-lifecycle-and-retention.md)).

**#333** unify the object model on two nouns · **#334** split `PausePoint` · **#335** multi-task
daemon *(**its "zero `Aer.Flow` changes" estimate is retired** — 0009 obliges an append-log
compaction/archival step for completed subtrees, a real engine addition)* · **#345** one token file
for both toolkits · **#336** desktop switcher shell · **#337** mobile list-as-destination · **#338**
Settings surface · **#339** templates to three shapes · **#340** derived sessions · **#368** scoped
warmth (0008 Path C) · cross-vendor usage view (**J9**, on the dedicated activity surface #360).

### Phase 4 — presentation and parity
**#320** approval-gate defaults · **#327** Author's graph · **#267** markdown rendering · **#323**
progress events · **#326** revoked pairing · **#346** the disconnected screen · **#325** stale
`CLAUDE.md` · **#282** notifications · **#266** / **#208** motion *(M26)*.

### Phase 5 — rewrite the spec to the target design
**#367** re-establish a coherent doc base and guard it against re-rot *(the decompose-and-audit this
plan's own move is part of — a spec rewritten over stale docs re-specifies stale claims)* · **#314**
assert spec claims in CI *(and promote the journey reconcile + this plan gate into the same required
check)* · **#315** vocabulary lint. *Gated on Phase 0's journeys landing first — a spec written
before them specifies screens instead of outcomes.*

## Why a disciplined spec produced an unusable product

The full evidence is in the **[ground-up evaluation](https://claude.ai/code/artifact/1ff40ef3-35d9-4492-ad88-549d8aeb6e44)**
(2026-07-22). The operative lesson, distilled: every defect found lived in a **seam**, and every
structural failure had the same shape — *something could go stale silently because nothing checked.*
The corrections are controls, not notes: a required artifact (#312), a gate (#313, #314), a lint
(#315), an immutable record (#316), and now this plan's own gate. **A recorded lesson is not a
control** — on 2026-07-21 the same lesson was written down and nothing structural followed, and it
recurred the next day at larger scale.

**The honest limit:** the two most valuable corrections in the evaluation came from the owner pushing
back, and both times the software was fine and the report was wrong. Automated journeys stop seams
rotting. They do not tell you the product *feels* bad — that still takes someone using it and saying
so.

## Open questions

- Whether a directory-less session is allowed at all, and where it runs (forced by #321, sharpened by #331).
- Where a project's permission ceiling is stored and first presented (0004 sets the ceiling, not its home — moves with #338).
- Typeface and motion — the direction is settled (**Quiet**, 0006) but the face is not, and it must ship as an asset on both toolkits (Avalonia defaults to Segoe UI, Flutter to Roboto). Motion belongs with the switcher build.
- Empty first run, derived-session grouping (#340), title derivation, and the spec-rename cost.

## Not in scope

- Multi-machine / multi-daemon switching — explicitly ruled out. A single daemon is fine.
- True zero-signup multi-user remote control (a stranger installs only the Aer app, no third-party
  identity step) — out of scope. It would mean operating your own coordination/relay infrastructure
  instead of Tailscale's (security surface, uptime, cost, abuse potential), not a refinement.
  Revisit only on real multi-user demand; two candidate shapes exist if it ever returns —
  self-hosted Headscale, or a purpose-built relay proxying only `Aer.Daemon`'s existing REST+WS API.
