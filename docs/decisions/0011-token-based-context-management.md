# 0011 — Context management is token-based, not turn-based

Status: accepted, except its unit and trigger — **corrected by [0027](0027-context-is-per-worker.md)**
Date: 2026-07-23

> **Amendment, 2026-07-25 (#501) — this one corrects rather than extends.** Token-based accounting is right and stands; counting turns was counting the wrong thing. **Two clauses are wrong.** This record tracks usage *per session* — and under [0013](0013-room-is-the-user-facing-noun.md) that object is the room — where context is a property of a **worker**: two workers in one room have completely different headroom, and a summed counter compacts the room while one of them has used almost nothing. And it makes running out an **automatic** handoff where it should be a **choice announced at a threshold** (summarise now / start a fresh room from here / leave it), with automatic compaction surviving only as a disclosed backstop. See [0027](0027-context-is-per-worker.md). The clauses are left in place below rather than edited: this record exists because a wrong unit was chosen once, and it then chose a wrong unit itself, which is worth keeping visible.

## Context

A long conversation must eventually compact — summarise its history and continue in a fresh
native vendor session — or it overruns the model's context window. AER already does this:
`ExecuteSessionTurnAsync` computes `isCeilingReached = metadata.TurnCount >= metadata.SafetyCeiling`
and, when the ceiling is crossed, forces a handoff (`SynthesizeContextSummary` + a fresh native
session, resetting the turn count). A manual `POST /api/sessions/{id}/compact` does the same on
demand. So the *mechanism* — auto-compact via handoff, plus manual compact — exists and works.

It is counted in the wrong unit. A **turn ceiling is a crude proxy for context pressure**: ten
one-line turns and ten 50k-token turns are treated identically, so the ceiling fires too early on a
light conversation (throwing away usable context and paying for an unnecessary summary) and too late
on a heavy one (risking an overrun the compaction was supposed to prevent). The thing that actually
determines when to compact is *tokens consumed*, and the vendors report it — `claude
--output-format stream-json` emits a usage figure on its `result` line. The same usage stream is
what a cross-vendor cost view (J9, [0008](0008-runtime-streaming-over-append-log.md)) has to capture
anyway, so accounting for it pays twice.

The considered alternative was the cheap one: leave the trigger turn-based and merely expose
`SafetyCeiling` as a user setting. Rejected — it makes the wrong proxy configurable instead of
replacing it.

## Decision

**The context-management trigger is token-based.** Track running token usage per session from the
vendor's reported usage, and trigger the handoff/compact when it approaches a **configurable,
model-aware token threshold**. The turn count is kept only as a **hard backstop** and as the
fail-safe fallback when a vendor reports no usage — the safety rail is never silently lost.

This re-bases `SafetyCeiling`'s trigger role onto tokens; it does not invent a new mechanism (the
handoff and `/compact` endpoint stay as they are).

## Consequences

**Easier.** Compaction fires when it should, not on an unrelated turn count. The token stream is
shared with J9's usage/cost view — one capture, two consumers. The threshold becomes a setting that
means something a user can reason about ("compact near 70% of the window"), model-aware because the
window differs by model.

**Harder.** Per-vendor usage accounting must live in the adapter (Adapter Isolation, CLAUDE.md rule
2): `claude`'s stream-json usage is available, but `agy`'s is unverified and must be checked before
token-based compaction is promised for Gemini (strip `CLAUDE_CODE_*` when probing — see the
vendor-CLI probe runbook). The threshold has to be model-aware, which couples it to knowing the
active model ([0010](0010-skills-and-advisor.md)'s participant-as-binding, surfaced by #391).

**Obliges us to.** Capture usage into `SessionMetadata` as running context size. Keep the turn-count
backstop as the fail-safe, so a session against a usage-less vendor still compacts. Surface context
pressure to the user — a silent handoff underneath a live conversation is confusing, so a fill
indicator and a compaction marker are part of the work, not a follow-on. Verify `agy`'s usage
reporting before enabling token-based compaction for Gemini.

Relates: [0008](0008-runtime-streaming-over-append-log.md) (runtime, worker lifetime, the usage
stream), [0010](0010-skills-and-advisor.md) (model as part of a participant binding), #395
(implementation), #338 (the Settings surface the threshold lives in), #391 (model visibility — the
window is per-model), J9 (usage/cost view).
