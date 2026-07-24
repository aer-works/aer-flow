# 0020 — One state machine: every surface renders the room's state, none derives its own

Status: accepted
Date: 2026-07-24

## Context

The M25 manual run produced two defects that look unrelated and are the same bug:

- **#467** — the shell reports *"no task open"* while a task is open and running.
- **#468** — the Conversation tab says *"no conversation recorded"* directly above the conversation.

Both were filed as UI bugs. Neither is. In each case a surface computed its own answer to a question
the room had already answered, and computed it differently. The header derived "is anything open?"
from one thing, the runner from another; the tab derived "is there a conversation?" from a check that
disagreed with the renderer sitting immediately below it.

The corpus names the general form, in
[`01-definition.md`](../design/01-definition.md#a-sessions-life) — written, it says, *"because the
last run produced two surfaces disagreeing about which state a session was in"*:

> One source of truth per session. Every surface — switcher row, header, inbox, phone — renders this
> state and nothing derived independently. **Cancelled and Failed are states, not absences**: a
> stopped session must never read as "Finished."

[`02-screens.md`](../design/02-screens.md) states the consequence as a call: *"Every surface renders
the room's state machine and none derives its own — which is what makes 'no task open' while running
**impossible rather than merely fixed**."*

That last clause is why this is a decision rather than two bug fixes. Fixing #467 and #468
individually leaves the mechanism that produced them intact, and the mechanism is *permission for a
surface to hold an opinion about state*. This is the same failure shape the whole milestone
diagnosed — something could disagree silently because nothing checked — and the correction has to be
a rule, not a patch.

A worked example of the cost: `docs/milestone-history.md` records that a **cancelled** task rendered
as **"Finished"** because cancellation has no `WorkflowStatus` and the card's derivation fell
through. No test caught it. A derivation with a missing case does not fail; it produces a confident
wrong answer.

## Decision

**A room has exactly one state machine. Every surface renders it. No surface derives state.**

The states are the ones the corpus draws — `Idle`, `Working`, `NeedsInput`, `Finished`, `Failed`,
`Cancelled` — and three rules govern how they are consumed:

**1. Rendering is a projection, never a computation.** A surface may map a state to a mark, a word, a
colour, or a layout. It may not *decide* the state. "Is anything running?", "does this have a
conversation?", "did this finish?" are answered by the room, once. If a surface needs an answer the
room does not expose, the fix is to expose it from the room — never to compute it locally.

**2. Absence is not a state.** `Cancelled` and `Failed` are values, not the lack of a value. A
derivation that reaches its end without matching must be a compile-time or test-time failure, not a
fallback. The `Finished`-for-`Cancelled` defect is exactly what a silent fallback produces, and
CLAUDE.md's rule against swallowing exceptions is the same principle one layer down.

**3. One object, several entry points — never several copies.** A gate rendered inline, in the
"needs you" filter, and on the phone is *one* piece of state seen three times. This is what makes
[0019](0019-consulting-is-not-deciding.md)'s consultation coherent (the gate you consult about is the
gate you answer) and what makes a stop propagate: the corpus's rule that a pending permission *"dies
with its turn, everywhere at once"* is only expressible when there is one object to kill.

### Errors are content, which this record carries

The corpus states this separately, but it is the same decision applied to the failed state — a
failure is a *value the room holds*, so it renders where the room renders:

> A failure shows what broke, **in the room**, with the worker that failed right there to be asked
> about it. Not a status word with the reason behind a drill-in.
> — [`02-screens.md`](../design/02-screens.md), *the calls made here*

Concretely, from the same document's failure screen: the error text is the turn's content, the first
few lines are on screen unasked, full output is one click away, and the affordances are *Try again ·
Ask claude to fix it · Show full output*. The corpus's reasoning: *"a failure that says only 'failed'
forces a hunt through logs; the first few lines of what actually broke are almost always enough to
know whether it is your problem or the agent's."*

This belongs here rather than in its own record because "the reason lives behind a drill-in" is a
derivation in disguise — the surface deciding that a failure is a *status* with detail attached,
rather than rendering what the room holds.

## Consequences

**Easier.** #467 and #468 stop being bugs and become impossible. A new surface — a third client, a
future widget — inherits correct state by construction rather than by re-deriving it correctly. And
the states everything must handle can be decided once, centrally: the corpus's table in
[`03-interaction-depth.md`](../design/03-interaction-depth.md) (empty, loading, disconnected, worker
missing, folder gone, cancelled, failed, archived, long output, reduced motion) becomes a checklist
against one machine rather than a set of per-screen judgements. The corpus is explicit that deciding
them per screen, late, by whoever was implementing, is *"what the last rebuild drifted on"*.

**Harder.** This is a real constraint on the daemon's contract, not only on the UI. Every question a
surface wants to ask has to be answerable from the projected state, which means the projection grows
and each addition is a protocol change (#446). The tempting shortcut — a surface computing something
"just for display" — is exactly the thing forbidden, and it will be tempting precisely when the
protocol change feels disproportionate. It also interacts with staleness: a surface rendering a state
it fetched a second ago must mark it stale rather than blank it
([0018](0018-attention-is-the-primary-signal.md)), because "I don't know yet" is not one of the
states.

**Obliges us to** expose state rather than let surfaces infer it; make an unmatched state a build or
test failure rather than a fallback; keep one object behind every entry point to a gate; render a
failure's reason as content in the room, with the failing worker offered as the first way to fix it;
and treat any new "is it …?" predicate on a surface as a smell to be pushed back into the room.

**Relates to** [0018](0018-attention-is-the-primary-signal.md) — that record orders the list by
state, which presupposes exactly one state to order by. [0019](0019-consulting-is-not-deciding.md)
depends on a gate being one long-lived object rather than a modal per surface.
[0015](0015-three-kinds-of-needs-you.md)'s three kinds are properties of that one object, and its
ask-time persistence obligation is what keeps the object alive across a crash.

Related: `#467`, `#468` (the two defects this generalises), `#404` (drill-in shows full detail for
every outcome, not only failures), `#446` (per-session subscription — the protocol surface this
grows), `#482` (a failure offers the fix, with the worker that failed there to be asked).
