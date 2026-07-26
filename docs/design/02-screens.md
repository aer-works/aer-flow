# Screens — every shape the product needs, both surfaces

> **Design corpus — started 2026-07-24 during the M25 design pause, kept current since.**
> The 2026-07-24 material below is unchanged; four new screens for M27 (personas, room
> orchestration) are appended after **Phone**, marked with when they landed. This is the
> *source* the decision records were written from; where a record and this document differ,
> **the record wins** — it is the reviewed extraction, not the ruling. See
> [`README.md`](README.md#kept-current-not-frozen-added-2026-07-25) for why this stopped being a
> closed snapshot.

---

AER Flow — screens

Screen design · draft 3 · complete set

## Screens

Every shape the product needs, on both surfaces. Desktop and phone are different views of one thing, not one layout at two widths — where they diverge, the divergence is written down here rather than discovered in code.

First run The daily driver Two workers, a gate
When it fails Starting from a template Drawing a shape
Settings Phone The calls

M27 addition, 2026-07-25 — The persona picker Persona creation Room-header controls Workflow toggle

### First run

One screen, one action, and the answer to the question that actually breaks first installs: are my CLIs even being found? Onboarding and diagnosis are the same screen because they are the same worry.

Desktop · nothing yet

AER Flow

▤ ◱ ⚙

Point AER Flow at a folder

A room is a conversation about one folder. Open one and start talking; add a second worker whenever it is worth it.

Choose a folder… Start from a template

Workers found ✓ claude ✓ agy — agy not installed

The readiness line is the real feature here. Every vendor CLI is authenticated outside this product, so "it can't find claude" is the most likely first failure and the least self-evident. Showing what was detected — and naming what wasn't, without treating it as an error — turns a dead end into a fact. Nothing else is on this screen. No tour, no sample project, no checklist: one sentence explaining what a room is, and the button that makes one.

### Desktop · the daily driver

One room, one worker. This is the screen you look at most, so it has to be boring and fast. Nothing here is a mode you enter.

Desktop · one worker

AER Flow

▤ ◱ ⚙

Rooms + New

◔ aer-flow Working · claude

◗ payments-api Needs you

✓ docs-sweep Finished · 1h

— spike-cache Cancelled · 1d

aer-flow claude + Add participant

you Why does a new room not show up in the list?

claude Two causes. The list only refreshed at startup, and a room was registered only when its first run returned 2xx — so a refused run left a real folder nothing knew about.

you Fix both.

claude · working Editing MainWindow.axaml.cs …

Reply… ⏎

The sidebar is a presence list, not a file browser. Name, state mark, and what that worker is doing right now — the three things that let you decide whether to switch. It is always there, so a room you are not watching is never invisible. "+ Add participant" is a control in the header , not a new object to create: that is what keeps "room" a single noun.

### Desktop · two workers and a gate

The escalation. You added a reviewer, it disagreed, and now something needs you. Note what did not happen: you did not move to a different screen.

Desktop · two workers · decision inline

AER Flow

▤ ◱ ⚙

Rooms + New

◗ aer-flow Needs you · 2 workers

◔ payments-api Working · 4m

✕ migration Failed · 3h

aer-flow claude agy +

claude Patch ready: refresh both lists through one call, and register a room when it is created.

agy · reviewing The refresh is right. But the picker path is not the only entry point — the CLI still registers only on success.

Needs you Apply agy's correction before continuing?
Apply Skip Ask claude to respond

Reply… ⏎

Shape Hide

draft · Artisan

↓

review · Auditor

↓

gate · you

↓

apply

A gate is answered where it was raised. It renders as a turn in the conversation, because that is where the context is — the argument you are ruling on is directly above it. It also appears on the phone and in the "needs you" filter; same object, several entry points, never several copies of the state. The shape panel is optional , showing where this room is in the template it was started from — dismissible, and absent entirely for a room you just started by talking.

A workflow step's binding can be a bare vendor name or a named Persona — the same duality as a room's worker chip. This document shows the Persona-bound case across template and shape views for clarity, though bare vendor bindings remain fully valid.

### When it fails

A failure is a state, not an absence. The rule this screen exists to enforce: a failed room reads as failed everywhere, and the reason is on screen rather than behind a drill-in.

Desktop · failed room

AER Flow

▤ ◱ ⚙

Rooms + New

✕ migration Failed · 3h

◔ aer-flow Working · claude

migration claude

you Run the schema migration.

Failed · claude · 3h ago The worker exited before finishing.
migrate: connect ECONNREFUSED 127.0.0.1:5432
at TCPConnectWrap.afterConnect

Try again Ask claude to fix it Show full output

Reply… ⏎

The error text is the content, not a detail. A failure that says only "failed" forces a hunt through logs; the first few lines of what actually broke are almost always enough to know whether it is your problem or the agent's. Full output stays one click away for when it isn't. "Ask claude to fix it" is the interesting affordance — the worker that failed is right there and has the error in context, so the most common next action should not require you to retype the problem.

### Starting from a template

Shaped work has to be about as cheap to start as a bare conversation, or nobody will ever use it. Three fields and a button.

Desktop · new room from a template

AER Flow · New room

▤ ◱ ⚙

Templates + New

◆ draft → review 2 workers · 1 gate

◆ just talk 1 worker

◆ triage sweep 3 workers

draft → review Edit shape

Folder

~/source/repos/aer/aer-flow    Choose…

Who runs it

draft · Artisan     review · Auditor

Start room Save as my default

A template names the shape and the roles. It does not name the folder — that is chosen per room.

"Edit shape" is the only door to the editor — and the editor edits a template, never a running room. That separation is what stops the graph creeping back into the daily path. A template deliberately does not remember a folder, so one shape serves every project.

### Drawing a shape

The one place the product asks you to think structurally. It gets the strongest opinion in this document: it is not a freeform canvas.

Desktop · template editor

AER Flow · draft → review

▤ ◱ ⚙

Templates + New

◆ draft → review editing

◆ just talk 1 worker

draft → review Done

draft Artisan ask me first ○

+ step

review Auditor ask me first ●

+ step

apply Artisan ask me first ○

+ step

Each step runs after the one above it. Turn on "ask me first" to put a gate before a step.

Preview

draft · Artisan

↓

gate · you

↓

review · Auditor

↓

apply · Artisan

A list that renders as a graph, not a canvas you drag on. Freeform node editors are the reason visual workflow tools feel like work: you spend your attention on layout — arranging boxes, routing edges — rather than on the actual decision, which is who does what, in what order, and where do I want a say. A vertical list of steps expresses every shape this product realistically needs, is keyboard-navigable, diffs cleanly in version control, and cannot produce an unreadable tangle.

A gate is a property of a step, not a node you add. "Ask me first" is the entire mental model for human oversight — one toggle, in the place you are already looking. That is also what makes the shape readable at a glance: the gates are the highlighted rows.

The cost of this choice is that genuinely parallel fan-out — three workers at once on the same input — needs a second affordance later. That is a real limitation and worth paying, because it is rare and the tangle is not.

### Settings

Three groups, one screen, no tabs. Settings should be somewhere you visit rarely and leave quickly.

Desktop · settings

AER Flow · Settings

▤ ◱ ⚙

Workers

claude ✓ found · signed in

agy ✓ found · signed in

agy not installed · how to add

AER runs whichever CLI is already signed in on this machine. It never stores keys.

Your phone

Pixel 8 paired · last seen 2m ago   Unpair

Pair another device Show code

Appearance

Theme Light Dark System

Density Comfortable Compact

"Workers" is the same information as the first-run readiness line , in the place you would go looking for it later — one source, two contexts, so a CLI that stops working has an obvious home. A missing worker offers a way to fix it rather than only reporting its absence. The line about never storing keys sits here because this is where people expect to be asked for one, and the answer is that they never will be.

### Phone

Same product, held differently. Rooms is the root here as on the desktop — you are visiting your work, not working a queue.

Phone · first run · rooms · a gate · a notification

9:41 ▮▮▮

Connect First run

Open AER Flow on your computer, go to Settings → Your phone, and enter the code it shows.

4 7 2 · · ·

Codes expire after a minute.

Scan a QR instead

9:41 ▮▮▮

Rooms 2 need you · 1 running

◗ aer-flow Needs you · claude + agy

◗ payments-api Needs you · schema change

◔ docs-sweep Working · 4m

✕ migration Failed · 3h

— spike-cache Cancelled · 1d

Rooms Needs you Settings

9:41 ▮▮▮

‹ aer-flow claude + agy

claude Patch ready: refresh both lists through one call.

agy The CLI entry point still registers only on success.

Needs you Apply agy's correction?
Apply Skip

Reply… ↑

9:41 ▮▮▮

Locked Notification

AER Flow · aer-flow agy corrected claude's patch — a decision is waiting.
Open

AER Flow · migration Failed — the worker exited before finishing.

A notification says enough to judge whether it is worth opening, and never decides anything.

The phone's first run is pairing, and nothing else. It has no folders of its own and no CLIs installed, so until it is connected to a computer there is genuinely nothing it can do — pretending otherwise with an empty rooms list would be worse than saying so. Notifications inform, they never decide: one tap opens the gate beside the argument you are ruling on, because approving an agent's work from a lock screen is one mis-tap from approving something you never read. Template authoring is out of scope for the phone's first version , not ruled out — a small-screen shape editor is an interesting problem worth returning to, and the step-list model above is far more portable to a phone than a canvas would have been.

### M27 addition, 2026-07-25 — four new screens for personas and room orchestration

Everything below this line is new: personas and the room orchestrator, decided in the M27 design
pass. Drafted with Gemini (`gemini-3.1-pro-high`, prompt at
`aer-agy-loop-scratch/design-m27-screens-prompt.md`), then corrected against the real decision
text — the draft had invented a specific Claude model version string, which is exactly what
[0023](../decisions/0023-effort-and-models-are-named-by-behaviour.md) says the UI must never do,
plus a mis-cited decision and an invented permission field. A matching mockup lives in
[`mockups/02-screens.html`](mockups/02-screens.html), appended the same way.

**A bare worker chip — just a vendor name, exactly as drawn in "The daily driver" and "Two workers,
a gate" above — stays completely valid.** A Persona is an optional named preset over that chip, not
a replacement for it; adding a worker with no Persona attached looks and works exactly as those
earlier screens already show. What follows describes the persona-bound case specifically, and — a
correction made mid-pass, not part of the original draft below — deliberately does **not** repeat
vendor, model tier and effort in the chip's visible label the way an earlier revision of this
section did. A named preset exists precisely so a person doesn't have to re-read its raw axes every
time they see it; showing `Artisan` and showing `Artisan · claude · balanced · careful` in the same
breath defeats the point of naming it. The raw axes live in the popover below, one tap away, not
duplicated in the label.

### The persona picker on the worker chip

Today's worker chip (0017) is three dependent dropdowns: vendor gates model gates effort. Picking a
Persona sets all three plus skill plus permissions in one gesture, without destroying the
underlying worker chip — and the chip's visible label stays as short as the bare-vendor case it
extends: the Persona's name, nothing else, until you open it.

Desktop · worker chip persona popover

```
AER Flow · aer-flow
▤ ◱ ⚙

Rooms + New            aer-flow  [👑 Artisan ▼]  [+ Add participant]
                      ┌────────────────────────────────────────────────────────┐
                      │ Persona Preset                                         │
                      │ ● Artisan (implementation & refactor)                 │
                      │ ○ Scout   ○ Courier   ○ Scribe   ○ Debugger            │
                      │ ○ Auditor ○ Advisor   ○ Architect                      │
                      │                                                        │
                      │ Overrides for this room                                │
                      │ Skill      [ implementation & refactor               ▼]│
                      │ Vendor     [ claude                                  ▼]│
                      │ Model tier [ balanced                                ▼]│
                      │ Effort     [ careful                                 ▼]│
                      │ Grant      [ Project ∩ Session ∩ Step (Read, Write)  ▼]│
                      │ Voice      [ Pragmatic implementer, concise code     ]│
                      │                                                        │
                      │ Status: Artisan (preset intact)                        │
                      │ [ Reset to preset ]             [ Save as new Persona ]│
                      └────────────────────────────────────────────────────────┘
```

Desktop · worker chip with axis override (modified state)

```
aer-flow  [👑 Artisan* ▼]
         ┌────────────────────────────────────────────────────────┐
         │ Persona Preset                                         │
         │ ● Artisan (modified)                                   │
         │                                                        │
         │ Overrides for this room                                │
         │ Skill      [ implementation & refactor               ▼]│
         │ Vendor     [ claude                                  ▼]│
         │ Model tier [ balanced                                ▼]│
         │ Effort     [ exhaustive *                            ▼]│
         │ Grant      [ Project ∩ Session ∩ Step (Read, Write)  ▼]│
         │ Voice      [ Pragmatic implementer, concise code     ]│
         │                                                        │
         │ Status: 1 parameter differs from preset 'Artisan'      │
         │ [ Reset to preset ]             [ Save as new Persona ]│
         └────────────────────────────────────────────────────────┘
```

Phone · persona picker bottom sheet

```
9:41 ▮▮▮

‹ aer-flow · Participants

Worker 1
[👑 Artisan* ▼]

┌────────────────────────────────────────┐
│ Select Persona                         │
│                                        │
│ ★ Scout — quick reconnaissance         │
│ ★ Courier — draft & relay              │
│ ★ Scribe — documentation               │
│ ★ Artisan — implementation & refactor ✓│
│ ★ Debugger — root-cause diagnosis      │
│ ★ Auditor — code & security review     │
│ ★ Advisor — cross-vendor critique      │
│ ★ Architect — system design & planning │
│                                        │
│ Overrides: Effort → exhaustive (mod)   │
│                                        │
│ [ Reset to preset ] [ Save as new ]    │
└────────────────────────────────────────┘
```

The worker chip shows only the active Persona's name — nothing else — because the whole point of a
named preset is that it stands in for its raw axes, not that it repeats them next to itself.
Clicking the chip opens the picker popover, where vendor, model tier and effort each get their own
row; that's also where model tier is named by *purpose* (deep/balanced/fast), never a specific
version string, per 0023. Selecting a built-in or custom Persona (e.g. Artisan) applies five fields
in one action: Skill binding, vendor/model tier, effort level, permission grant, and
voice/personality — all visible in the popover, none of them duplicated in the label.

The phone picker list follows the same principle: it shows what each Persona *does* (Scout —
"quick reconnaissance," Auditor — "code & security review") rather than its raw model×effort grid
coordinate. The grid mapping matters when building or comparing the library, not when picking a
Persona by what you need it to do — showing `fast × quick` next to every name in a pick-list forces
someone to decode a coordinate before they can decide, which is exactly the busyness a name-first
label is supposed to avoid.

When someone opens the popover and changes a single axis (e.g., bumping Effort from `careful` to
`exhaustive`), **your call, not yet decided**: the chip retains its Persona origin as `Artisan*`
(modified) rather than immediately severing the link or forcing a new saved Persona to be created.
An explicit "Reset to preset" reverts all axes to the Persona default; an explicit "Save as new
Persona…" opens the creation drawer pre-filled with the modified parameters. If the modified
parameters come back to match the preset, the asterisk clears automatically.

On phone, the picker expands as a standard bottom sheet covering the participant list.

### The persona-creation flow

Creating a Persona's instructions *is* creating a private, unnamed skill by construction (0010).
Promoting that instruction set into the shared skill library is an explicit step, mirroring 0003's
"promote into structure only when needed" pattern.

Desktop · persona creation drawer (progressive disclosure)

```
AER Flow · Create Persona
▤ ◱ ⚙

┌──────────────────────────────────────────────────────────────────────────────┐
│ Create Persona                                                             ✕ │
├──────────────────────────────────────────────────────────────────────────────┤
│ 1. Skill Instructions                                                        │
│    ● Pick existing skill from library:                                       │
│      [ Code & Security Review (Auditor)                                    ▼]│
│    ○ Write custom instructions from scratch (creates private skill):         │
│      [                                                                      ]│
│                                                                              │
│ 2. Identity & Voice                                                          │
│    Name  [ Security Sentinel                                                ]│
│    Voice [ Stern auditor. Flags OWASP vulnerabilities with exploit context. ]│
│                                                                              │
│ 3. Model Purpose                                                             │
│    [ Fast ]  [ Balanced ]  [● Deep ]                                         │
│                                                                              │
│ 4. Effort Level                                                              │
│    [ Quick ]  [ Standard ]  [● Careful ]  [ Exhaustive ]                     │
│                                                                              │
│ 5. Permissions                                                               │
│    [● Inherit room/project default ]  [ Custom grant... ]                    │
│                                                                              │
│ 6. Shared Skill Promotion (Optional)                                         │
│    [✓] Save instructions as a shared skill in your library                  │
│        Library skill name: [ Security Review Standard                       ]│
│                                                                              │
│                                           [ Cancel ]  [ Save Persona ]       │
└──────────────────────────────────────────────────────────────────────────────┘
```

The flow uses progressive disclosure inside a single side drawer rather than a multi-step wizard
dialog. Multi-step wizards hide full context, break keyboard navigation, and turn simple edits into
step-through obstacle courses.

Step 1 lets the author select a pre-existing shared skill from the library or type custom markdown
instructions. Typing custom instructions creates a private, unnamed skill attached solely to this
Persona. Editing instructions imported from a shared skill automatically forks a private copy for
this Persona (0021's clone-and-fork rule); changes to the shared library item do not quietly alter
existing Persona definitions.

Step 6 makes skill promotion explicit: checking "Save instructions as a shared skill in your
library" assigns a name and registers the instruction set in the shared skill library. If
unchecked, the skill remains a private instruction set embedded in the Persona.

"Save Persona" and "Cancel" are a plain create/discard pair, not a safeguard 0028 governs — nothing
about naming and saving a preset grants, applies, overwrites or dismisses anything by itself (the
permission grant, if customized, is its own control at step 5). They're drawn with calm, comparable
weight because that's this corpus's general register, not because 0028 specifically requires it
here.

### Room-header controls: reassigning the orchestrator, and adding/removing a Persona mid-room

The room orchestrator is a pinnable role held by exactly one participant in the room. Mid-room
modifications — reassigning the orchestrator or removing a Persona — must preserve room state
integrity and enforce failure safety.

Desktop · room header with orchestrator pin & mid-gate blocked state

```
AER Flow · aer-flow
▤ ◱ ⚙

Rooms + New            aer-flow  [👑 Artisan]  [Auditor]  [+ Add]
                      ┌────────────────────────────────────────────────────────┐
                      │ Room Participants & Orchestrator                       │
                      │                                                        │
                      │ 👑 Artisan (claude)    [ Active Orchestrator ]         │
                      │    Auditor (agy)       [ Make Orchestrator ]           │
                      │                                                        │
                      │ 🔒 Reassignment blocked: Decision gate #3 is open.     │
                      │    Resolve or abandon the gate before swapping.        │
                      └────────────────────────────────────────────────────────┘

you · gate #3         Needs you · permission requested
                      claude requests execution of `rm -rf ./build`
                      [ Allow once ]  [ Deny ]
```

Desktop · removing a Persona with in-flight work & DAG dependency refusal

```
AER Flow · aer-flow
▤ ◱ ⚙

Rooms + New            aer-flow  [👑 Artisan]  [Auditor ✕]
                      ┌────────────────────────────────────────────────────────┐
                      │ Remove Participant 'agy (Auditor)'?                    │
                      │                                                        │
                      │ ⚡ In-flight work detected: Running security sweep...   │
                      │    Stopping worker via InFlightExecutionRegistry...    │
                      │                                                        │
                      │ ✕ Cannot remove 'Auditor':                             │
                      │    Active workflow step 2 (Security Audit) requires   │
                      │    this persona. Stop workflow or edit shape first.    │
                      │                                                        │
                      │ [ Stop Workflow & Remove ]              [ Cancel ]     │
                      └────────────────────────────────────────────────────────┘
```

Orchestrator reassignment is human-only and singular. A human clicks the orchestrator pin (`👑`)
next to any participant chip to hand off the role. If a decision gate (an `aer decide` pause point,
or a permission request) is open, reassignment is **blocked mid-gate** — the lock badge explains
that the pending gate must be resolved or abandoned first, so a swap can never orphan a pending
decision.

Removing a Persona mid-room triggers two sequential guardrails:
1. **In-flight execution stop.** If the Persona is currently executing a task, AER Flow invokes
   `InFlightExecutionRegistry.RequestCancellationAsync` to halt the CLI worker before updating room
   state — the real, already-existing mechanism, not a new one.
2. **DAG dependency check (v1 refusal).** If the room has an active workflow where a downstream step
   relies on the targeted Persona, removal is **refused with a clear reason** ("Active workflow step
   2 requires this persona"). The graph is not silently repaired or mutated. To proceed, the person
   can choose "Stop Workflow & Remove" (toggles the workflow off and removes the participant) or
   cancel. This pair genuinely is a 0028 case — "Stop Workflow & Remove" is destructive and carries
   no more visual weight than "Cancel."

### The workflow-toggle-off control

A room does not require a workflow (0001). Toggling a room's workflow off removes the structured
execution graph while leaving all Persona participants intact in the room as free-form conversation
partners.

Desktop · room header with workflow toggle ON vs OFF

```
Desktop · Workflow ON (shape panel visible)

AER Flow · aer-flow                                          Workflow [● ON ]
▤ ◱ ⚙

aer-flow  [👑 Artisan]  [Auditor]

you Fix the auth bug and run security audit.

claude · working Editing auth.ts...                           Shape
                                                              draft · claude
                                                              ↓
                                                              review · agy
                                                              ↓
                                                              gate · you

──────────────────────────────────────────────────────────────────────────────

Desktop · Workflow OFF (shape panel hidden, personas remain as free-form participants)

AER Flow · aer-flow                                          Workflow [○ OFF]
▤ ◱ ⚙

aer-flow  [👑 Artisan]  [Auditor]

you Fix the auth bug and run security audit.

claude Editing auth.ts...

you @agy review the auth changes in auth.ts.

agy Reviewing auth.ts... No issues found.

Reply… ⏎
```

Toggling the workflow switch in the room header is a visual non-event, not a mode transition. The
right-hand shape panel slides away or fades out, reflecting that step-by-step DAG execution is no
longer active.

The Persona chips in the room header (`[👑 Artisan]`, `[Auditor]`) do **not** change, disconnect,
or enter an artificial "idle" state. They stay in the room as ordinary free-form participants —
because a room without an active workflow is already free-form by 0001's own model. Turning off a
workflow strips away the graph overlay; it does not touch who's present.

### The calls made here

Each of these is a deliberate divergence between the two views, or a place these screens stop doing something the current app does.

One noun Adding a worker never creates a new object. The header chip changes; the room is the same room. "Session" retreats to its technical meaning — the vendor CLI's resumable session — and stops appearing in the interface at all.

Gates inline Decisions render in the conversation that produced them , and are also reachable from the "needs you" filter and the phone. Several entry points, one piece of state. The separate decision surface goes away.

Steps, not a canvas Shapes are authored as an ordered list that renders as a graph. Costs genuine parallel fan-out until a later affordance; buys keyboard navigation, clean diffs, and no tangle.

Gate as a toggle "Ask me first" is a property of a step, not a node type. One switch is the entire mental model for human oversight.

State first, then recency The room list groups by state — needs you, working, earlier — and orders by recency inside each group. An earlier draft said recency alone; the stress test showed that at a hundred rooms the three that need you get buried among ninety-one finished ones. Grouping is what keeps the list stable and useful.

Same root Both surfaces open on rooms. "Needs you" is a filter, not the front door — a product that greets you with a queue feels like a chore list rather than a place your work lives.

Notifications inform No approve or reject from the lock screen. It says enough to judge whether to open, then takes you to the decision in context.

Errors are content A failure shows what broke, in the room. Not a status word with the reason behind a drill-in — and the worker that failed is right there to be asked about it.

Readiness up front Which CLIs were detected is shown at first run and in Settings. The most likely first failure is the least self-evident one, so it gets stated rather than discovered.

State is one thing Every surface renders the room's state machine and none derives its own — which is what makes "no task open" while running impossible rather than merely fixed.

M27 addition, 2026-07-25 — the calls added with the four new screens above.

Preset over chip Picking a Persona sets skill, model tier, effort, and permissions in one gesture on top of the worker chip, without inventing a fourth axis.

Bare chips are unchanged A worker with no Persona attached looks exactly like it does in "The daily driver" and "Two workers, a gate" above — just the vendor name. A Persona replaces that label with its own name; it doesn't add a second visual language beside it.

Name only on the chip, corrected mid-pass An earlier revision of this section put vendor, model tier and effort on the chip itself (`Artisan · claude · balanced · careful`) — busy on desktop, worse on a phone's narrow header. The chip now shows only the Persona's name (plus the crown, plus an asterisk if modified); the raw axes live one tap away in the popover, where model tier is still named by purpose (deep/balanced/fast), never a specific vendor version — 0023's rule, now enforced without also cramming the label.

Pick by what it does, not its coordinate The phone's persona list shows each Persona's skill ("quick reconnaissance," "code & security review"), not its model×effort grid position — the grid matters for building the library, not for choosing who joins a room.

Modified state over instant fork Overriding an axis marks the chip as `Persona* (modified)` rather than severing identity; saving as a new Persona is an explicit action (*your call, not yet decided*).

Single drawer for creation Persona creation uses progressive disclosure in a single side drawer to keep context visible, rather than a multi-step wizard.

Explicit skill promotion Authoring instructions creates a private, unnamed skill by default; promoting it to the shared library is an explicit step-6 checkbox.

Reassignment blocked mid-gate The orchestrator pin cannot be reassigned while a decision gate is open, so a swap can never orphan a pending decision.

In-flight cancellation on removal Removing an active Persona halts execution via the real `InFlightExecutionRegistry.RequestCancellationAsync` before updating room state.

DAG removal refused in v1 Removing a Persona required by an active workflow step is refused with an explicit reason; silent DAG repair is deferred, not built.

Workflow toggle is a non-event Turning off a room's workflow hides the shape panel while retaining all Personas as free-form room participants.

Complete set, draft 3 — the shapes and the calls, not the pixels. Mark it up and I'll take another pass before any of it becomes a decision record or touches the backlog.

M27 addendum, 2026-07-25 — four more screens added for personas and room orchestration; draft 3
above is otherwise unchanged. Mark this section up the same way before any of it becomes a
decision record.
