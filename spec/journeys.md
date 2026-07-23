# AER — Product journeys

> What AER promises a person. The behavioural spec says what the **engine** does; this says what the
> **product** does — the layer that was never written, and where every defect the M25 evaluation
> found actually lives.

This is a living document, not a versioned snapshot. Its home was chosen for legibility, not for
adjacency to the frozen behavioural specs; the doc scrub (#367) may relocate it further. It is the
artifact issues cite and the target-design spec rewrite is written against.

## Reading these

- **A journey is a promise stated as a person's outcome that crosses surfaces** — not a screen, not a
  feature. Milestones were capability-shaped; no milestone was ever *"start work at your desk and
  approve it from your phone,"* so that path was nobody's deliverable. It is the broken one.
- **Steps illustrate, they don't certify.** Each journey's *Passes when* line is the acceptance bar;
  the *Path* just shows one way there and is explicitly **not** a completion checklist.
- **Status is machine-kept.** A journey's status is derived from its test (#313) and enforced by CI
  (#314): a recorded status that contradicts its test breaks the build, and a journey with no test
  cannot claim to pass. Human-gated journeys carry a dated sign-off the same check guards. This is the
  teeth that stops these promises from rotting the way the old docs did.
- **A milestone is done when its journeys pass.** Passing means the automated test drives the *real*
  surface end to end; only where something genuinely needs a person — live vendor auth, physical
  device pairing — does a dated human sign-off stand in, flagged per journey.

## Status today

Baseline **2026-07-22** — **Fails 7 · Partial 2 · Passes 0.** The honest starting line the rebuild
moves. Written against the target product; today's product fails most of these, which is the point.

---

## J1 — Start work on the desktop, approve it from your phone

**Status:** Fails — automated

You kick off a piece of work at your desk, walk away, and later approve it from your phone — without
going back to the machine.

- **Spans** — desktop → daemon → paired phone · *seam: the phone's decision inbox ↔ the daemon's open
  work*
- **Passes when** — a desk-started run that pauses at a decision gate appears on the paired phone;
  approving it there advances the run to completion, and the desktop reflects the new state without a
  manual reload.
- **Path** *(illustrative)* — start a review-run on the desktop · it pauses at its gate · the phone
  shows it waiting on you · you decide and approve · it resumes and finishes · the desk updates.
- **Today** — the phone's inbox is scoped to the daemon's single open task, so a desk-started run
  often isn't there to approve.
- **Serves** — #335, #319, #330

## J2 — Open a folder, talk to an agent, and grow the room without leaving the chat

**Status:** Partial — automated + live

You point the product at a directory and start talking to an agent. When it's worth more, you bring
another worker into the room or spin off a gated review as a child — and the chat stays the place you
are. It never becomes the review.

- **Spans** — desktop · *the room model: a session is a multi-participant conversation that spawns
  child sessions (decisions 0001 / 0008 / 0009)*
- **Passes when** — from a live chat you can either **add a second worker to the same room** or
  **spin off a clearly-marked child** (draft→review→gate) that reports its result back into the chat;
  the chat stays live throughout (async), and the child shows both inline and in the inbox, marked as
  a child.
- **Path** *(illustrative)* — open a folder · chat with the agent · add a reviewer to the room, or
  spin off a two-vendor review as a child · it runs (you can watch and interject) · it reports back at
  its gate.
- **Verify** — spawn / host / gate and async liveness automated; the live-vendor quality of a review
  is a human / live-smoke check (vendor auth can't be automated).
- **Today** — sessions and review-runs exist only in isolation; the room model — several workers,
  spawn-and-hold child sessions, staying live while a child runs — isn't built, and the singleton
  daemon can't hold a chat and its child at once.
- **Serves** — #333, #335, #340, decisions 0001/0008/0009

## J3 — Come back after a day and immediately see what needs you

**Status:** Fails — automated

You reopen the product after being away and, without hunting, see the things waiting on your decision
first — held apart from what's still running and what already finished.

- **Spans** — desktop + phone · *seam: list UI ↔ projection / state*
- **Passes when** — on reopening either surface, work is legibly separated into **waiting on you** /
  **running** / **finished**, with waiting-on-you first; and a **failed** piece of work reads as
  failed, not as "finished."
- **Path** *(illustrative)* — reopen the app · the first thing you see is the short list of decisions
  waiting · running work is visible but secondary · finished work (failures correctly labelled) is
  available, not in your face.
- **Today** — a running task shows the phone "Nothing is waiting on you" and nothing else (#337);
  failed tasks list as `Terminal`/finished (#355).
- **Serves** — #337, #355, #334

## J4 — Pair a phone from scratch on an ordinary network

**Status:** Partial — human pairing

A brand-new phone on the same normal Wi-Fi as your machine — not enrolled in any tailnet — pairs and
starts working together in one pass.

- **Spans** — phone + daemon · *seam: pairing / discovery, the tailnet-vs-LAN address gap*
- **Passes when** — a fresh phone on the same LAN reaches the daemon at an address it's actually
  given, completes the handshake within the code's lifetime, and makes a first authenticated
  round-trip; a daemon port change doesn't permanently strand it.
- **Path** *(illustrative)* — fresh phone · enter the reachable host · mint and enter the code in one
  pass · handshake completes · first authenticated call succeeds.
- **Verify** — the real cross-device pairing is a human walk (physical device on a real LAN, per the
  runbook); the code-lifecycle and port-stability logic is automated.
- **Today** — works on a tailnet; on a plain LAN the daemon advertises only its Tailscale address, the
  phone persists host:port verbatim with no rediscovery, and a restarted daemon on a new port can
  strand every device (#347, #349).
- **Serves** — #347, #349, #346

## J5 — Start the same piece of work from either surface and see it on both

**Status:** Fails — automated

Whether you start something at your desk or on your phone, it shows up live on the other — the same
object, the same state, not two disconnected views.

- **Spans** — desktop ↔ daemon ↔ phone · *seam: the broadcast path*
- **Passes when** — work started on one surface appears on the other with no manual refresh; both
  render the same object identity and its live status; and this holds for **every** kind of work, not
  just chat.
- **Path** *(illustrative)* — start work on the desktop · the phone shows it appear and track state
  live · (and the reverse) · both agree on what it is and where it's at.
- **Today** — desktop-started work never broadcasts, so paired phones never see it (#330); starting a
  non-chat template from the phone leaves it on "No task is open" while the daemon reports it running
  (#348).
- **Serves** — #330, #348, #335

## J6 — Deny a tool and have it actually blocked

**Status:** Fails — automated · safety

When you withhold a capability from a piece of work, the work genuinely cannot use it — the permission
is enforced, not merely displayed.

- **Spans** — engine · *seam: permission grant ↔ enforcement*
- **Passes when** — a capability the user has not granted cannot be exercised by the worker; an attempt
  to use a denied tool is refused at the boundary and recorded — not silently allowed.
- **Path** *(illustrative)* — start work with a tool withheld · the worker attempts to use it · the
  attempt is refused and surfaced · the withheld capability never runs.
- **Today** — **safety defect.** Grants are unenforced — a shell-denied session ran `hostname` and
  returned the real value (#331). Arguably gates the rebuild. The same depth/count and turn ceilings
  that bound worker spawning and worker dialogue (decisions 0001 / 0009) are the other half of this
  rail.
- **Serves** — #331, decision 0004

## J7 — Lose the connection and get back to work

**Status:** Fails — automated + human

When the phone loses the daemon — network drop, daemon restart, changed port — it tells you the truth
about what happened and offers a recovery that actually works.

- **Spans** — phone ↔ daemon · *seam: connection state ↔ recovery action*
- **Passes when** — a disconnected phone shows a truthful, human-readable state (not a raw exception)
  and the offered recovery action genuinely restores the connection or leads to re-pairing — no
  dead-end button that can't succeed.
- **Path** *(illustrative)* — the connection drops · the phone shows a clear "disconnected, here's
  why" state · the offered action (reconnect / re-pair) actually restores service.
- **Verify** — the state / action logic is automated; the real-device network-drop walk is a human
  check.
- **Today** — a disconnected phone shows a raw Dart exception (`errno 111`); the only offered action,
  Reconnect, can't succeed; real recovery is hidden as "Forget pairing" — which itself doesn't fully
  revoke (#346, #349).
- **Serves** — #346, #347, #349

## J8 — Open it for the first time and know what to do

**Status:** Fails — automated

The first time you launch the product — no work, no pairings, nothing — each surface tells you what it
is and gives you a real first action, not a blank wall.

- **Spans** — desktop first-run + phone first-run (pre-pairing) · *seam: empty state ↔ a real entry
  point*
- **Passes when** — on a truly empty first launch, each surface presents a clear primary next step that
  leads to a real outcome (open a folder / start work on desktop; pair to a machine on phone) — not an
  empty list or a "Nothing is waiting on you" dead-end.
- **Path** *(illustrative)* — fresh install · open desktop — it invites you to open a folder / start
  your first work · open phone — it invites you to pair · you reach a real first action without a
  manual.
- **Today** — the empty first run has no designed onboarding; the phone's empty state is the same
  "Nothing is waiting on you" dead-end (#337); no first-run guidance exists.
- **Serves** — #337, #338, #339

## J9 — See what you're spending across every vendor

**Status:** Fails — automated

You can see usage across all the vendors AER drives — in one place — so multi-worker work and
worker-to-worker exchanges don't spend blindly.

- **Spans** — every adapter · *home on the dedicated activity surface (#360) / Settings (#338)*
- **Passes when** — a single view shows usage across every vendor AER orchestrates, best-effort per
  what each vendor's CLI exposes — the real cost lever for the invoke-per-turn runtime (decision 0008).
- **Path** *(illustrative)* — open the usage view · see per-vendor consumption across your workers · it
  reads the same whichever runtime path is in play.
- **Verify** — aggregation and display automated; per-vendor figures are only as rich as each CLI
  exposes (best-effort, and labelled as such).
- **Today** — no cross-vendor usage view exists; usage is invisible across the workers AER runs.
- **Serves** — #360, #338, decision 0008

---

The starting set is deliberately small and **grows** as milestones add promises. Each journey earns a
test (#313); its status is enforced against that test (#314); a milestone is done when its journeys
pass. Failure and safety promises (J6, J7) are first-class, not edge-conditions inside the happy path.
