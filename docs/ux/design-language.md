# Design Language

M19 Phase 1 (#186). The premium bar made concrete rather than left as vibes. Phase 2
materializes the tokens below as the shared theme resource every new surface consumes; Phases
2–4 build with them from day one; **Phase 5's human gate is judged against this document** —
"does each surface hold up beside its reference?" is the review question, not "does it look
nice".

## The reference set (owner-supplied, 2026-07-17)

The products this should feel like, each tied to the part of M19 it informs:

| Reference | What we take from it | Informs |
|---|---|---|
| **Linear** — Inbox & Triage | The decision inbox as the product's center of gravity: dense, calm, keyboard-driven triage; an item is actionable where it's read | Home / inbox (Phase 2) |
| **GitLab** — To-Do List | Cross-project "what needs me" aggregation; honest empty states | Home / inbox (Phase 2) |
| **Dagster** — Launchpad | Form-first config over a real schema: inline validation, sensible defaults, the config always inspectable | Author view (Phase 4) |
| **Stately.ai / XState Visualizer** | Visual ↔ config in lockstep — edit either, the other follows; the graph as an honest projection of the definition | Author view + DAG preview (Phase 4) |
| **GitButler** | A desktop git client that doesn't look like one: proof a developer-tool domain can carry consumer-grade finish | Overall bar (Phase 5) |
| **Neovim / Neovide** | Presentation-agnostic core under a polished skin — the architectural precedent for `Aer.Ui.Core` under `Aer.Ui`; Neovide's motion restraint (smooth, fast, never showy) | Phase 2 seam; motion |
| **n8n** | Clean node-based DAG rendering: readable edges, unambiguous node status, a canvas that stays calm at scale | Task view DAG (Phases 3, 5) |
| **Raycast** | The gold standard for the chrome itself: type discipline, spacing rhythm, subtle depth, keyboard-first with visible hints, animation that confirms rather than performs | Tokens, chrome, keyboard (all phases) |

The set is the owner's (sourced via their Gemini design exploration, adopted here verbatim);
changing it is an owner decision, not an implementation one.

### One product, not a collage (owner directive, 2026-07-17)

The references **calibrate the bar; they do not supply the look**. AER Flow has one identity —
its own — and every reference is consulted for the quality of a specific decision (how Linear
paces an inbox, how n8n keeps a canvas calm), never for its visual style. The failure mode this
rules out: a Home that looks like Linear, a canvas that looks like n8n, an Author view that
looks like Dagster, stitched together. Phase 5's review question (§ above) is therefore
two-sided — "does each surface hold up beside its reference?" **and** "do all surfaces
unmistakably belong to the same product?"

What makes the identity portable: everything below — the tokens, the one status system, the
motion rules, the vocabulary — is the product's look, defined once, owned here, and rendered by
whatever client exists. The desktop app (this milestone), a remote client (candidate M20), and
any eventual web surface implement *this document*, not each other and not the references; a
user moving between them should experience the same product wearing different windows. That is
the same boundary Phase 2 enforces in code (`Aer.Ui.Core` under `Aer.Ui`), applied to design:
the identity lives in the system, not in any one skin.

Two corollaries (owner, same directive):

* **Fit over fidelity.** Anything in a reference that doesn't make sense for this domain is
  skipped without apology — being unlike the reference is fine; being unlike ourselves is not.
* **Mine them for capabilities, not just polish.** When a reference offers a genuinely great
  affordance we lack (say, a Raycast-style command palette over tasks and actions, or Linear's
  one-keystroke inbox triage), that's worth pursuing: adapt it to our identity and vocabulary,
  fold it into the phase it naturally belongs to if it fits the phase's scope, and surface it
  to the owner as a candidate if it would grow scope. The reference set is a quality bar *and*
  a hunting ground — what it is not is a style guide.

## Design tokens

Named tokens, defined once as the shared theme resource (Phase 2), consumed by name everywhere.
A surface using a raw hex value, ad-hoc font size, or magic pixel number is a defect from
Phase 2 onward. Exact values are fixed when the resource is materialized; the *system* — the
names, scales, and rules — is fixed here.

### Type

Bundled **Inter** (app-shipped, not system-dependent) for UI text; the platform monospace stack
for artifact/config/transcript content. A single modular scale, nothing off-scale:

| Token | Size / weight | Use |
|---|---|---|
| `Type.Display` | 24 / semibold | View titles (Home, task name) |
| `Type.Heading` | 17 / semibold | Section and card headings |
| `Type.Body` | 14 / regular | Default UI text |
| `Type.BodyStrong` | 14 / medium | Emphasis within body (step names, labels) |
| `Type.Caption` | 12 / regular | Metadata, timestamps, hints, shortcut hints |
| `Type.Mono` | 13 / regular, monospace | Artifact previews, transcripts, ids, paths |

### Spacing

A 4px base grid: `Space.1` = 4 through `Space.8` = 32 (4, 8, 12, 16, 20, 24, 28, 32). All
padding, gaps, and margins are grid values; component internals default to `Space.2`/`Space.3`,
view gutters to `Space.4`/`Space.6`.

### Color

Semantic tokens only — surfaces never reference palette entries directly, which is what makes
dark + light two mappings of one system rather than two themes:

* **Neutrals**: `Color.Background` (app), `Color.Surface` (cards/panels),
  `Color.SurfaceRaised` (popovers/dialogs), `Color.Border`, `Color.BorderSubtle`,
  `Color.Text`, `Color.TextSecondary`, `Color.TextDisabled`.
* **Accent**: `Color.Accent` (+ `Hover`/`Pressed`/`SubtleBg`) — one accent, used for primary
  actions, selection, and focus; restraint is the premium tell.
* **Status** — the one status system, used identically in the DAG, task cards, inbox items,
  attempt lists, and anywhere else state appears. Color is never the only carrier: each status
  pairs with its icon (and its plain word, per the vocabulary map):

| Token | Meaning (plain word) | Icon shape |
|---|---|---|
| `Status.Running` | Working | animated spinner/pulse |
| `Status.NeedsYou` | Waiting for your review | filled attention dot/badge |
| `Status.Succeeded` | Finished | check |
| `Status.Failed` | Failed | cross |
| `Status.Idle` | Not started / skipped | hollow dot |
| `Status.Stale` | Out of date | back-reference/refresh mark |

`Status.NeedsYou` is the loudest thing in the system (principles §4: what needs the human
outranks everything); `Status.Running` reads as calm activity, not alarm. Both themes meet
WCAG AA contrast for text and status-on-surface pairings.

### Shape and depth

`Radius.Small` = 4 (inputs, chips), `Radius.Medium` = 8 (cards, panels, popovers) — two radii,
no rounded-rectangle zoo. Depth by elevation tokens (`Elevation.Flat` / `.Raised` / `.Overlay`)
combining subtle shadow + surface color step, tuned per theme (dark relies more on surface
steps than shadows). Borders are 1px and quiet; depth separates, borders delineate.

## Motion

Animation confirms what happened; it never performs (Raycast/Neovide restraint). System-level
reduced-motion preference disables all non-essential motion.

* **Durations/easing**: `Motion.Fast` = 120ms (hover, pressed, focus), `Motion.Base` = 200ms
  (expand/collapse, view transitions, selection moves), `Motion.Slow` = 320ms (status
  celebrations, one-shot). Standard ease-out for entrances, ease-in-out for moves; nothing
  bounces.
* **View transitions**: navigation cross-fades + slight slide at `Motion.Base`; drill-in panels
  expand in place — the DAG doesn't jump when a step opens.
* **Hover/pressed**: every interactive control has visible hover and pressed states
  (`Motion.Fast` color/elevation shifts) — no dead-feeling clicks.
* **Live status**: a step changing status animates the change (icon swap + brief color
  settle) so the eye is drawn to what moved; `Status.Running`'s pulse is slow and subtle.
* **Loading**: skeleton placeholders for structure (cards, panels), inline spinners for
  actions; anything possibly >150ms acknowledges itself — no frozen frames (principles §6).

## Chrome

Custom-themed window chrome consistent across OSes to the extent Avalonia allows, an app icon
that survives small sizes, crisp at high DPI. Keyboard hints rendered in `Type.Caption` beside
primary actions (Raycast-style), not hidden in a help screen.
