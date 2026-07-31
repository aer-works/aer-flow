# 0045 — The product is Baton; the journal is the ledger; the CLI token stays `aer`

Status: accepted
Date: 2026-07-31

## Context

The product had no name that survives dictation: the owner dictates constantly, and "AER Flow"
spoken **is** "Airflow" — the most famous name in this product category. That disqualifier
started a recorded three-stage process (issue #823): wide generation (three haiku agents, 45
candidates), an orchestrator cull with real collision knowledge, then a cross-vendor
Gemini/Sonnet deliberation run through AER's own Dialogue worker. The deliberation's final
report is archived verbatim on #823 (the `dialogue-naming2` run artifact, turn 6) and is the
authority on why each alternative died — Ledger-as-brand on `ledger-cli`'s two-decade-old
binary, Warren on log-line attribution ambiguity paid forever, the dictation kills (Belay,
Thrum, Stoa, Locus, Wharf) on the speech round-trip itself. The deciding criteria, in cutting
order: dictation round-trip, developer-tool collision cleanliness, the product's own test
sentences.

## Decision

**The product's name is Baton.** Passing control between workers is the engine, and Baton is
the one candidate with no wound inside the hard criteria — dictation-perfect, collision-clean
at the well-known bar, unambiguous in every test sentence. The report's own honesty stands
recorded with it: Baton won as the last survivor of a filter, not the meaning contest, and its
one niche CLI collision (ConductorOne's `baton`) was judged survivable, not absent.

**The append-only journal is "the ledger"** — lowercase, an internal product noun ("every run
writes to the ledger"). This carries the project's founding artifact — the owner's original
hand-run Ledger document, where the two vendors first recorded thoughts about each other —
into the product's beating heart, where the collision criterion that killed Ledger-as-brand
cannot reach it.

**The CLI token stays `aer`** (`aer run`, `aer status`). Muscle memory, criterion-clean, and
decoupling command from brand needs no bridge: Baton-the-product over aer-the-command.

The engine layer's repo may keep `aer-flow` as its technical name — the deliberation explicitly
allows it; whether the public repo/org rename happens at all is #823's last, separate call.

## Rests on

| fact | how we know | if false |
|---|---|---|
| "aer-flow" dictates as "Airflow" | **measured** — the owner's own dictation, the incident that opened #823 | the disqualifier that forced the rename evaporates, but the collision with the category's most famous product remains on paper |
| `ledger` is a well-known developer CLI binary | **measured** — ledger-cli.org, verified during the cull | Ledger-as-brand revives as a candidate; the ledger-noun decision here still stands on the founding-artifact grounds alone |
| `baton`'s only known CLI collision is niche (ConductorOne) | **checked at decision time** — the deliberation's collision search, recorded in the archived report | the same collision class that killed Ledger applies; the name needs re-deliberation, and the report's own "judged survivable" clause is the hook to reopen |

## Consequences

**Easier.** The product can finally be said out loud. The journal's user-facing noun stops
being an implementation term ("the event log", "flow.jsonl") and becomes a product word with
the project's own history inside it.

**Harder.** Every user-facing surface that says "AER Flow" as a product name is now wrong and
must be inventoried (#823's second box) — but which occurrences are *the product's name*
versus *the engine layer's internal name* is a judgment sweep, not a find-replace.
[0002](0002-one-vocabulary.md)'s one-vocabulary rule means "the ledger" lands in the spec's
vocabulary once, and everything else references it — not a scatter of synonyms.

**Obliges us to** execute #823's inventory before adopting either noun on any surface, adopt
"the ledger" wherever the journal is user-facing, keep `aer` as the token everywhere commands
are typed, and treat the repo/org rename as a separate, last decision.

**Relates to** [0002](0002-one-vocabulary.md) (the vocabulary this feeds two words into),
[0013](0013-room-is-the-user-facing-noun.md) (the naming layer this sits above), and
[0008](0008-runtime-streaming-over-append-log.md) (the journal the ledger-noun names).
