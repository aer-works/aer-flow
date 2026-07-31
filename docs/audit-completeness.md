# Audit completeness register — the whole chain, in one place

**Date: 2026-07-25.** The #527 audit ran as an eight-step chain. This is the audit register that
makes *"nothing was left out"* checkable rather than asserted.

```
pixi run audit-completeness
```

The rule it is built on is [`documentation-lessons.md`](documentation-lessons.md) #15: **any claim of
completeness ships with the artifact that lets someone check it.** So every step below names its
**population**, the **artifact** that dispositions each member, and whether a command **recomputes**
it. A step with no enumerable population says so instead of pretending.

**No population size is written down here.** Every count in this chain is computed by the command
above, and a number copied into prose is a number that goes stale silently — this file carried three
different check counts in one afternoon before the copies were removed. Where you want a figure, run
the command; it prints each population next to what it expected. That is the same rule the audit
register enforces on everything else, applied to the audit register.

## The chain

| # | step | population | artifact | recomputed |
|---|---|---|---|---|
| 1 | No doc source missed | the source families | [`vendor-doc-audit.md § Sources`](vendor-doc-audit.md) + `vendor_survey.py` | **yes** |
| 2 | Every source actually read | the mirrored pages | the audit register + [`vendor-doc-audit.md`](vendor-doc-audit.md) dispositions | **yes** |
| 3 | Gaps verified against real behaviour | the registered checks | [`tools/vendor-verify/`](../tools/vendor-verify/README.md) | **yes** |
| 4 | Gaps fixed or filed | defects + open questions | GitHub issues (below) | no — needs the network |
| 5 | What reality changed | the registered checks | [`architecture-impact.md`](architecture-impact.md) | **yes** |
| 6 | Design verified against it | the decision records | [`decision-audit.md`](decision-audit.md) | **yes** |
| 7 | Milestone approach re-verified | M26–M30 | [`plan.md § What the vendor audit changes`](plan.md) | **yes** |
| 8 | Plan to start building | the ordered first slice | [`plan.md`](plan.md) + issues (below) | no — a judgement |

Six of eight recompute. The two that do not are named, with the reason, rather than quietly counted
as done.

## Step 4 — every gap, and where it went

