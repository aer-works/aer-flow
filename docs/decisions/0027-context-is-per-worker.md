# 0027 — The mechanics behind 0011's per-worker unit and announced-choice trigger

Status: accepted
Date: 2026-07-25

## Context

[0011](0011-token-based-context-management.md) states the rule: context is tracked per worker, and
running out is announced as a choice rather than acted on silently. This record is the mechanics and
code-level grounding behind that rule — why the unit has to be the worker, not the room, and why the
trigger has to be an announced choice, not an automatic handoff.

The current code tracks usage *per session*, captured into `SessionMetadata`. Verified in code, not
inferred: `src/Aer.Adapters/InteractiveSessions.cs` declares

```csharp
record SessionTurn(int TurnIndex, string Vendor, string HumanMessage, string? AssistantResponse, …)
```

and `SessionMetadata` carries exactly **one** `TurnCount`, **one** `SafetyCeiling`, **one**
`CurrentAdapter`, **one** `Model`, and a single flat list of turns. There is no participant dimension
in the record at all. Under [0013](0013-room-is-the-user-facing-noun.md) that object *is* the room.

The corpus says the opposite, and says why it matters
([`04-workers-commands-control.md`](../design/04-workers-commands-control.md)):

> **Context is per worker, not per room** — that is the fact a single-agent tool never has to express.
> Two workers in one room have completely different amounts of headroom, and a room can be comfortable
> for one and nearly full for the other.

Its worked example is a room where one worker sits at 64% of a 200k window and another at 4% of a 1M
window. Against a summed counter, a threshold fires for both at once — **the room compacts while one
worker has used almost nothing.** Compaction is lossy, so that is not a harmless early trigger; it
throws away context on a worker that had no reason to lose any.

The second question is the trigger: an automatic handoff with a marker afterward, or a choice
announced at a threshold? The corpus draws the three options: *Summarise now · Start a fresh room from
here · Leave it.* The distinction is not cosmetic — losing context without having been asked is the
kind of surprise that costs trust in the whole product, and the operator is the only one who knows
whether the older turns still matter.

## Decision

**Context headroom is a property of a worker. Running out is surfaced as a choice before it becomes an
event.**

**1. The unit is the worker, not the room.** Each participant carries its own token accounting against
its own model's window. A room reports *its workers'* headroom; it does not have headroom of its own.
This requires the participant dimension `SessionMetadata` currently lacks — the same object-model work
`#493` scopes.

**2. A threshold announces; it does not act.** Approaching the limit raises a choice: **summarise
now**, **start a fresh room carrying the conclusion**, or **leave it**. The room stays usable while the
choice is pending; this is information, not a gate ([0015](0015-three-kinds-of-needs-you.md)'s three
kinds are all things a *worker* needs from you, and this is not one of them).

**3. Automatic compaction survives as a backstop, and says so when it fires.** If the choice goes
unanswered and the window is genuinely about to overflow, compacting beats failing — but it is the
fallback, not the mechanism, and the transcript records that it happened and what it dropped. 0011's
*"a fill indicator and a compaction marker are part of the work, not a follow-on"* is right and stands.

**4. Per-worker headroom is visible without being asked for.** Two workers at wildly different fill
levels is the normal case in a multi-model room, and it is one of the few facts that changes which
worker you address next.

### Not the same thing as running out of plan

[0026](0026-running-out-of-plan-is-a-state-not-a-failure.md) is the *other* limit a room runs into, and
conflating them would be a real error: **context filling up is per worker and recoverable by
compaction; plan exhaustion is per vendor and recoverable only by time.** A worker at 90% context and a
vendor at 100% of its weekly cap need different words, different affordances, and different bands.

## Rests on

| fact | how we know | if false |
|---|---|---|
| `SessionMetadata` carries exactly one `TurnCount`, `SafetyCeiling`, `CurrentAdapter` and `Model`, with no participant dimension | **measured** — `src/Aer.Adapters/InteractiveSessions.cs`, read directly rather than inferred | the gap this record closes does not exist, and its mechanics section describes code that has already moved |
| A vendor CLI surfaces per-turn token usage AER can attribute to one worker | **measured for claude** — `cost.subagent-tokens-excluded` (`usage.output_tokens` excludes subagent tokens; `modelUsage` is whole-tree, #479). **Not measured for agy.** | headroom cannot be computed per worker on the unmeasured vendor, and the announced-choice trigger has nothing to fire on there |
| Two models in one room can have materially different context windows | **assumed** — per-vendor documentation in `docs/vendor-capabilities.md`; no check probes window size | per-worker tracking stays correct but the asymmetry that motivates it weakens to a rounding difference |

## Consequences

**Easier.** The deep-author / fast-reviewer pattern stops being quietly penalised — the expensive
worker's window fills at its own rate and the cheap one is not compacted alongside it. The number
becomes actionable rather than ambient: *"this worker is nearly full"* names who to hand off from.

**Harder.** It needs the participant dimension in session state, so it cannot land before `#493`. Every
consumer of `SessionMetadata`'s single counter has to move to a per-participant one, and the wire
protocol carries more. The threshold also has to be model-aware per worker rather than global, since a
200k window and a 1M window are not comparable at a shared percentage.

**Obliges us to** account tokens per participant against that participant's own window; announce at a
threshold rather than acting silently; keep automatic compaction as a disclosed backstop; record what
a compaction dropped; and keep this vocabulary distinct from
[0026](0026-running-out-of-plan-is-a-state-not-a-failure.md)'s.

**Relates to** [0011](0011-token-based-context-management.md), the rule this gives its mechanics to.
[0017](0017-vendor-model-effort-are-three-choices.md) is what makes per-worker windows expressible at
all. `#493` is the object-model prerequisite; `#395` is where the surface work lives.
