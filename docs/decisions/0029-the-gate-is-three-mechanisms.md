# 0029 — The gate is three mechanisms with three populations, not one (amends 0015)

Status: accepted
Date: 2026-07-25

[0015](0015-three-kinds-of-needs-you.md)'s three *kinds* of pause — permission, decision, approval —
are unchanged and were not in question. What this record replaces is its **mechanism** guidance:
*"prefer `--permission-prompt-tool` on `claude`, and keep the elected-tool path for `agy`."*

That sentence crowns one mechanism. The verification pass behind #527 measured that no single
mechanism covers the gate, and that the three available ones protect **different populations of
tools**. A design that names only one ships with a hole in whichever population that one does not
cover — and the hole is invisible, because a gate that is configured, running, and never consulted
looks exactly like a gate that works.

## Context

Four measurements force this, all re-runnable via `pixi run vendor-verify`:

**1. Tool restriction is not a capability boundary.** `--allowedTools` *pre-approves*; it does not
restrict the toolset (`gate.allowedtools-is-preapproval-not-ceiling`, live defect
[#529](https://github.com/aer-works/aer-flow/issues/529)). A model denied `Write` reaches for `Bash`
and writes the file. So **an MCP gate bounds nothing the model can reach another way**: a
purpose-built `aer_approve_deploy` tool cannot be faked, but "do not write `prod.yaml`" can — with
a shell redirect. Gating an MCP tool and gating a *capability* are different acts.

**2. A hook's `ask` survives `auto` mode, and the MCP callback does not.** 0015 already records that
`--permission-mode auto` silently disables `--permission-prompt-tool` — zero `tools/call`, no error,
no warning. It then concluded that if the operator's own settings enable `auto`, AER must treat its
permission surface as *absent*. That conclusion is now too pessimistic: a `PreToolUse` hook
returning `permissionDecision: "ask"` forces a prompt even in `auto`
(`gate.hook-ask-in-auto`), and a hook exiting 2 blocks a tool even against an explicit allow rule
(`gate.hook-exit-2-beats-allow`). **The hook is the recovery path the record said did not exist.**

**3. Elicitation is uncircumventable on both vendors.** `elicitation/create` is in the MCP
specification — unlike `_meta["anthropic/requiresUserInteraction"]`, which is a vendor extension
absent from the protocol. Measured across every permission mode on `claude`
(`allowedTools`, `bypassPermissions`, `--dangerously-skip-permissions`) and on `agy`
(`--dangerously-skip-permissions`, `accept-edits`): the gated tool body never ran
(`gate.elicitation-capability`, `agy.elicitation-capability`).

Portability here is **measured, not inferred.** The neighbouring mechanism falsifies the inference:
`force_ask` survives `--dangerously-skip-permissions` on `claude` and collapses on `agy`
(`agy.force-ask-defeated-by-skip`). Vendors disagreeing about what a bypass flag bypasses is this
audit's norm.

**4. But elicitation is a refusal, not a channel to a person.** Every arm answered `cancel` —
headless there is no human, and the client says no on their behalf. This is the single most
mis-readable finding in the audit, so it is stated flatly: **elicitation headless is a fail-closed
deny.** It cannot hold a worker while somebody decides.

## Decision

**The gate is three mechanisms. Each is named by the population it covers, and none substitutes for
another.**

| mechanism | covers | property | fails when |
|---|---|---|---|
| **`PreToolUse` hook** | **vendor tools** — `Bash`, `Write`, `Edit`, everything the model reaches without MCP | the only enforcement point over the toolset a worker actually has; `ask` survives `auto`, exit-2 beats an allow rule | not loaded — see the discovery constraint below |
| **Blocking `tools/call`** | **AER's own MCP tools** | the durable wait: AER declines to respond until its UI returns a human answer. The only mechanism that *holds* rather than refuses | reaped mid-wait without a `timeout` floor or progress notifications |
| **`elicitation` (+ `requiresUserInteraction` on claude)** | **AER's own MCP tools** | uncircumventable refusal — no permission mode on either vendor approves it | always, headless: it denies rather than asks |

**The durable gate is the blocking `tools/call`, and only that.** Elicitation and
`requiresUserInteraction` do not carry a pause across a human's absence; they guarantee that a tool
is *not silently approved*. Use them to make the refusal unbypassable, not to ask the question.

**The hook is not optional, because it is the only mechanism covering vendor tools.** Per finding 1,
an MCP-only gate protects MCP tools. Any capability the model can reach through `Bash` is ungated
unless a hook gates it. AER must therefore ship a `PreToolUse` hook on every worker it spawns, not
only on workers whose flows declare a gate.

**Hook discovery constrains process launch.** Hooks load only from the process's own cwd `.claude/`,
with no parent-directory fallback, and `--add-dir` grants file access but loads **no** configuration
(`gate.add-dir-loads-no-config`). So **AER must control the worker's working directory or pass
`--settings` explicitly** — and with [#521](https://github.com/aer-works/aer-flow/issues/521)
(`--bare` disables hooks even via `--settings`) the viable combinations are narrow. On `agy` this is
sharper still: permission rules are global-only (`agy.permissions-are-global-only`), so a hook in
the workspace's `.agents/hooks.json` is the *only* way to gate an agy worker without writing to the
operator's own settings file.

**The gate must hold for a tree of unknown depth.** One level of subagent nesting runs with nothing
configured (`fanout.nesting-allowed-by-default` — the documentation claims the opposite), and a
subagent inherits the parent's permission mode and cannot be given a stricter one
(`fanout.parent-mode-covers-subagents`). AER must set
`CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH` explicitly rather than trusting a default, and must never
assume a subagent is more constrained than the session that spawned it.

## Rests on

| fact | how we know | if false |
|---|---|---|
| `--allowedTools` pre-approves and does not restrict the toolset | **measured** — `pixi run vendor-verify -- --only gate.allowedtools-is-preapproval-not-ceiling` (#529) | an MCP-only gate would suffice; the mandatory hook becomes optional and this record over-builds |
| A `PreToolUse` hook's `ask` forces a prompt in `auto` mode | **measured** — `--only gate.hook-ask-in-auto` | 0015's original pessimism was right: an operator's `auto` removes AER's permission surface entirely and AER must refuse to render one |
| A `PreToolUse` hook exiting 2 blocks a tool despite an allow rule | **measured** — `--only gate.hook-exit-2-beats-allow` | the hook is advisory, not an enforcement point; nothing covers vendor tools and the gate is MCP-only by necessity |
| `elicitation` is honoured and unbypassable on **both** vendors | **measured** — `--only gate.elicitation`, `--only agy.elicitation` | the portable refusal does not exist; the gate needs a per-vendor mechanism table and `requiresUserInteraction` is claude-only |
| A blocking `tools/call` survives long enough to be answered by a human | **measured to 200 s only** — the upper bound of the idle window is unknown | the durable gate has a ceiling shorter than a person's response time, and the pause must be persisted and the call released rather than held |
| Hooks load only from the process cwd `.claude/`, with no parent fallback | **measured** — `--only gate.add-dir-loads-no-config` | AER need not control the worker's cwd; the launch constraint above relaxes |
| One level of subagent nesting is permitted by default | **measured** — `--only fanout.nesting-allowed-by-default`, two independent runs | the vendor's documented default (off) holds and the explicit depth cap is belt-and-braces rather than required |
| Hooks on Windows run through Git Bash and have historically failed **silently** there | **assumed** — vendor-documented, not measured on this host; Windows is the primary development host | the mandatory hook is unreliable on the main dev platform and every gate above it is too — this is the highest-value unrun check in the set |
| A second concurrent login against one subscription is permitted | **assumed** — needs the account owner; not measurable from an agent session | per-worker config roots collapse to one and worker isolation needs a different design |

## Consequences

**Easier.** "Is this gated?" becomes answerable per tool rather than per product: name the tool's
population, read the row. The mechanism that covers it either is or is not configured.

**Harder.** Three mechanisms mean three failure modes, and two of them fail *silently* — a hook that
never loaded and a callback disabled by `auto` both look exactly like a working gate. AER must
**verify its own gate at worker start** rather than assume configuration took effect: the discovery
control that made these measurements trustworthy is the same technique the product needs at runtime.
This obliges a startup self-check that proves the hook fires, not merely that the file was written.

**Obliges us to** ship a `PreToolUse` hook on every spawned worker; control the worker's cwd or pass
`--settings`; set the subagent depth cap explicitly; give every blocking MCP gate a `timeout` floor
or progress notifications; and never render a permission surface that AER has not confirmed can
fire — [0023](0023-effort-and-models-are-named-by-behaviour.md)'s disclosed-collapse rule applied to
the gate.

**Amends [0015](0015-three-kinds-of-needs-you.md)**; its three-kind split and its gate-durability
section stand unchanged. Does not touch [0004](0004-permission-scopes.md), which governs
pre-declared policy rather than runtime mechanism.

Related: #529 (tool restriction is not a boundary), #521 (`--bare` disables hooks), #527 (the audit),
#445 (the permission-request mechanism), #503 (fan-out limits).
