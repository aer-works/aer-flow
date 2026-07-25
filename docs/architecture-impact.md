# Architecture impact — every measured finding, and what it changed

**Date: 2026-07-25 (#527).** Population: every check registered in `tools/vendor-verify/verify.py`.
Recomputed by `pixi run audit-completeness`, which fails if a check has no row here.

This is step 5 of the audit chain: *what has reality changed for the architecture?* The answer is
only checkable if the question is asked of **every** measurement, including the ones that changed
nothing — otherwise "I considered the findings" is an assertion. A `no impact` row is a real
disposition and the commonest correct one: most of these confirm something the design already
assumed, and a confirmation is worth exactly as much as it costs to re-run.

Read alongside [`decision-audit.md`](decision-audit.md), which does the same over decision records.
This table runs the other direction: from measurement to design.

## The gate

| check | result | architectural impact |
|---|---|---|
| `gate.allowedtools-is-preapproval-not-ceiling` | `--allowedTools` pre-approves and does not restrict | **Largest impact in the audit.** Tool restriction is not a capability boundary — a model denied `Write` writes through `Bash`. Forced [0029](decisions/0029-the-gate-is-three-mechanisms.md); made a `PreToolUse` hook mandatory on every worker; moved gate work from M28 into M26; flipped journey J6 from Partial to **Fails**. Live defect #529. |
| `gate.hook-exit-2-beats-allow` | a hook exiting 2 blocks despite an allow rule | The enforcement point 0029 rests on. Without it the hook is advisory and nothing covers vendor tools. |
| `gate.hook-ask-in-auto` | a hook's `ask` forces a prompt even in `auto` | **Softened a conclusion 0015 had already drawn.** 0015 said an operator's `auto` leaves AER with no permission surface; the hook is the recovery path, so the surface survives a mode AER does not control. |
| `gate.elicitation-capability` | uncircumventable on claude across all three modes | Portable refusal primitive. With the agy arm, it is the only gate primitive measured unbypassable on both vendors. |
| `agy.elicitation-capability` | uncircumventable on agy under skip-permissions | **Turned an inference into a measurement.** Without it 0029's portability row would have been "assumed" — and `agy.force-ask-defeated-by-skip` shows that exact inference failing for the neighbouring mechanism. |
| `agy.url-mode-elicitation` | agy declares and routes SEP-1036 `mode: "url"` | The non-blocking durable gate is **already standardized**, and exists on one vendor. Added 0029's "build it to migrate" clause: releasing the call must be the normal path, not the crash path. |
| `gate.requires-user-interaction` | no mode or allow rule approves it | Confirms the vendor extension is a real refusal — but it is claude-only and hard-denies headless, which is why 0029 does not build on it. |
| `gate.prompt-tool-conversion` | a prompt tool's `allow` becomes `deny` for such a tool | Bounds what `--permission-prompt-tool` can approve. No design change: AER never needed to approve one. |
| `gate.ask-rule-beats-bypass` | an explicit `ask` rule gates under `bypassPermissions` | **No impact** — confirms 0004's fail-closed premise. Kept because it is the cheapest sentinel for a regression in the permission ladder. |
| `gate.add-dir-loads-no-config` | `--add-dir` grants files, loads no config | Launch constraint: AER must control the worker's cwd or pass `--settings`. Lands in M26, not M28. |
| `gate.permission-request-not-headless` | `PermissionRequest` never fires under `-p` | Removed 0018's assumed notification source. Forced [0030](decisions/0030-aer-is-its-own-notifier.md). |
| `gate.headless-event-surface` | 10 events fire, 3 silent, 10 untested | The other half of 0030 — it is what makes "then what?" answerable, and its three-way split is why the record does not assert eleven untested things. |

## Fan-out

| check | result | architectural impact |
|---|---|---|
| `fanout.nesting-allowed-by-default` | one level of nesting runs unconfigured | **Contradicts the vendor's own documentation.** #503 items 4–5 rested on the opposite. AER must set `CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH` explicitly; the gate, cost attribution and concurrency budget must hold for a tree of unknown depth. Lands in M27. |
| `fanout.parent-mode-covers-subagents` | a subagent inherits the parent's mode | AER cannot rely on a subagent being more constrained than its parent. Whatever the lead runs under, the tree runs under. |
| `fanout.concurrency-cap` | peak overlap tracks the cap exactly (2 and 6) | AER can delegate fan-out bounding to the vendor rather than serialising dispatch. **The documented default of 20 remains unverified** — neither arm ran uncapped. |

## Cost

| check | result | architectural impact |
|---|---|---|
| `cost.subagent-tokens-excluded` | top-level `usage.output_tokens` under-reports by 22% on one subagent | Any cost surface summing the top-level field is wrong, and wrong-er with depth. `modelUsage` is the whole-tree figure. Binds J9 (#479). |
| `cost.max-budget-enforced` | `--max-budget-usd` exits 1 with `error_max_budget_usd` | AER can **delegate per-session budget enforcement to the vendor** rather than implementing its own — which pairs with the row above: the vendor is the reliable source for spend, AER's arithmetic over the top-level field is not. |
| `cost.json-schema-conforms` | `--json-schema` constrains the result shape | Makes Architecture Rule 1 practical: Flow routes on a structured return instead of parsing prose. Enables the decision/approval kinds of 0015 without content inspection. |

## Durability and lifecycle

| check | result | architectural impact |
|---|---|---|
| `durability.session-id-guard-is-not-a-lock` | two concurrent processes both win the race | **The docs claim one writer.** `Aer.Daemon` must enforce single-writer per session itself. Amends 0008's premise. |
| `durability.auth-status-is-per-config-root` | `auth status` reports per root, starts no session | **Overturned a recorded constraint.** Per-worker config roots are viable and Rule-4-clean; `auth status` is a free pre-dispatch readiness probe. Corrected Architecture Rule 4 in CLAUDE.md. |
| `durability.config-dir-redirect-breaks-auth` | a fresh `CLAUDE_CONFIG_DIR` is not logged in | The cost of the row above: a fresh root needs a one-time interactive `claude auth login` **by the operator**. That is a human signing in, not AER handling a credential. |
| `lifecycle.bg-projection` | `claude agents --json` projects state with a vocabulary | AER can read background session state structurally rather than scraping. **No design change** — confirms 0008's projection model is buildable. |
| `lifecycle.daemon-status` | `claude daemon status` reports machine-readable readiness | Supervisor input for `Aer.Daemon` (#478). **No design change.** |

## agy

| check | result | architectural impact |
|---|---|---|
| `agy.permissions-are-global-only` | no project-scoped permission rules are honoured | **Hooks are agy's only per-worker gate.** AER cannot scope an agy worker's permissions without writing the operator's own settings file — which it must not. Makes 0029's mandatory hook load-bearing on agy specifically. |
| `agy.force-ask-defeated-by-skip` | `force_ask` collapses under `--dangerously-skip-permissions` | A vendor asymmetry running the *opposite* way from the usual one. It is also the evidence that made measuring agy elicitation non-optional rather than a formality. |
| `agy.hook-deny-honoured` | a `PreToolUse` deny blocks and surfaces its reason | Confirms the gate is symmetric across vendors at the hook layer — the premise 0029's mechanism table depends on. |
| `agy.fails-closed-headless` | `agy -p` auto-denies an ungated tool and names the rule | **No impact** — confirms 0015's corrected premise that both vendors fail closed. Also the reason a permissive arm is required to measure anything else about agy headless. |
| `agy.settings-allow-honoured-headless` | `permissions.allow` is honoured under `-p` | **No impact, and deliberately kept red-adjacent**: upstream #548 says otherwise and does not reproduce here. The check exists to notice if the upstream report becomes true on this host. |
| `agy.termination-behavior` | `PostInvocation terminationBehavior: terminate` ends the loop | Confirms agy's control surface is real and CLI-reachable. **No design change**; AER does not currently need it. |

## What this table cannot tell you

- **Whether an implication is correct** — only that one was recorded and is traceable to a run.
- **Whether a finding that changed nothing should have.** A `no impact` row is a judgement. The
  defence is that each names *what* it confirms, so a wrong call is visible rather than silent.
- **What was never measured.** The blind spot is the unrun check, not the unrecorded row. The
  standing ones are listed under *Still not settled* in
  [`vendor-doc-audit.md`](vendor-doc-audit.md), and the highest-value of them is whether hooks fire
  reliably on Windows through Git Bash — the platform this is developed on, and the thing 0029's
  mandatory hook sits on top of.
