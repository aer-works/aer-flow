# Audit completeness ledger — the whole chain, in one place

**Date: 2026-07-25.** The #527 audit ran as an eight-step chain. This is the ledger that makes
*"nothing was left out"* checkable rather than asserted.

```
pixi run audit-completeness
```

The rule it is built on is [`documentation-lessons.md`](documentation-lessons.md) #15: **any claim of
completeness ships with the artifact that lets someone check it.** So every step below names its
**population**, the **artifact** that dispositions each member, and whether a command **recomputes**
it. A step with no enumerable population says so instead of pretending.

## The chain

| # | step | population | artifact | recomputed |
|---|---|---|---|---|
| 1 | No doc source missed | 7 source families | [`vendor-doc-audit.md § Sources`](vendor-doc-audit.md) + `vendor_survey.py` | **yes** |
| 2 | Every source actually read | **382 mirrored pages** | ledger + [`vendor-doc-audit.md`](vendor-doc-audit.md) dispositions | **yes** |
| 3 | Gaps verified against real behaviour | **29 checks** | [`tools/vendor-verify/`](../tools/vendor-verify/README.md) | **yes** |
| 4 | Gaps fixed or filed | defects + open questions | GitHub issues (below) | no — needs the network |
| 5 | What reality changed | **29 checks** | [`architecture-impact.md`](architecture-impact.md) | **yes** |
| 6 | Design verified against it | **30 decision records** | [`decision-audit.md`](decision-audit.md) | **yes** |
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
| Hooks may fail **silently** on Windows; 0029's mandatory hook rests on it | **filed — [#530](https://github.com/aer-works/aer-flow/issues/530)**, and it is the highest-value unrun check in the set. |
| SEP-1036 URL-mode elicitation is unproven end to end | **filed — [#531](https://github.com/aer-works/aer-flow/issues/531)**, permanently a human action item. |
| `.mcp.json` project scope is approval-gated, so unusable headless | **recorded on #445**, with the twice-spawned and release-the-call constraints. |
| `PermissionRequest` / `Notification` silent under `-p` | **fixed in design — 0030**; no issue needed, the answer was architectural. |
| Nested subagents allowed by default; subagent inherits parent mode | **fixed in design — 0029** + carried into M27 in the plan. |
| `--session-id` is not a lock | **recorded against 0008**; `Aer.Daemon` enforces single-writer. Tracked by #393. |
| `usage.output_tokens` excludes subagent tokens | **already filed — #479** (J9's cost surface). |
| `CLAUDE_CONFIG_DIR` / per-worker roots viable | **fixed — CLAUDE.md Architecture Rule 4 corrected**, dated, with the measurement named. |
| Concurrency default 20 · `defer`'s batch limit · MCP idle ceiling · `PermissionDenied` arm | **open, and recorded as open** — [`vendor-doc-audit.md § Still not settled`](vendor-doc-audit.md). Untested is not refuted. |
| A second concurrent login on one subscription | **owner action** — not measurable from an agent session; an **assumed** row in 0029. |

## Step 8 — the first slice, and why it is that one

The audit moved work *earlier*, so the build order is not the milestone order applied naively:

1. **The gate self-check, in M26** — 0029 makes a `PreToolUse` hook mandatory on every spawned
   worker, and a hook that silently does not fire looks exactly like one that works. So the first
   thing built is the thing that proves the gate fires, not the gate. **#530 gates this**: if hooks
   can fail silently on Windows, the self-check is the only thing standing between the product and a
   permanently open gate on its own development platform.
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

## What this ledger cannot do

- **Find a source nobody thought of.** Enumeration cannot find its own blind spot. What bounds it is
  that step 2 dispositioned all 382 pages rather than sampling — but that bounds the *documentation*,
  not reality.
- **Tell you a disposition is right.** Every checker verifies that a reason was given, never that the
  reason is good. `unaffected` in step 6 and `no impact` in step 5 are judgements; they are written
  so a wrong one is visible rather than silent.
- **Prove the checks still pass.** That is `pixi run vendor-verify`, which spends real subscription
  usage and is deliberately not wired into CI.
- **Close a live-vendor gate.** #530 and #531 need a human. An agent session can build the runbook
  and the instrument; it cannot be the person.

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
