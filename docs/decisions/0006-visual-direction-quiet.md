# 0006 — Visual direction is "Quiet"

Status: accepted
Date: 2026-07-22

> **Amendment, 2026-07-25 (#501).** [0028](0028-no-permissive-control-is-the-default.md) adds the half this record left out: colour carries **status**, and *emphasis* carries **consequence**. No permissive control is ever the visual default — not accent-filled, not the sole solid among outlines, not focused on open. It was written because the corpus's own permission mockup drew `Allow once` as the accent-filled primary against an outlined `Deny`, training the reflex its prose forbids. Everything below about the Quiet palette and status legibility stands unchanged.

## Context

Theming was explicitly open. The brief was *"something that looks good, feels good to use, is simple,
and powerful"*, with dark, light and system modes and one brand across desktop and mobile — and no
attachment to the current colours or layouts.

Three directions were prepared and reviewed. They were deliberately made to differ in **register**
rather than hue, because three palettes varying only in accent are not three directions:

| Direction | Register | Accent |
|---|---|---|
| **Quiet** | calm IDE — low chroma, soft rules, generous spacing | dusty teal |
| **Signal** | operator console — near-white/near-black grounds, tight density, crisp hairlines | confident blue |
| **Ink** | warm neutrals with **no brand hue at all**, so the only colour on screen is status | none (ink itself) |

**They were rendered as a component gallery in light and dark, not as screen mockups.** The switcher
shell (#336) does not exist yet, and full-screen renders of a speculative UI get evaluated as
*layout* — the pixel-fidelity of an invented screen swamps whether the colour reads well, and the
renders go stale the moment the layout moves. Rendering the pieces (status markers, list rows, chat,
step nodes, buttons, both densities) asks the question actually being decided.

## Decision

**Quiet.** Desaturated throughout, an accent that never raises its voice.

The reasoning that decides it: this is a tool people leave open all day, so the thing most likely to
make it feel bad is fatigue, not blandness. Signal buys scanning speed at the cost of being tiring
over a long sitting; Ink is the most distinctive and ages best, but leans entirely on typography and
spacing and fails loudest if those are sloppy.

**Directions are stances, not packages.** Ink's rule — *the only colour on screen is status* — is
worth carrying into Quiet even though Quiet was chosen, because it costs nothing and sharpens exactly
the information this product exists to convey.

### Two rules that survive a re-brand

These hold regardless of direction and must not be re-decided per screen:

1. **Semantic status colour is a separate ramp from the brand accent.** Status is this product's
   primary information. Its scale does not move when the brand does — which is what allows the accent
   to change without re-teaching anyone what amber means.
2. **Status reads without colour.** Every state carries a distinct **mark** *and* a word, never hue
   alone. Roughly one man in twelve has some colour-vision deficiency, and the phone case is
   frequently bright sunlight; a coloured dot with nothing else is unreadable in both. The marks
   must differ in *silhouette*, not merely be five different things — five marks that can only be
   told apart once you can see their colour satisfy a literal reading of this rule and fail the
   people it is for.

The states began as #334's split — Working / Needs input / Ready for review / Finished / Failed —
and grew by three under Consequences below (#461): Cancelled, Queued, Unavailable.

## Consequences

**Closes a live defect.** Tasks renders selected checkboxes in *amber* — the same colour Home uses
for "needs your attention". Selected-ness had borrowed the semantic attention colour. Selection now
uses the brand accent; amber is reserved for "needs input".

**Obliges us to** ship one variable font as an asset on both platforms. Avalonia defaults to Segoe
UI and Flutter to Roboto; different faces at different metrics cannot read as one brand, and a
font-family *name* resolves differently per device.

**The face was left open here and has since been chosen** (2026-07-24, #453): **Source Sans 3** for
prose, **JetBrains Mono** for code, both shipped as in-repo assets on each toolkit. Source Sans won
on reading comfort over a long sitting — the fatigue this direction exists to avoid. The mono was
then chosen *against* it rather than to match it: Source's own matched mono is drawn from the same
skeleton, so code blended into prose, and separating code from prose is the thing the pairing has to
do. Selected from rendered specimens in these components, not from description.

That resolution also produced a rule this decision did not anticipate. **Code separates from prose by
the surface it sits on, never by type size** — scaling code up overflows a phone's width and forces
horizontal scrolling, which is worse than the problem it solves. The step is directional rather than
a fixed elevation: code steps *away* from the page ground, down in light and up in dark. Going darker
in dark mode was the first instinct and was rejected on this decision's own reasoning above, which
faulted Signal partly because a near-black dark mode "can feel harsh on OLED at night" — a near-black
code panel imports exactly that onto the surface stared at longest.

**The mark is a drawn shape, not a character** (2026-07-24, #458). This decision said "glyph", and
the token file took that literally — one Unicode codepoint per state. That does not work with the
faces chosen above: `◐`, `▣` and `✕` are absent from Source Sans 3 and `✓` from JetBrains Mono, and
the two shipped faces share **no checkmark and no cross at all**, so no choice of characters can
express this set. A codepoint a font lacks renders as tofu or falls back to whatever the device
happens to have — the per-device resolution this decision rules out, arriving through the back door
on the one element the accessibility rule depends on. The token file therefore names a shape and
each toolkit draws it: a `StreamGeometry` on Avalonia, a `CustomPainter` on Flutter, both authored
on the same 16×16 grid, with CI failing if either lacks a mark the token file names.

That correction also exposed a live defect this decision had not noticed: **"needs input" was
drawing the same dot as idle**, so the one state meaning "this is waiting on you" was shaped
identically to "nothing is happening" and differed only in hue — rule 2 failing in the shipped
product, not hypothetically.

**The five states were not enough** (#461). Reviewing the marks as rendered specimens surfaced three
more that the surfaces actually have to draw, and one worse defect: **a cancelled task reported
itself as "Finished"**, because cancellation has no `WorkflowStatus` of its own and the card
derivation fell through. Cancelled, Queued (which #448's concurrency cap must show) and Unavailable
(the stale-list state) are now part of the model. They share one muted colour rather than earning
three hues — they are states you are *not* being asked to act on, and the ramp's vocabulary is worth
spending on the ones that ask something of you. They differ by mark, which is the channel that
carries the meaning anyway.

The marks are ring / bubble / eye / check / cross / dash / ellipsis / slashed-circle. Two were
replaced after review: a diamond read as a gem rather than an alert, and a page read as "a document"
rather than "review this". **Cancelled is deliberately not a filled square** — a square is
universally a *stop control*, this product needs a real stop affordance for a running session, and a
state that looks like an action is a trap.

**Whether a mark is filled is part of the design, not of each toolkit.** Avalonia's call sites set
`Stroke` and never `Fill` while Flutter's painter filled the same closed path, so one status drew as
an outline on desktop and a solid on the phone. The token file now states it once. This is the
general shape of the risk: the marks are the only part of the system that cannot be generated, so
every property of them that *can* be made declarative should be.

**Does not settle** motion, which belongs with the switcher build rather than a palette decision.

**Not a screen.** This fixes the direction, not the result. When #336 lands the switcher, the tokens
get applied to the real layout and reviewed again.

Related: #345 (the token pipeline, three theme modes and two densities — none of which depended on
which direction won), #336 / #337 (the layouts this first applies to), #334 (the states the ramp
encodes), #283 (index).
