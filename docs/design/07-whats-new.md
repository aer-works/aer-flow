# What is actually new

> **Design corpus — started 2026-07-24 during the M25 design pause, kept current since.**
> The 2026-07-24 material is unchanged from the artifact of the same name; where a decision
> record and this document differ, **the record wins** — it is the reviewed extraction. Kept
> because the records deliberately capture decisions, and this also holds screen
> specifications, delights and demonstration criteria that are not decision-shaped and would
> otherwise exist nowhere. See [../README.md](../README.md#kept-current-not-frozen-added-2026-07-25)
> for why this corpus is maintained in place rather than staying a closed snapshot.

---

AER Flow — what's actually new

Product · what is actually new

## Why you'd use this instead

Held to a hard test: would this make someone switch, and does anything else already do it? Everything that fails that test is listed separately as table stakes, because a differentiator list padded with settings screens is worth nothing.

### The one that matters most

If only one thing survives contact with implementation, it should be this. Nothing else in the design is as hard to copy or as immediately useful.

The centrepiece

#### You can cross-examine, and asking is not deciding

When something needs your judgement, you are not stuck answering the worker that asked. Put the question to anyone — including a model that isn't in the room yet — and it joins to answer it. Ask a third. The decision stays open the whole time , and only you close it.

Every other tool makes the moment of asking the moment of committing. Here, "I'm not sure, what does the other one think?" is a first-class move rather than a workaround — and the second opinion is formed on the same evidence , not on your summary of it.

Why it's a delight: the anxious pause before approving something you don't fully understand turns into two clicks and a second opinion. That feeling — of being able to check — is the product.

### Genuinely novel

Each of these fails to exist elsewhere, in this combination, for someone on ordinary subscriptions.

#### 01 Your subscriptions, not API keys — several at once

Multi-model tools overwhelmingly mean metered API access. This drives the vendor CLIs already signed in on your machine , so a Claude plan and a Google plan work together with no key handling anywhere in the product.

Why it's hard to copy: it is an architectural commitment, not a feature — adapters that own no key-handling code and shell out to whatever is authenticated. Retrofitting it means giving up the API-key business model.

#### 02 One memory, shared across vendors

Today every vendor keeps its own memory file, so what one agent learned is invisible to the next. Here the room owns the memory and every worker in it gets the same document — what claude worked out in turn 12, agy knows at turn 104.

It stays visible and editable, and a worker proposes additions for you to accept rather than quietly writing them.

Why it matters: this is the difference between several tools in one window and several workers in one conversation.

#### 03 Two of the same vendor, at different models and efforts

Because vendor, model and effort are three separate choices, claude · opus · careful and claude · haiku · quick are two distinct participants. A patient author and a cheap reviewer — on one subscription .

Why it's unusual: tools that model "which model" as a single setting can't express it at all.

#### 04 Files that move between vendors, with receipts

A plan claude wrote, agy edits, agy reads — vendor-neutral files rather than messages trapped in a transcript format. Every version is attributed and diffable , so "what did agy actually change" is one click.

Why it's a delight: handing work between models stops being copy-paste and starts being a review trail.

#### 05 Close the laptop; it keeps working; answer from your phone

The daemon owns the run, so quitting the app doesn't stop anything. A permission raised while you're out reaches your phone, and you answer it in context — the notification opens the decision, it never decides.

Why it's structural: remote isn't a companion app bolted on; it is why the run lives outside the UI in the first place.

#### 06 Permissions with a scope ladder

Allow once, allow rm in this room, allow anything in this room — offered at the moment of asking , not buried in settings. And a denial is a real answer: the worker is told and carries on.

Why it matters: unscoped prompts train a click-through reflex, which is worse than no prompt. The ladder is what keeps the safety feature actually safe.

#### 07 Shapes you draw as a list, not a canvas

Repeatable work — draft, review, gate, apply — is an ordered list with an instruction per step and one "ask me first" toggle. It renders as a graph, diffs cleanly in git , is keyboard-navigable, and works on a phone.

Why it's contrarian: everyone builds the node canvas. The canvas is why visual workflow tools feel like work.

#### 08 One question to everyone

/ask-all puts a question to every worker at once and lays the answers side by side. "Does this migration look safe?" answered by two models that disagree is more informative than either alone .

Why it's cheap to love: one command, and the multi-model room immediately pays for itself.

### Small things that make it feel good

Not differentiators on their own. Collectively they are the difference between a tool you tolerate and one you like.

##### y / n
Permissions answered without reaching for the mouse — and never bound to Enter, so a reflex can't approve something.

##### Typing never blocks
Type while it works; the message queues visibly, with interrupt and remove beside it. Nothing is ever modal.

##### Failures offer the fix
The error text is right there, and so is "ask claude to fix it" — the worker that failed already has the context.

##### Jump to the last decision
An event rail marks gates and failures in a long room, so turn 104 is navigable from the keyboard.

##### Status without colour
Every state has a distinct mark and a word, so it reads in sunlight and for the one man in twelve with a colour deficiency.

##### "Thought for 12s"
Reported after the fact rather than as a live counter — sets expectations without making you watch a spinner.

##### Success collapses, failure opens
Nobody reads a passing build's output. Everybody needs a failing one's.

##### Refresh never blanks
Lists keep their contents and mark them stale, because a list that empties itself reads as data loss.

### Table stakes — necessary, wins nobody

Worth naming so they never get mistaken for progress. All of these must be excellent; none is a reason to switch.

- Talking to one agent about one folder, fast, with nothing in the way

- Code blocks, diffs, and command output rendered properly and collapsed sensibly

- Light and dark, one typeface pairing, consistent on both surfaces

- Settings, pairing, archive, search

- Not losing your work when something crashes

- Slash commands and skills — expected of anything claiming to replace a coding agent

### How to tell if we're still on track

The point of writing this down. Each claim above becomes a thing that can be demonstrated — if it can't be shown working, it isn't true yet.

Claim | Demonstrated when |

Cross-examination | At a live gate, a model not previously in the room is asked, answers, contradicts the first — and the gate is still open. |

Two subscriptions | A room where claude and agy both act, on plan auth, with no key configured anywhere. |

Shared memory | A fact established by one vendor is used by a different vendor later in the same room. |

Two of one vendor | Two chips, same vendor, different model and effort, both answering. The two chips no longer show model/effort directly ([04-workers-commands-control.md](04-workers-commands-control.md)); demonstrate via each chip's popover, or via two workers on the same vendor with different skills attached. |

Files with receipts | One document authored by one vendor and edited by another, with a diff between their versions. |

Work outside the UI | Quit the desktop app mid-run; answer the permission on the phone; reopen and find it continued. Confirmed 2026-07-25 by a live run behind [0029](../decisions/0029-the-gate-is-three-mechanisms.md): held 162s, answered out of band, worker accepted the late result and continued. The hold has a measured ~200s ceiling, so the design persists the question rather than relying on holding the call open indefinitely. |

Scoped permissions | Grant "allow rm in this room", see it not asked again, find and revoke it in settings. |

Shapes as lists | Author a four-step template on a phone, start it on the desktop, watch it run. |

Ask everyone | One question, two answers side by side, disagreeing. |

These are journey-shaped on purpose. The last rebuild drifted because milestones were capability-shaped and every completion gate drove an HTTP surface rather than a person's experience. A claim that can only be demonstrated end to end cannot be quietly satisfied by a passing unit test.

Nine claims, eight delights, six table stakes. If a design decision serves none of the first two lists, it needs a different justification — and if a claim can't be demonstrated, it is a plan rather than a feature.
