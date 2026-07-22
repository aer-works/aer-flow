# 0006 — Visual direction is "Quiet"

Status: accepted
Date: 2026-07-22

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
2. **Status reads without colour.** Every state carries a distinct glyph *and* a word, never hue
   alone. Roughly one man in twelve has some colour-vision deficiency, and the phone case is
   frequently bright sunlight; a coloured dot with nothing else is unreadable in both.

The five states are #334's split: Working / Needs input / Ready for review / Finished / Failed.

## Consequences

**Closes a live defect.** Tasks renders selected checkboxes in *amber* — the same colour Home uses
for "needs your attention". Selected-ness had borrowed the semantic attention colour. Selection now
uses the brand accent; amber is reserved for "needs input".

**Obliges us to** ship one variable font as an asset on both platforms. Avalonia defaults to Segoe
UI and Flutter to Roboto; different faces at different metrics cannot read as one brand, and a
font-family *name* resolves differently per device. The choice of face is still open — the shipping
mechanism is not.

**Does not settle** motion, which belongs with the switcher build rather than a palette decision.

**Not a screen.** This fixes the direction, not the result. When #336 lands the switcher, the tokens
get applied to the real layout and reviewed again.

Related: #345 (the token pipeline, three theme modes and two densities — none of which depended on
which direction won), #336 / #337 (the layouts this first applies to), #334 (the states the ramp
encodes), #283 (index).
