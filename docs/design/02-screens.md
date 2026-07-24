# Screens — every shape the product needs, both surfaces

> **Design corpus — authored 2026-07-24 during the M25 design pause.**
> Extracted verbatim from the artifact of the same name. This is the *source* the decision
> records were written from; where a record and this document differ, **the record wins** —
> it is the reviewed extraction. Kept because the records deliberately capture decisions, and
> this also holds screen specifications, delights and demonstration criteria that are not
> decision-shaped and would otherwise exist nowhere.

---

AER Flow — screens

Screen design · draft 3 · complete set

## Screens

Every shape the product needs, on both surfaces. Desktop and phone are different views of one thing, not one layout at two widths — where they diverge, the divergence is written down here rather than discovered in code.

First run The daily driver Two workers, a gate
When it fails Starting from a template Drawing a shape
Settings Phone The calls

### First run

One screen, one action, and the answer to the question that actually breaks first installs: are my CLIs even being found? Onboarding and diagnosis are the same screen because they are the same worry.

Desktop · nothing yet

AER Flow

▤ ◱ ⚙

Point AER Flow at a folder

A room is a conversation about one folder. Open one and start talking; add a second worker whenever it is worth it.

Choose a folder… Start from a template

Workers found ✓ claude ✓ antigravity — antigravity not installed

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

aer-flow claude + Add worker

you Why does a new room not show up in the list?

claude Two causes. The list only refreshed at startup, and a room was registered only when its first run returned 2xx — so a refused run left a real folder nothing knew about.

you Fix both.

claude · working Editing MainWindow.axaml.cs …

Reply… ⏎

The sidebar is a presence list, not a file browser. Name, state mark, and what that worker is doing right now — the three things that let you decide whether to switch. It is always there, so a room you are not watching is never invisible. "+ Add worker" is a control in the header , not a new object to create: that is what keeps "room" a single noun.

### Desktop · two workers and a gate

The escalation. You added a reviewer, it disagreed, and now something needs you. Note what did not happen: you did not move to a different screen.

Desktop · two workers · decision inline

AER Flow

▤ ◱ ⚙

Rooms + New

◗ aer-flow Needs you · 2 workers

◔ payments-api Working · 4m

✕ migration Failed · 3h

aer-flow claude antigravity +

claude Patch ready: refresh both lists through one call, and register a room when it is created.

antigravity · reviewing The refresh is right. But the picker path is not the only entry point — the CLI still registers only on success.

Needs you Apply antigravity's correction before continuing?
Apply Skip Ask claude to respond

Reply… ⏎

Shape Hide

draft · claude

↓

review · antigravity

↓

gate · you

↓

apply

A gate is answered where it was raised. It renders as a turn in the conversation, because that is where the context is — the argument you are ruling on is directly above it. It also appears on the phone and in the "needs you" filter; same object, several entry points, never several copies of the state. The shape panel is optional , showing where this room is in the template it was started from — dismissible, and absent entirely for a room you just started by talking.

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

draft · claude     review · antigravity

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

draft claude ask me first ○

+ step

review antigravity ask me first ●

+ step

apply claude ask me first ○

+ step

Each step runs after the one above it. Turn on "ask me first" to put a gate before a step.

Preview

draft · claude

↓

gate · you

↓

review · antigravity

↓

apply · claude

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

antigravity ✓ found · signed in

antigravity not installed · how to add

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

◗ aer-flow Needs you · claude + antigravity

◗ payments-api Needs you · schema change

◔ docs-sweep Working · 4m

✕ migration Failed · 3h

— spike-cache Cancelled · 1d

Rooms Needs you Settings

9:41 ▮▮▮

‹ aer-flow claude + antigravity

claude Patch ready: refresh both lists through one call.

antigravity The CLI entry point still registers only on success.

Needs you Apply antigravity's correction?
Apply Skip

Reply… ↑

9:41 ▮▮▮

Locked Notification

AER Flow · aer-flow antigravity corrected claude's patch — a decision is waiting.
Open

AER Flow · migration Failed — the worker exited before finishing.

A notification says enough to judge whether it is worth opening, and never decides anything.

The phone's first run is pairing, and nothing else. It has no folders of its own and no CLIs installed, so until it is connected to a computer there is genuinely nothing it can do — pretending otherwise with an empty rooms list would be worse than saying so. Notifications inform, they never decide: one tap opens the gate beside the argument you are ruling on, because approving an agent's work from a lock screen is one mis-tap from approving something you never read. Template authoring is out of scope for the phone's first version , not ruled out — a small-screen shape editor is an interesting problem worth returning to, and the step-list model above is far more portable to a phone than a canvas would have been.

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

Complete set, draft 3 — the shapes and the calls, not the pixels. Mark it up and I'll take another pass before any of it becomes a decision record or touches the backlog.
