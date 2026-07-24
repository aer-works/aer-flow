# The mockups

The seven design artifacts as originally drawn. **Open any `.html` in a browser** — they render as the
mockups themselves, not as source.

The markdown in the parent directory carries the *words*; these carry the **layout, states and visual
treatment**, which for a screen design is most of the point. A sidebar described as "a presence list,
not a file browser" is a sentence; the mockup shows what that actually looks like next to a running
turn.

| Mockup | Shows |
|---|---|
| [01-definition.html](01-definition.html) | What the product is and the shape of the day job |
| [02-screens.html](02-screens.html) | Every screen, both surfaces — first run, daily driver, a gate, failure, templates, shapes, settings, phone |
| [03-interaction-depth.html](03-interaction-depth.html) | Turns, attachments, output rendering, expanders |
| [04-workers-commands-control.html](04-workers-commands-control.html) | Vendor/model/effort chips, commands, skills, run control |
| [05-stress-test.html](05-stress-test.html) | The design at 100 rooms and 100 turns |
| [06-answers.html](06-answers.html) | Every open question closed, with the affordance drawn |
| [07-whats-new.html](07-whats-new.html) | The nine claims and how each is demonstrated |

## What was changed from the originals

Two edits, both mechanical, so these can live in a repo instead of as published pages:

- **Fonts point at the repo's own subset files** (`src/Aer.Ui/Assets/Fonts/`) instead of being inlined
  as base64. The originals embedded the fonts to be self-contained; here the real files are a few
  directories away, and inlining them cost ~130KB per page. Renders identically from a clone; if you
  move these files, fix the relative path or the type falls back.
- **The publishing frame's runtime script is stripped.** It was harness plumbing, never part of the
  design.

Nothing else is altered — no re-layout, no re-wording, no "cleanup".

## Reading them

These are **design intent, drawn 2026-07-24. Nothing here is built.** They show the product as it
should become, in the Quiet palette ([0006](../../decisions/0006-visual-direction-quiet.md)) and the
real typeface pairing, which is why they were built as HTML: rendering the design *in the product's
own theme* let both be judged at once, and it is how the missing `Elevation` token and the
flat-in-dark bug were caught.

Where a mockup and a decision record disagree, **the record wins** — some corrections were made after
the drawing (notifications never carrying a verdict, rooms rather than "needs you" as the phone's
landing screen, keeping the playful status verbs). The audit of what has and has not been carried
into the repo is `#474`.
