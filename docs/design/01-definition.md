# What AER Flow is

> **Design corpus — started 2026-07-24 during the M25 design pause, kept current since.**
> The 2026-07-24 material below is unchanged; a 2026-07-25 amendment note and two new noun
> entries for M27 (personas, room orchestration) are marked where they land, not blended in
> silently. This is the *source* the decision records were written from; where a record and this
> document differ, **the record wins** — it is the reviewed extraction, not the ruling. See
> [`README.md`](README.md#kept-current-not-frozen-added-2026-07-25) for why this stopped being a
> closed snapshot.

---

AER Flow — what it is

Product definition · draft for markup

## AER Flow

A drop-in replacement for Claude Code that puts more than one model in the room , and lets you leave the room without losing it.

Agreed 24 July 2026. There were 11 decisions about parts and 9 journeys about behaviours, but nothing saying what the product is — and every defect from the last manual run traced back to that absence, not to wrong code. This is that missing document; the screen designs derive from it.

### The three things it has to be

Each of these carries weight the others don't. They are how arguments get settled later: a proposal either serves one of them, or it doesn't belong.

The baseline

##### A real coding agent

You point it at a directory and talk to it; it edits, runs, and reports. This is the hardest constraint — drop-in means someone moving across from Claude Code loses nothing they had.

The shape

##### A messenger, not a console

Workers are presences in a sidebar — present or not, busy or idle — that you talk to one at a time or address together, and reach from wherever you are rather than only at the machine they run on.

The leverage

##### Shapes you can draw

Defining a repeatable piece of work should be visual and quick — draw it once, save it, start it in a click, and look at its shape again only if you want to.

### What it is, and what it isn't

The right side is the useful half. A definition that rules nothing out isn't a definition — it's a wish.

It is

- A coding agent you point at a folder. The single-agent path stays first-class and fast. Multi-model is an escalation, never a tax on the simple case.

- A room. More than one worker in one conversation, on your own subscriptions, live.

- Operable from elsewhere. Start on the desktop, decide from the phone. Remote is a property of the product, not an add-on.

- Honest about state. Anything running is visible, nameable, and interruptible.

It isn't

- An API product. It runs against subscriptions through vendor CLIs. No key handling, by design.

- A workflow builder you live in. Graphs are for authoring and inspecting templates, not for daily work. You should be able to draw one easily, save it, and never look at it again unless you want to.

- A router or a judge. Flow never reads conversation content to decide anything. Discipline in Flow, intelligence in Workers.

- A team tool. One operator, several agents. Not multiplayer.

### The nouns

Small on purpose. Every noun added here becomes a thing the person has to learn, so each has to earn its place.

> **Amendment, 2026-07-25 — [0013](../decisions/0013-room-is-the-user-facing-noun.md) renamed
> this noun.** Wherever "Session" appears below as the user-facing noun for a conversation with
> one or more workers in it, read **Room** — the model (participants, one directory, its own
> history) is unchanged, only the word moved. "Session" now names something narrower: the vendor
> CLI's own resumable thread, an adapter concern that is never presented as the thing you opened.
> The **Settled** section further down states the opposite outcome ("room" never enters the
> vocabulary) — that specific claim is superseded and is left as written below rather than
> edited, per [0010](../decisions/0010-skills-and-advisor.md)'s own precedent for a corrected
> clause. Two new nouns from the same M27 pass — **Persona** and **Room orchestrator** — are
> appended after Template below.

Session A conversation against a directory, with one or more workers in it . The main noun — what you start, return to, and what the sidebar lists. There is deliberately no second noun for "a session with more than one worker": adding a worker changes who is present, not what kind of thing you have.

Worker One vendor's CLI running under your subscription. `claude`, `agy`. Interchangeable by design , and present or absent like a person in a thread.

Gate Where work stops and asks you — the only thing allowed to block , and the unit the "needs you" list carries and the phone answers. Comes in three kinds, because they ask different things of you: a permission (may I run this command), a decision (which of these), and an action (review this). Two already exist in the engine as ReadyForReview and NeedsInput ; permission is the one genuinely new kind.

Template A saved shape of work — draft→review→gate — defined on a graph and started in one click . The graph is how you author and inspect it, never where you live day to day.

Persona *(added 2026-07-25, M27)* A named, saved binding of a Skill onto a Worker's vendor/model/effort chip, plus a permission grant and a voice — a preset over the worker chip, not a new axis beside it. Eight predefined (Scout, Courier, Scribe, Artisan, Debugger, Auditor, Advisor, Architect), each pinning a real skill to a model-purpose × effort cell; cloning one and renaming it forks permanently from the built-in default. See [02-screens.md](02-screens.md) for the picker and the creation flow.

Room orchestrator *(added 2026-07-25, M27)* A pinnable role: which participant in the room is authorized to call `aer decide` on another's gate, standing in for the human. Human-assigned only, blocked while a gate is open, one per room at a time, and never retroactive — a decision already made under the previous holder keeps its attribution. See [02-screens.md](02-screens.md) for the reassignment control.

### The one flow that matters

If this path is good, the product is good. Everything else is in service of it — and today it is the path that breaks.

flowchart LR
A["Point at a folder"] --> B["Talk to one agent"]
B --> C{"Worth more
than one?"}
C -- no --> B
C -- yes --> D["Add a participant
to the room"]
D --> E["They work
you watch"]
B --> E
E --> F{"Needs
you?"}
F -- no --> E
F -- yes --> G["Gate:
decide"]
G -.-> H["from the phone"]
G -.-> I["from the desktop"]
H --> E
I --> E
E --> J["Done"]

Two claims worth testing. The chat is where you stay — escalating to a second worker must not move you to a different screen. And a gate is answerable from whichever surface you happen to be holding, which is why remote can't be bolted on later.

### A session's life

Written as states because the last run produced two surfaces disagreeing about which state a session was in — the header said "no task open" while the thing was running.

stateDiagram-v2
[*] --> Idle: created
Idle --> Working: you send / it runs
Working --> NeedsInput: hits a gate
NeedsInput --> Working: you decide
Working --> Finished: completes
Working --> Failed: errors
Working --> Cancelled: you stop it
Finished --> Working: you send again
Cancelled --> Working: you send again
Failed --> Working: retry

One source of truth per session. Every surface — switcher row, header, inbox, phone — renders this state and nothing derived independently. Cancelled and Failed are states, not absences: a stopped session must never read as "Finished."

### The surface this implies

Not the current app — what the definition above asks for. Sessions always visible, the room in the middle, and a gate answered where you already are.

● AER Flow

▤ ✎ ◈ ⚙

Sessions + New

◗ aer-flow Needs you · 2 workers

◔ payments-api Working · 4m

✓ docs-sweep Finished · 1h

✕ migration Failed · 3h

— spike-cache Cancelled · 1d

aer-flow claude + gemini  ·  + Add participant

you Rework the switcher so a new session shows up immediately.

claude Two causes — the list only refreshed at startup, and a task registered only on a successful run. Patch ready.

Needs you · gemini reviewed Approve the change to the push fan-out?
Approve Changes Reject

Reply… ⏎

What changed versus today. The gate is answered inline in the conversation rather than on a separate decision screen; adding a worker is a control in the room's header rather than a different noun to create; and every session's state is legible without leaving the one you're in.

### Settled

Four questions, answered. These are now constraints on everything downstream, not opinions to relitigate per screen.

Resolved

#### Graphs author templates; they are not the day job

What makes visual workflow definition worth having is that it is easy — so keep that, and put it where it pays: you draw a shape once, save it as a template , and start a session from it in a click. You can visualise a running session's shape whenever you want, including after it has started. What you don't do is live on a canvas.

Consequence: the DAG stops being a destination and becomes two things — an authoring surface and an optional view of a session. A meaningful slice of the backlog is scoped against the old assumption and has to be re-read.

Resolved

> **Superseded, 2026-07-25.** [0013](../decisions/0013-room-is-the-user-facing-noun.md) reversed
> this specific outcome: the noun is **Room**, not Session. The point underneath it — no second
> noun for "a session with more than one worker," adding a worker changes who's present, not what
> kind of thing you have — stands unchanged; only which word won. Left as written below; see the
> amendment on **The nouns**, above.

#### One noun, not two — a session just has more workers

"Room" is gone. A session with two workers is still a session; adding one changes who is present , not what kind of object you're holding. One fewer concept to teach, and it kills the question "is this a session or a room?" before anyone can ask it.

Consequence: decision 0001's two nouns stay workflow and session , and "room" never enters the vocabulary. Cheaper to drop now than after it's in a UI.

Resolved

#### Rip out the UI layers only

The engine, adapters, daemon and protocol stay — they are tested and were never the thing that failed. Aer.Ui and Aer.Mobile get rebuilt against this definition rather than patched toward it.

Consequence: open UI issues are no longer bug reports against code that will exist. They become requirements on the rebuild, or they get closed.

Resolved

#### Both surfaces get designed before either gets built

Desktop and phone are genuinely different views of one product, not one layout at two widths — so the mockups cover both, and the differences are decided on paper rather than discovered in code.

Definition agreed; screen-level design in progress. This becomes a numbered decision in docs/decisions/ , the nine journeys get re-derived from it, and the whole backlog is audited against it — including issues that stop earning their place.
