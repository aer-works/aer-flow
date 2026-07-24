# 0015 — A pause asks for one of three things: permission, a decision, or approval

Status: accepted
Date: 2026-07-24

**Accepted 2026-07-24, after the probe this record was blocked on actually ran (#472).** All three
kinds are now backed by verified mechanism: two by machinery that already ships, and permission by a
blocking MCP tool demonstrated working on **both** vendors. The probe also **disproved a premise this
record was originally written on** — see *The dependency, resolved* below.

## Context

When a worker stops and hands control back to the person, the product today shows one undifferentiated
"paused" and, at the surface level, a single approve/reject affordance. But a person is being asked
qualitatively different things, and answering the wrong kind of question is how the surfaces confused
each other in the manual run behind [0012](0012-what-aer-flow-is.md).

#334 already split *two* of these in the engine. `PausePointKind`
(`src/Aer.Flow/Domain/WorkflowDefinition.cs:36-54`) distinguishes:

- `ReadyForReview = 0` — "the step ran to a terminal outcome and its result awaits human
  review/approval." The historical meaning of every pause, and deliberately the zero value so
  snapshots written before the field existed still deserialize correctly (`WorkflowDefinition.cs:38-44`).
- `NeedsInput = 1` — "an interactive turn paused ready for the operator's next message… not awaiting
  approval… awaiting input."

Crucially, that kind is **a static property of the step declaration**, derived from the bound
snapshot at projection time and carried by no event, "never inferred from conversation content"
(`WorkflowDefinition.cs:26-54`). That is CLAUDE.md Architecture Rule 1 holding: Flow classifies the
*shape* of the pause, never reads the *content* to decide.

What the engine has no representation for at all is the third thing: a worker asking **"may I do
this?"** — run this command, write outside the working directory, hit the network. Today that is not
a pause; it is the silent auto-approval [0004](0004-permission-scopes.md) documents (#331).

## Decision

**Every pause is exactly one of three kinds, and the surface names which:**

| Kind | The question | Engine mapping |
|---|---|---|
| **Permission** | *May I do this?* — a capability the run is not pre-cleared for | **new** — see below |
| **Decision** | *Which way should I go?* — a fork the worker will not choose for you | `PausePointKind.NeedsInput` |
| **Approval** | *Is this finished work acceptable?* — act on a completed result | `PausePointKind.ReadyForReview` |

The three are not styling on one control. They differ in what an answer *means*: a permission answer
authorizes a capability (and composes with [0004](0004-permission-scopes.md)'s scopes); a decision
answer supplies a direction the run continues along; an approval answer accepts, revises, or rejects
work already done. Rendering them as one "paused" state is what let a surface offer "approve" where
the honest act was "answer," and vice versa.

**This is a different axis from [0004](0004-permission-scopes.md), and does not contradict it.** 0004
governs permissions *declared ahead of time* — the project/session/step scopes that decide what never
needs asking and what fails closed. This record governs what happens *at runtime when a worker asks
anyway*: a permission pause is the fall-through when a capability is neither pre-granted nor
pre-denied. 0004 is the policy; this is the interruption when policy is silent.

### The dependency, resolved

The permission kind is only real if a vendor CLI, running headless under AER, will **stop and ask**
rather than decide for itself. That probe ran on 2026-07-24 (#472), and it settled the question in
our favour while correcting the record's own starting assumption.

**Correction: `claude` headless does *not* auto-approve.** The earlier reading — the #331 defect —
came from a probe that leaked the parent session's environment, so the child inherited a tool set no
daemon-spawned worker ever has. Re-run with every `^CLAUDE` variable stripped, in a neutral
directory, `claude -p` **denies** a `Write` it was not granted. **Both vendors fail closed.** That is
strictly better for us than the asymmetry we feared: the risk was never silent approval, it is that a
capability dies quietly unless AER pre-authorises or mediates it.

Two further facts, both material:

- **`--permission-mode manual` is a no-op headless.** The session still reports
  `permissionMode: default`, and no prompt is ever issued. `--permission-prompt-tool` does not exist
  on either CLI. There is no built-in headless "ask the human" path — which is exactly why #445's
  mechanism has to exist rather than being a flag we could have set.
- **Denials are structured.** `claude`'s result event carries
  `permission_denials: [{tool_name, tool_use_id, tool_input}]` — the whole call, replayable verbatim
  once a human answers.

**The mechanism works, on both vendors.** An AER-hosted MCP server exposing a blocking `ask_human`
tool held a turn open on an out-of-band human answer, proven with a token minted *after* the tool
call began so it could not have been foreknown:

| vendor | blocked for | tool-call metadata returned |
|---|---|---|
| `claude` | 10.9 s | `claudecode/toolUseId`, `progressToken` |
| `agy` | 10.3 s | `antigravity.google/conversation_id`, `artifacts_dir`, `progressToken` |

`agy` discovers MCP servers from `~/.gemini/config/mcp_config.json`; the grant grammar is
`mcp(server/tool)` / `mcp(server/*)`. So permission-by-consultation is **uniform across vendors**, not
Claude-only as an earlier note in this milestone claimed.

### Gate durability — a pause must outlive the process holding it

The mechanism above blocks a turn by holding a tool call open. **The process holding it open is the
one a crash kills** — the point was made concretely when a power cut ended the session this probe ran
in. If the pending question lives only in that process, a host loss silently converts "needs you" into
"nothing here", which is the exact failure [0018](0018-attention-is-the-primary-signal.md) exists to
prevent.

So: **the room records the pause when the question is asked, not when it is answered.** The instant
the tool is invoked, the room's durable state gains the kind, the question, and the vendor's
correlation id. Both vendors hand us one in the call metadata, and `agy`'s
`antigravity.google/conversation_id` *is* the key `agy --conversation <id>` resumes with — the vendor
gives us the resume key at gate time, for free.

On restart a room is therefore in one of **three** states, not two, and they are not interchangeable:

1. **Completed** — nothing to do.
2. **Interrupted mid-flight** — resumable against the vendor conversation (`claude -c`,
   `agy --continue` / `--conversation <id>`). The expensive context survives on disk; only AER's
   orchestration state was lost.
3. **Was blocked on a gate** — **re-present the question; do not re-run the worker.** Re-running
   silently re-does work the human never approved, which is the same class of error as answering the
   wrong kind of question.

## Consequences

**Easier.** Each pause surface has one job and one honest set of answers. "Needs you" stops being a
single bucket the UI has to guess the meaning of, and the guess that produced approve-where-answer-was-meant
is designed out.

**Harder.** The product now has to *know* which kind a given pause is at the moment it renders it —
trivial for decision/approval (the snapshot says so), and for permission it means AER must host an
MCP server and keep it running for the life of every turn. That server is also a **new crash
surface**: it must be cheap to start and hold no state, because `claude` spawns it **twice** per run
(once to enumerate tools, killing it immediately after `tools/list`, then again for the turn itself).
Any state it needs belongs in the room, not the process.

**Obliges us to** persist a gate at ask-time rather than answer-time (above), keep the kind derived
from the declaration and never from content (CLAUDE.md Rule 1, as `PausePointKind` already does), and
wire a permission answer into [0004](0004-permission-scopes.md)'s scope intersection rather than
treating it as a fourth, ad-hoc grant. It also obliges the room list to treat a gate recovered from
disk exactly like a live one — the operator must not be able to tell that the host restarted.

**Supersedes nothing.** It *extends* #334's two-kind split to three and names the third as the work
#445 exists to enable.

Related: #331 (enforcement — see 0004), #334 (the two-kind pause split), #445 (permission-request
mechanism and its probe).