| gap | disposition |
|---|---|
| `--allowedTools` does not bound a worker's capabilities | **filed — [#529](https://github.com/aer-works/aer-flow/issues/529)**, live defect. Also flipped journey J6 to *Fails* and forced 0029. |
| `--bare` disables hooks even via `--settings` | **already filed — #521.** Narrows the viable gate combinations; cited by 0029. |
| A `PreToolUse` hook is the gate that always fires | **already filed — #517.** 0029 is the decision that answers it. |
| Hooks may fail **silently** on Windows; 0029's mandatory hook rests on it | **filed and answered — [#530](https://github.com/aer-works/aer-flow/issues/530)**, closed with a measurement (`gate.broken-hook-fails-open`). They fail **open and silently**, and the assumption named the *wrong cause*: CRLF and spaces in paths both survive. Made 0029's self-check load-bearing and fixed what it must assert. |
| SEP-1036 URL-mode elicitation is unproven end to end | **filed — [#531](https://github.com/aer-works/aer-flow/issues/531)**, permanently a human action item. |
| `.mcp.json` project scope is approval-gated, so unusable headless | **recorded on #445**, with the twice-spawned and release-the-call constraints. |
| `PermissionRequest` / `Notification` silent under `-p` | **fixed in design — 0030**; no issue needed, the answer was architectural. |
| Nested subagents allowed by default; subagent inherits parent mode | **fixed in design — 0029** + carried into M27 in the plan. |
| `--session-id` is not a lock | **recorded against 0008**; `Aer.Daemon` enforces single-writer. Tracked by #393. |
| `usage.output_tokens` excludes subagent tokens | **already filed — #479** (J9's cost surface). |
| `CLAUDE_CONFIG_DIR` / per-worker roots viable | **fixed — CLAUDE.md Architecture Rule 4 corrected**, dated, with the measurement named. |
| `PermissionDenied` never observed firing | **answered — `gate.permission-denied-fires`.** Was an unresolved arm (a zero from a condition that may never have arisen); a two-arm rebuild proved three real denials and a still-silent event. Converted 0030's last **assumed** row to **measured**. |
| Broken hooks on `agy`, where the hook is the *only* per-worker gate | **answered — `agy.broken-hook-fails-open`.** Written after noticing 0029 justified an agy sentence with a claude measurement. Fails open there too, worse consequence. Whether it also fails *silently* is recorded as **unmeasured**: no arm gave the output detector a positive control on agy. |
| Concurrency default 20 · `defer`'s batch limit · MCP idle ceiling | **open, and recorded as open** — [`vendor-doc-audit.md § Still not settled`](vendor-doc-audit.md). Untested is not refuted. |
| A second concurrent login on one subscription | **owner action** — not measurable from an agent session; an **assumed** row in 0029. |

## Step 8 — the first slice, and why it is that one

The audit moved work *earlier*, so the build order is not the milestone order applied naively:

1. **The gate self-check, in M26** — 0029 makes a `PreToolUse` hook mandatory on every spawned
   worker, and a hook that silently does not fire looks exactly like one that works. So the first
   thing built is the thing that proves the gate fires, not the gate. **#530 settled this and made
   it non-negotiable**: a hook whose command cannot execute lets the tool run on **both** vendors,
   and on claude the CLI says nothing at all. The self-check is the only thing standing between the
   product and a permanently open gate on its own development platform.
2. **Worker launch: cwd, `--settings`, `--mcp-config`, depth cap** — the constraints from
   `gate.add-dir-loads-no-config`, `claude/mcp`'s approval gating, and
   `fanout.nesting-allowed-by-default`. All are spawn-path work, cheap now and expensive once three
   surfaces render against it.
3. **The gate itself, releasing the call** (#445) — persisted at ask-time, answerable without the
   originating call open, so SEP-1036 (#531) is a transport to add rather than a rebuild.
4. **Then M26's room surfaces**, which is where the original plan starts.

Steps 1–2 are new work the plan did not have before this audit. That is the audit's concrete output:
**not a reordering of milestones, but three pieces of foundation discovered underneath the first
one.**

## What this audit register cannot do

- **Find a source nobody thought of.** Enumeration cannot find its own blind spot. What bounds it is
  that step 2 dispositioned every mirrored page rather than sampling — but that bounds the *documentation*,
  not reality.
- **Tell you a disposition is right.** Every checker verifies that a reason was given, never that the
  reason is good. `unaffected` in step 6 and `no impact` in step 5 are judgements; they are written
  so a wrong one is visible rather than silent.
- **Prove the checks still pass.** That is `pixi run vendor-verify`, which spends real subscription
  usage and is deliberately not wired into CI.
- **Close a gate that needs a person on the other end.** #531 is the standing one: SEP-1036 hands a
  human a URL to answer out of band, and no agent session can be that human. An agent session can
  build the runbook and the instrument; it cannot be the person. Note the distinction — #530 *read*
  like a human item and was not one; it needed a better instrument, not a browser.

## The two rules the whole audit runs on

Both were paid for by getting it wrong first, and they generalise past this chain:

1. **One variable per check, always with a control arm.** A negative from an instrument that cannot
   distinguish two causes is not evidence. Three conclusions in this audit were wrong because the
   instrument could not separate *never fired* from *fired and failed*.
2. **Prove execution with a sentinel file, never the model's prose.** A model will describe a nested
   spawn it never performed. Where even a file is ambiguous — the middle subagent can write it — count
   the events instead.

A third earned its place here: **a checker whose passing condition is weaker than the claim it
certifies is worse than no checker**, because it converts an open question into a false answer. Both
step 2 and step 6 shipped with that defect and were fixed; step 2's cost was a Final MCP SEP going
unread for a day.
