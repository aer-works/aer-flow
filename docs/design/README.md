# The M25 design corpus

Seven documents, authored 2026-07-24 during the design pause that produced decisions
[0012](../decisions/0012-what-aer-flow-is.md)–[0018](../decisions/0018-attention-is-the-primary-signal.md).
They are the **source those records were written from**, and they are here because the records did
not carry all of it.

| # | Document | What it holds |
|---|---|---|
| 01 | [Definition](01-definition.md) | What the product is, who it is for, and the shape of the day job |
| 02 | [Screens](02-screens.md) | Every screen on both surfaces — first run, the daily driver, a gate, failure, templates, shapes, settings, phone |
| 03 | [Interaction depth](03-interaction-depth.md) | What a room does under the surface — turns, attachments, output, expanders |
| 04 | [Workers, models, commands, control](04-workers-commands-control.md) | Vendor/model/effort, commands, skills, and how a run is steered |
| 05 | [Stress test](05-stress-test.md) | The design at 100 rooms, 100 turns, several pipelines, three subscriptions |
| 06 | [Answers](06-answers.md) | Every question the first five passes left open, closed with a decision |
| 07 | [What is actually new](07-whats-new.md) | Nine differentiating claims, eight delights, six table stakes, and how each is demonstrated |

**The mockups themselves are in [`mockups/`](mockups/)** — open any of them in a browser. The
markdown here carries the words; those carry the layout, the states and the visual treatment, which
for a screen design is most of the point.

## Why this exists, and the rule for reading it

A decision record answers *why we chose this*. Much of what was settled during the pause is **not
decision-shaped**: a screen layout, a keyboard rule, a list of small delights, a table of criteria
for when a claim counts as demonstrated. Asked for decision records, the extraction produced decision
records — and everything that did not fit that container was dropped rather than misfiled. Landing
the corpus means the next extraction can be checked against a source that lives in the repo instead
of in a chat transcript and seven private pages.

**Where a decision record and this corpus disagree, the record wins.** The records are the reviewed,
argued-through extraction; several incorporate corrections made after the artifact was drawn, and one
([0015](../decisions/0015-three-kinds-of-needs-you.md)) was corrected again by a live vendor probe.
This is the raw material, not the ruling.

Read it as **design intent that is still being transferred**, not as a specification of what exists.
Nothing here is built. The audit of what has and has not been carried across is `#474`.

## Provenance

Extracted from the local build sources for each artifact (`*_src.html`), not re-fetched — so this is
the text as authored, minus the presentation layer. The artifacts rendered in the product's own Quiet
palette and real subset fonts, which is why they were built as HTML rather than markdown; that
styling is deliberately not preserved here, because the words are the part worth keeping.
