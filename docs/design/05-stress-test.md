# Stress test — the design at 100 rooms and 100 turns

> **Design corpus — authored 2026-07-24 during the M25 design pause.**
> Extracted verbatim from the artifact of the same name. This is the *source* the decision
> records were written from; where a record and this document differ, **the record wins** —
> it is the reviewed extraction. Kept because the records deliberately capture decisions, and
> this also holds screen specifications, delights and demonstration criteria that are not
> decision-shaped and would otherwise exist nowhere.

---

AER Flow — stress test and gaps

Design review · stress test

## Does it hold up?

Four passes of design, tested against the cases that actually break interfaces: heavy use, long histories, many rooms, and the things I put on a screen without thinking them through.

Effort The new surfaces on a phone At scale
The common case Not thought through What the backlog says

### Effort — the axis I missed

You were right that this was absent. Choosing a worker has three dimensions, not two: which subscription, which model, and how hard it should think.

Claude · Opus 4.8

Quick no extra thinking

Standard default

Careful thinks first

Exhaustive slow, costly

Applies to

✓ this worker, this room

every room using this worker

aer-flow claude opus · careful antigravity gemini 3 flash · quick +

claude · thought for 12s The ordering disagreement is in the view model, not the daemon.

Reply… ⏎

Effort belongs on the chip beside the model , because it changes what an answer costs and how long you will wait — the same reason the model is there. opus · careful and opus · quick are meaningfully different participants.

Named by behaviour, not by mechanism. Quick / standard / careful / exhaustive, never a token budget or a vendor's flag name. Vendors express this completely differently and rename it often; the person's question is always "how hard should it think about this."

Effort is per worker per room, with an option to make it global — the deep-author / fast-reviewer pattern is exactly a per-room effort choice, and it belongs in templates too, alongside the model.

Thinking time is reported after the fact ("thought for 12s") rather than as a live counter. It sets expectations for the next turn without turning waiting into a spectacle.

### The fourth pass, on a phone

Permissions, models, commands and usage were all designed on the desktop. Three of the four are fine; one genuinely needed rethinking.

Phone · permission · picker · usage · commands

9:41 ▮▮▮

‹ aer-flow claude · opus

claude Stale build output is causing the failure.

Permission Run this command?
rm -rf build/

Allow once Deny

Allow rm here · Allow anything here

Reply… ↑

9:41 ▮▮▮

‹ Add a worker Step 1 of 2 · vendor

Claude signed in · 3 models

Antigravity signed in · 2 models

Codex not installed on your computer

Then

Model and effort chosen on the next screen

9:41 ▮▮▮

‹ Usage All rooms · this week

Claude plan

72%

Antigravity plan

18%

Rooms near their context limit

◗ aer-flow claude at 64% of context

9:41 ▮▮▮

‹ aer-flow Actions

This room

Ask everyone one question, both workers

Files 6 touched · 2 working documents

Shape draft → review → apply

Usage claude 64% · antigravity 4%

Workers

claude · opus · careful change model or effort

Permissions work better on a phone than on a desktop , which surprised me. The command is short, the question is binary, and the scope options fit on one line beneath. This is the surface most worth having remotely — it is the thing most likely to be blocking a worker while you are away from the machine.

The one that needed rethinking is the command palette. Typing / to discover commands is a keyboard idiom and does not survive a touch keyboard. On the phone the same capabilities become an Actions sheet reached from the room header — same commands, same namespacing, browsed rather than typed. Nothing is lost because the phone's job is deciding and watching, not driving.

Choosing a worker becomes two steps rather than one nested menu — vendor, then model and effort. A three-level cascade is unusable with a thumb.

Usage is a genuinely good phone surface. "Am I about to run out of Claude this week" is a question you ask away from your desk more often than at it.

### At scale — 100 rooms, 100 turns, three subscriptions

Everything designed so far was drawn with five rooms and six turns. Here is what actually breaks, and what fixes it.

Desktop · a long room in a large fleet

▤ ◱ ⚙

Search rooms… ⌘K

Needs you · 3

◗ aer-flow Permission · rm -rf build/

◗ payments-api Decision · auth provider

◗ billing-svc Action · review 14 files

Working · 6

◔ docs-sweep claude · 4m

◔ infra-audit antigravity · 11m

⋯ 4 more working

Earlier · 91

✓ migration Finished · yesterday

⋯ Show all 91

aer-flow claude opus · careful antigravity gemini 3 pro turn 104

turns 1–96 · summarised Rebuilt the switcher, fixed room registration, renamed the noun. Show anyway

claude Stale build output is causing the test failure.

Permission · claude Run rm -rf build/ ?
Allow once Allow rm here Deny

Reply… ⏎ · ⌘↑ jump to last decision

#### What breaks, and the fix

At scale | What breaks | Fix |

100 rooms | A flat recency list is unusable. The three rooms that need you are buried among 91 finished ones. | Group by state — needs you, working, earlier — with the tail collapsed. Recency orders within a group. Search with a keyboard shortcut becomes mandatory rather than "not yet". |

100 turns | Scrolling to find the decision you half-remember is hopeless, and the top of the room is dead weight. | An event rail marking gates and failures (already scoped as #459 ), a jump-to-last-decision key, and older turns collapsed into a summary that can be expanded. |

Long output | A 5000-line log inside a turn destroys the conversation. | Already collapsed by default; add an explicit line count and a hard cap that opens the full thing in its own view. |

3 subscriptions | Worker chips, model, and effort are a lot of text in a header. | Chips truncate to vendor plus a mark; model and effort appear on hover and in the Actions sheet. The header is a status line, not a control panel. |

Complex shapes | A 12-step template is a long list, and the preview graph stops fitting. | The list is fine — it is a list. The preview needs collapsing of completed stages, which is what a stage strip does naturally. |

Many permissions | Fifty prompts a day trains the click-through reflex the design exists to prevent. | This is what scoping is for, and it means the scope ladder must be the prominent path, not the fallback. If a person is answering the same permission twice, the second time should offer the standing grant first. |

The list grouping is the biggest change scale forces , and it contradicts something I wrote earlier. The screens pass said rooms are ordered by recency, full stop. At a hundred rooms that is wrong: state has to be the primary sort and recency the secondary. That change belongs back in the screens document.

Two of these fixes are already in the backlog — the event rail is #459 and search was explicitly deferred. Scale is the argument that promotes search from "not yet" to required.

### Is the common case still cheap?

Four passes of design have added rooms, workers, models, effort, templates, permissions, commands and shapes. The test that matters: how many actions to just talk to one agent about a folder?

Task | Cost | Verdict |

Talk to one agent about a folder | Choose folder → type. Default worker, default model, default effort. | Two actions. Right. |

Return to yesterday's work | Click its row. It is in "earlier", one search away if buried. | One action. |

Answer a permission | y , or one click. | Right — it happens constantly. |

Add a second opinion | Ask someone → pick → it joins and answers. | One gesture; adding and asking are the same act. |

Start shaped work | New → pick template → choose folder → start. | Four. Acceptable, but a saved default should make it two. |

Change a model mid-room | Click chip → vendor → model → effort. | Three levels deep for something done often. Wants a recents shortcut. |

The simple path survived , which was the thing most at risk — every addition landed as an escalation rather than a step in the default flow. The two soft spots are both "frequently repeated thing is three levels deep", and both are fixed the same way: remember the last choice and offer it first.

### Things I put on a screen without thinking them through

You asked directly, so here they are — the places where a mockup implies something I have not designed, and a couple where I contradicted myself.

##### "Ask someone" with the room's history

I showed a worker being brought in mid-room to answer one question. What do they see of the previous 103 turns? Nothing? A summary? Everything? Each answer has real cost and quality consequences, and I drew a menu that quietly assumes this is solved.

##### Two workers editing the same file

The files list shows attribution per file, which implies one writer each. Nothing in the design says what happens when claude and antigravity both edit MainWindow.axaml.cs in the same minute. The engine serialises per room, so it may be impossible — but I have not checked, and the UI makes a promise either way.

##### Queued messages and gates

A queued message sends when the worker finishes. But what if the worker finishes by raising a permission? Does the queued text send into a blocked worker, wait for the permission to be answered, or get dropped? I drew the happy path only.

##### Where a template's steps get their prompts

The shape editor has name, worker, and "ask me first". It has no prompt field. A step called "review" must tell its worker what reviewing means, and I omitted the single most important thing about a step.

##### Recency versus state in the room list

The screens pass says recency, full stop, and defends it as the stable ordering. The scale section above says state first, recency within. They contradict; state-first wins , and the earlier document is now wrong.

##### "Save into the project" for a file that already exists

Drawn as a one-way door with no thought about collisions, overwrite, or what happens when the project's copy has since changed. It is a merge problem wearing a button.

##### Stop, mid-permission

Stop is always available and a permission is blocking. Stopping a worker that is waiting on you is presumably fine — but the permission then needs to disappear from three surfaces at once, and I have not said so anywhere.

### What the backlog says we still have not discussed

Read against all 57 open issues. Most are already covered by these four passes or become requirements on the rebuild — these are the ones the design genuinely has not addressed.

Issue | Why it matters to the design |

#442 · what memory a worker gets | Memory falls out of the working directory today and splits per vendor — so two workers in one room do not share what they have learned. That is a room-model question, not a config detail, and nothing in four passes addresses it. |

#340 · derived rooms | Starting a review from inside a room — a child that reports back. The room model implies it and no screen shows it. Closest thing designed is the shape panel. |

#385 · advisor participant | A standing cross-vendor critic rather than an ad-hoc "ask someone". Possibly just a saved worker with a role — worth deciding, because it changes whether roles are a concept. |

#386 · canonical skills per vendor | I drew skills as each vendor's own. This proposes app-level skills realised per vendor — a different and stronger model that the command palette design would have to reflect. |

#424 · AER as an MCP context source | Exposing execution state and artifacts so a worker can query the room it is in. Has no UI, but changes what "what does an added worker see" can mean. |

#445 · host an MCP server | The mechanism behind permissions and decisions. The three-kinds design depends on this existing — worth stating as a dependency rather than assuming. |

#282 · notifications | Designed for the phone; the desktop half is unscoped and the inventory marks it "later". |

#268 · keyboard-first triage | Keyboard shows up piecemeal (⌘K, y/n, ⌘↑). There is no coherent keyboard model across the whole shell, and at a hundred rooms that is the difference between fast and unusable. |

#405 · working-status verbs | Cosmetic, but it is the one place the product has a voice. Worth deciding deliberately rather than inheriting. |

#266 · curved DAG edges, hover tracing | Probably obsolete. It describes canvas polish for a canvas we decided not to build. A candidate for closing rather than doing. |

Stress test complete. The corrections that change earlier documents — state-first room ordering, effort on the chip, an Actions sheet in place of the phone's command palette — should be folded back into those documents rather than living only here.
