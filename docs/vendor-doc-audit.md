# Vendor documentation audit — every documented capability, and whether we verified it

**Status: in progress.** Started 2026-07-24 against `claude` 2.1.219 and `agy` 1.1.7.

This exists because `docs/vendor-capabilities.md` was built by probing binaries and help text while
both vendors publish documentation. Several rows were wrong as a result, and the errors were not
random — they were all the same shape: **a capability was recorded as absent because the surface we
checked did not mention it.** The documentation mentions it.

## The method, and why it changed

The order is now: **read the docs, then verify each claim against the live CLI.** Previously it was
probe-first, which produced two rounds of wrong answers and a 166 MB binary scan looking for a flag
list that was published on a web page.

Claude Code's documentation index is machine-readable and worth knowing about:
`https://code.claude.com/docs/llms.txt` — ~170 pages, each fetchable as `.md`.

Every row below carries an **evidence class**, same discipline as `vendor-capabilities.md`:

- **documented** — the vendor says so. A claim, not a measurement.
- **verified** — we ran it and observed the documented behaviour.
- **contradicted** — we ran it and the documentation does not hold here.
- **unverifiable here** — cannot be tested on this host/platform/plan.

---

## Corrections this audit forces

### 1. `claude` HAS an OS-enforced sandbox — but not on Windows

`vendor-capabilities.md` recorded claude's sandbox as *"referenced in help only"*, and
[0004](decisions/0004-permission-scopes.md) concluded a project ceiling is *enforceable* on `agy` and
only *advisory* on `claude`. **There are two full documentation pages on Claude Code sandboxing.**

| | documented |
|---|---|
| enforcement | **OS-level** — Seatbelt on macOS, `bubblewrap` on Linux/WSL2. Applies to all child processes |
| enable | `/sandbox` panel, or `sandbox.enabled` in settings; managed settings can force it |
| filesystem | `sandbox.filesystem.allowWrite` / `denyWrite` / `denyRead` / `allowRead` / `disabled` |
| network | proxy outside the sandbox; `network.allowedDomains` / `deniedDomains` / `tlsTerminate` / `httpProxyPort` / `socksProxyPort` |
| credentials | `sandbox.credentials.files` and `.envVars`, each `"mode": "deny"`, envVars also `"mask"` (sentinel substituted by the proxy for `injectHosts`) |
| hard-fail | `sandbox.failIfUnavailable: true` — refuse to start rather than silently running unsandboxed |
| escape hatch | model may retry with `dangerouslyDisableSandbox`; disable via `allowUnsandboxedCommands: false` ("Strict sandbox mode") |
| org lockdown | `allowManagedDomainsOnly`, `allowManagedReadPathsOnly`; settings files are write-denied inside the sandbox at every scope |

**Why we got it wrong, and it matters for how we test:** *"The sandbox is built into Claude Code and
runs on macOS, Linux, and WSL2. **Native Windows is not supported.**"* The probe host is Windows 11.
So the observation "no sandbox here" was true **of this machine** and was generalised into a claim
about the product. AER Flow ships cross-platform, so the correct statement is that claude's ceiling is
OS-enforced on macOS/Linux/WSL2 and unavailable on native Windows.

**This is the sharpest methodological lesson in this audit: a single-platform observation is not a
capability claim.** Every row in `vendor-capabilities.md` was established on Windows only.

### 2. `--help` is officially incomplete on `claude` too, and the docs say so outright

On channels: *"Neither `--channels` nor `--dangerously-load-development-channels` appears in
`claude --help` while the feature is in preview. **The flags work even though they aren't listed.**"*

So "not in `--help`" is not evidence of absence on **either** vendor. That is now confirmed by the
vendor rather than inferred from a single counter-example.

### 3. `--permission-prompt-tool` was never undocumented

It is in the CLI reference. It is absent from `--help`. Those are different claims, and
[0015](decisions/0015-three-kinds-of-needs-you.md) and `vendor-capabilities.md` said the wrong one.
The documentation also states two constraints we had not measured:

> "Claude Code waits for that tool's MCP server to connect before running the first turn, up to the
> `MCP_TIMEOUT` startup timeout of **30 seconds**. The prompt tool **can't approve an MCP tool marked
> as requiring user interaction**: Claude Code converts an `allow` result for one to a deny."

### 4. `--permission-mode manual` is an alias for `default`, by design

Recorded as "a no-op headless". The documentation: *"`manual` as an alias for `default`… the mode the
UI labels Manual… `claude --help` lists it in place of `default`, and both values work."* Our
observation (session reports `default`) was correct; the interpretation was not.

---

## Capabilities the design predates and should be measured against

### Dynamic workflows — the vendor ships an orchestrator with the same shape as AER's engine

A **JavaScript script that orchestrates subagents at scale**, executed by a runtime in the background
while the session stays responsive. This is close enough to what AER Flow does that it must be
compared deliberately rather than discovered later.

```javascript
export const meta = { name: 'audit-routes', description: '…' }
const found = await agent('List every .ts file under src/routes/.', { schema: { … } })
const audits = await pipeline(found.files, file => agent(`Audit ${file}…`, { label: file }))
return audits.filter(Boolean)
```

| documented property | value |
|---|---|
| primitives | `agent()` spawns one subagent; `pipeline()` runs one per item |
| concurrency | **up to 16 concurrent agents**, fewer on low-core machines |
| total cap | **1,000 agents per run** |
| user input mid-run | **none** — *"Only agent permission prompts can pause a run. For sign-off between stages, run each stage as its own workflow"* |
| filesystem/shell | the script has none; only its agents act |
| resumability | resumable **within the same session**; completed agents return cached results. Exiting Claude Code loses the run |
| storage | `.claude/workflows/` (project) or `~/.claude/workflows/` (personal); saved runs become `/<name>` commands; distributable in plugins as `/<plugin>:<name>` |
| input | an `args` global |
| subagent permissions | **always `acceptEdits`**, inheriting the tool allowlist, *regardless of the session's mode* |
| approval | prompted per run except in bypass / `-p` / SDK, where *"the run starts immediately"* |
| disable | `disableWorkflows` setting, `CLAUDE_CODE_DISABLE_WORKFLOWS=1`, or `/config` |

**Directly relevant to our decisions:**

- *"No mid-run user input… For sign-off between stages, run each stage as its own workflow"* is the
  vendor hitting the same wall [0015](decisions/0015-three-kinds-of-needs-you.md) and the gate model
  are built around — and choosing the opposite trade-off. Worth understanding before we commit.
- The four-way comparison table (subagents / skills / agent teams / workflows) is organised around
  **who holds the plan**, which is precisely the axis the fan-out decision (#503 items 4–5) argues
  about.
- `agent()` + `pipeline()` is a fan-out primitive with an explicit concurrency cap. Our blockers model
  should be compared against it.

### Agent teams

*"A lead agent supervising peer sessions"*, coordinating through **a shared task list**, where
*"teammates keep running"* through an interruption. A third fan-out primitive, distinct from both
subagents and workflows.

### Channels — events pushed into a running session, and remote permission relay

An MCP server that **pushes events into a live session**, two-way, so the session reacts to things
that happen while nobody is at the terminal. Enabled per session with `--channels plugin:<name>@<mkt>`.

Two properties that land directly on our open work:

- **Permission relay.** *"If Claude hits a permission prompt while you're away from the terminal, the
  session pauses until you respond. Channel servers that declare the permission relay capability can
  forward these prompts to you so you can approve or deny remotely."* That is the remote-answer half
  of 0015's gate, already specified by the vendor.
- **Non-interactive safety.** *"When you run channels in non-interactive mode with `-p`, tools that
  need terminal input… are disabled so the session never stalls waiting for input."*

Gated: research preview, requires claude.ai or Console auth, Team/Enterprise must enable
`channelsEnabled`; `allowedChannelPlugins` restricts which plugins may register.

---

## To verify

Nothing in the sections above has been run yet except where `vendor-capabilities.md` already records a
measurement. The verification pass is tracked in #515 and the issues it references.

Priority, by how much design leans on it:

1. Sandbox on a non-Windows host — the 0004 claim cannot be tested on this machine at all.
2. Workflow `agent()`/`pipeline()` semantics vs. the blockers model.
3. Channels permission relay vs. 0015's gate.
4. `--permission-prompt-tool`'s 30 s `MCP_TIMEOUT` and the `requiresUserInteraction` allow→deny conversion.
5. `--max-budget-usd` enforcement (#479).
6. `agy`'s documented surface — not yet located; see below.

---

## `agy` — the documentation exists, and it overturns four of our rows

Docs live at `https://antigravity.google/docs/cli/...` (`overview`, `reference`, `permissions`,
`sandbox`, `modes`, `subagents`, `projects`, and `commands/*`). None of it had been read.

### 1. `command(...)` rules are documented as **regex**, and we recorded them as literal

This is the most consequential conflict in the audit, because
`vendor-capabilities.md` calls its literal-matching finding *"the single most consequential finding
for the permission surface"*, and [0004](decisions/0004-permission-scopes.md) carries the consequence
that a command *family* cannot be pre-authorised on `agy` at all.

The documentation says the opposite:

> "Each whitespace-separated token is evaluated as an **anchored regular expression**."
> `command(npm run (build|lint|test))` matches `npm run build` and `npm run test`.

Our measurement (against **1.1.6**) was that `command(node)` and `command(node .*)` both **denied**
`node --version`, while `command(node --version)` ran.

**Partially reconcilable, not fully.** Per-token anchoring explains `command(node)` failing: the rule
has one token, the command has two, so the extra token is uncovered. It does **not** explain
`command(node .*)` failing — `.*` anchored should match `--version`.

**RESOLVED 2026-07-24 — matching is literal, and the documentation is wrong.** Re-run against
**1.1.7** with the operator's explicit authorisation, one rule at a time, restoring the settings file
byte-exactly after every case:

| rule | if literal | if regex | **observed** |
|---|---|---|---|
| `command(node)` | denied | denied | denied |
| `command(node .*)` | denied | *granted* | **denied** |
| `command(node (--version\|--help))` | denied | *granted* | **denied** |
| `command(node --version)` | granted | granted | **granted** |

Both discriminating rules failed, **including the documentation's own alternation form**. So the
1.1.6 finding holds on 1.1.7, and 0004's consequence stands: **AER cannot pre-authorise a family of
commands on `agy`**, only enumerate exact command lines. The asymmetry recorded in #515 is unchanged.

**This is the only row in the entire audit where the documentation is wrong and our measurement was
right.** Every other correction ran the other way. That asymmetry is worth holding onto: it is why
documentation carries the evidence class *documented* rather than *verified*. A vendor's claim about
its own product is still a claim, and the run is what settles it — in both directions.

Method note, since it was the blocking constraint: this could not be tested unattended, because `agy`
grants live only in the operator's real `~/.gemini/antigravity-cli/settings.json`. The run happened on
explicit authorisation, with a byte-exact backup taken first, a restore after **every individual
case**, and the file's SHA-256 verified unchanged afterwards
(`d6b5507858fb371caa2a4827449fbe291f0032a341fe22ada8af99e73dfbf9df`, before and after).

### 2. There is an `ask` list, and the precedence is a three-rung ladder

We recorded `permissions.allow` / `.deny`. There are **three** lists — `allow`, `deny`, **`ask`** —
and:

> "Conflicting rules are strictly evaluated in priority order: **Deny > Ask > Allow**."

So `agy` has the same allow / ask / deny shape as `claude`'s `auto-mode` classifier and as
[0022](decisions/0022-permission-ladder-and-denial-is-an-answer.md)'s ladder. Three independent
designs, one shape. 0022 should be reconciled against both rather than either.

Also documented and not recorded by us:
- **Implicit rules** — writing a file grants read on the same path; denying read blocks write.
- **Defaults** — workspace files auto-allowed, web browsing asks, *unconfigured actions default to ask*.
- **Interactive scope editing** — a user may edit the target string to widen scope before approving,
  "except for terminal commands".
- **Windows path normalisation** — paths are normalised before rule evaluation "by stripping drive
  letters and converting all backslashes to forward slashes". Directly relevant to AER on Windows.

### 3. `agy` sandboxes on Windows; `claude` does not

| OS | `agy` mechanism | `claude` mechanism |
|---|---|---|
| Linux | `nsjail` (namespaces + cgroups) | `bubblewrap` |
| macOS | `sandbox-exec` | Seatbelt |
| **Windows** | **`AppContainer`** | **not supported** |

Enabled by `enableTerminalSandbox` in settings (default `false`), restricting shell execution,
filesystem, network, and CPU/memory. Per-execution override both ways: *"Yes, and run without sandbox
restrictions"* when enabled, *"Yes, and run in sandbox"* when disabled.

**On the operator's Windows host, `agy` can contain a process and `claude` cannot.** That is the real
asymmetry, it is platform-dependent, and neither of the two previous versions of the 0004 claim said
so.

### 4. `agy` does report quota — just not headlessly

`vendor-capabilities.md` says *"`agy` — nothing"*. Documented:

- **`/usage`** (alias **`/quota`**) — "Display model quota usage"; shows "your usage limits and
  remaining requests/tokens for each supported model (e.g. Gemini 3.5 Flash, Gemini 3.1 Pro)", and
  triggers "a fresh check of your quotas on disk and from the backend service".
- **`/credits`** — "View remaining G1 credits and purchase links", with a `useG1Credits` setting to
  spend personal credits once quotas are exhausted.

**It opens an interactive TUI panel.** So our observation — `agy -p "/usage"` produces no report — was
correct, and the *conclusion* ("agy has no usage data") was wrong. The data exists and reaches a
backend; what is missing is a non-interactive path to it. That reframes #479 from "impossible on agy"
to "needs a different route" — and the local RPC server (#508) is the obvious candidate.

### 5. `toolPermission` has four values, and the binary strings I guessed at were these

`toolPermission`: `request-review` (default) · `proceed-in-sandbox` · `always-proceed` · `strict`.

Worth recording as a near-miss: `always-proceed`, `proceed-in-sandbox` and `request-review` were all
turned up by the binary scan and were about to be tested **as command-line flags**. They are settings
values. The scan surfaced real strings and my interpretation of them was wrong — which is the whole
argument for reading the docs first.

### 6. Slash commands `agy` has that our records never mentioned

| command | documented as |
|---|---|
| `/btw <query>` | "Ask a side question in the background **without interrupting the main conversation**" |
| `/fork` / `/branch` | "Clone the current conversation thread into a **new parallel session**" |
| `/agents` | "Agent Manager Panel to switch custom agents and **monitor background subagents**" |
| `/tasks` | "Task Manager Panel to monitor background shell execution logs" |
| `/rewind` / `/undo` | "Roll back your conversation history to a previous message" |
| `/context` | "context usage visualization panel" |
| `/permissions` | "interactive tool permissions manager panel" |
| `/diff` | "Interactive Diff Viewer to view changes, turns, and commits" |
| `/planning` | "multi-turn plan generation mode" |
| `/hooks`, `/skills`, `/mcp`, `/model`, `/statusline`, `/keybindings`, `/artifact` | — |

Two land directly on open work:

- **`/btw` is a documented answer to the queued-message problem (#462)** — a side question that does
  not interrupt the running turn. Worth studying before we design ours.
- **`Alt+J` "switches focus to the next subagent awaiting confirmation"** and **`Ctrl+K` "approves the
  pending subagent action"**. `agy` already models *a queue of gates across parallel subagents* with
  keyboard affordances — which is close to what the room list is being designed to do.

### 7. Settings keys we had not recorded

`allowNonWorkspaceAccess` (default `false`) — "Permit agent file access outside workspace", which is
almost certainly the mechanism behind the cwd sharp edge in `vendor-capabilities.md`.
Plus `artifactReviewPolicy` (`asks-for-review` / `agent-decides` / `always-proceed`), `colorScheme`,
`altScreenMode`, `notifications`, `verbosity`, `enableTelemetry`, `editor`, `runningLightSpeed`.

---

## Verification pass

Documented claims run against the live CLIs. **Verified** means observed, not read.

### `--bg` background sessions — **verified**

```
$ claude --bg --name aer-probe-bg "Write a file called hello.txt containing BANANA, then stop."
backgrounded · 330a655f · aer-probe-bg
  claude agents             list sessions
  claude attach 330a655f    open in this terminal
  claude logs 330a655f      show recent output
  claude stop 330a655f      stop this session
```

It appears in the registry, with its own working directory and the name we set:

```
background | blocked | id 330a655f | aer-probe-bg | …\scratchpad\bgtest
```

**So #506's original conclusion was wrong and its correction holds:** `claude agents --json` sees
`--bg` sessions, and a `-p` run simply is not one. If AER spawns workers as `--bg` sessions it
inherits the whole lifecycle — `attach`, `logs`, `stop`, `rm`, `respawn`, and a supervisor that
survives its own restart.

Two observations worth more than the flag itself:

- **The probe session's state was `blocked`, because it wanted to `Write` and was waiting on
  permission.** That is a background worker sitting on a gate, surfaced in a machine-readable
  registry — the exact object 0015's durable-gate section and the room list are designed around,
  already modelled by the vendor.
- **State vocabulary, observed:** `working` · `idle` · `blocked` · `stopped`. Four values, from
  `--json` and `--json --all`. Not established as exhaustive.

`claude daemon status` also works and reports pid, version, uptime, origin (`transient — started
on-demand by claude (pid …)`), config path and log path. Useful for #478 readiness.

Cleanup: `claude stop <id>` then `claude rm <id>` removed only the probe session; the operator's own
sessions were untouched.

### Both vendors self-updated during this one session — **verified, unprompted**

The staleness trigger built in #504 fired on its own within hours:

```
[STALE  ] claude has moved: findings were recorded against 2.1.219 on 2026-07-24,
          but 2.1.220 is installed. Every row for this vendor is now unverified.
[ok     ] agy 1.1.7 — findings recorded against this exact version on 2026-07-24.
```

Combined with `agy` moving 1.1.6 → 1.1.7 earlier the same day, **both CLIs shipped a new version
inside a single working session.** Vendor drift is not a quarterly concern to design around; it is
hours-scale. Anything derived from a probe needs a version attached or it is already decaying.

### `PreToolUse` hook fires where the permission-prompt tool does not — **verified**

The claim that decides AER's gate mechanism. A hook was installed via `--settings` **inline JSON**, so
nothing was written to the operator's configuration, and asked to deny a `Write`:

```json
{"hooks":{"PreToolUse":[{"matcher":"*","hooks":[{"type":"command","command":"python hook.py"}]}]}}
```

```json
{"hookSpecificOutput":{"hookEventName":"PreToolUse",
 "permissionDecision":"deny","permissionDecisionReason":"blocked by aer probe hook"}}
```

| `--permission-mode` | hook fired | write blocked |
|---|---|---|
| `auto` — where `--permission-prompt-tool` is silently skipped (#514) | **yes** | **yes** |
| `bypassPermissions` — the most permissive mode there is | **yes** | **yes** |

The worker reported: *"Blocked — a hook named 'aer probe hook' rejected the Write to `x.txt`. I'm not
going to route around it."*

**So a `PreToolUse` hook is the only gate instrument observed to hold in every mode.** It is what AER
should install on a `claude` worker. `--permission-prompt-tool` remains useful, but as a step-6
callback it is bypassed by `auto`, by `acceptEdits`, by `bypassPermissions`, and by any allow rule.

The payload is **richer than the permission-prompt tool's**:

```json
{ "session_id": "4e75b9d5-…", "prompt_id": "230ecffc-…",
  "transcript_path": "C:\\Users\\…\\<session>.jsonl",
  "cwd": "…\\hooktest",
  "permission_mode": "bypassPermissions",
  "effort": { "level": "high" },
  "hook_event_name": "PreToolUse",
  "tool_name": "Write",
  "tool_input": { "file_path": "…\\x.txt", "content": "BANANA\n" },
  "tool_use_id": "toolu_01HS7fN4VsAy6Egjdu2fYtLk" }
```

Three fields matter beyond the call itself:

- **`permission_mode`** — the gate is told what regime it is running under, so it can refuse to
  present itself as a control when the session is in a mode that would otherwise skip it.
- **`transcript_path`** — the conversation on disk. Structured access without parsing stdout.
- **`effort.level`** — not in the hooks reference we read; noted as undocumented-but-present.

Decision values are `allow` / `deny` / `ask` / `defer`, plus `updatedInput` to rewrite the call and
`additionalContext` to inject text without blocking. Exit 2 is an alternative deny path with stderr as
the reason.

### `defer` ends the query, and the session resumes — **verified end to end**

**This is 0015's durable gate, shipping.** The conflict below is resolved: the SDK reading is right.

A `PreToolUse` hook returning `defer`:

```json
{"hookSpecificOutput":{"hookEventName":"PreToolUse",
 "permissionDecision":"defer","permissionDecisionReason":"aer probe defer"}}
```

```
exit=0
subtype : success
reason  : tool_deferred        ← a distinct terminal reason
result  : (empty)
file written: NO
```

The query **ends cleanly** — not an error, not a denial — with `terminal_reason: "tool_deferred"`.
The process is then free to exit. Resuming that session with a hook that allows:

```
claude --resume ce6eea58-… --settings <hook that allows> -p "continue"
→ reason: completed
→ "Done — x.txt created in the working directory with the contents BANANA."
→ file written: YES
```

**So the pending work survives the process that was holding it, on disk, and completes when the gate
opens.** That is exactly the requirement 0015 states — *"the room records the pause when the question
is asked, not when it is answered"* — and it is available rather than needing to be built.

**Stated precisely, because the distinction matters:** what is verified is that the session persists
across process exit and the work completes once the gate allows it. Whether the *identical*
`tool_use_id` is replayed, versus the model re-attempting the same work, is **not** established. That
difference decides whether AER can promise "the exact call you approved is the one that ran", which is
a claim 0022's answer semantics may depend on.

Also documented, and worth having: when several hooks or rules apply, the precedence is
**`deny` > `defer` > `ask` > `allow`**. And `updatedInput` is ignored on a `defer`.

### The conflict this resolves

The SDK's user-input page says `defer` is how a gate outlives its process:

> "If a user might take longer to respond than your process can reasonably stay running, return the
> **`defer` hook decision**, which lets the process **exit and resume later from the persisted
> session**."

A summary of the CLI hooks reference read `defer` as *"proceed with normal permission flow (same as
exiting 0 with no output)"*. **The run above shows that is wrong**, and the SDK hooks page states the
correct behaviour outright: *"Returning `"defer"` **ends the query** so you can resume it later."*

**The lesson is about the reading, not the docs.** That misreading came from a *summarised extraction*
of a long page, not from the page itself — a lossy step between the source and the claim, which is the
same failure mode as trusting `--help` over the reference. When a documentation claim is load-bearing,
go to the passage, and then run it.

### Hook events the design predates

The SDK hooks page lists events nothing in our records mentions. Four land directly on open work:

| event | why it matters |
|---|---|
| **`PermissionDenied`** | *"The auto mode classifier denies a tool call"* — a hook for exactly the #514 hole, so AER can observe classifier denials it would otherwise never see |
| **`Notification`** | fires with `permission_prompt` when Claude needs permission and `idle_prompt` when it is waiting for input — [0018](decisions/0018-attention-is-the-primary-signal.md)'s attention signal, as an event |
| **`Elicitation`** / `ElicitationResult` | *"An MCP server requests user input mid-task"* — a second, MCP-side path for "needs you" |
| **`SubagentStart`** / `SubagentStop` | carries `agent_transcript_path`, so fan-out progress is readable without parsing output |

Others present: `PostToolUse` (with `updatedToolOutput` to rewrite results before Claude sees them),
`PostToolUseFailure`, `PostToolBatch`, `UserPromptSubmit`, `UserPromptExpansion`, `MessageDisplay`,
`Stop`, `StopFailure`, `PreCompact`, `PostCompact`, `SessionStart`, `SessionEnd`, `Setup`,
`TeammateIdle`, `TaskCreated`, `TaskCompleted`, `ConfigChange`, `InstructionsLoaded`,
`WorktreeCreate`, `WorktreeRemove`, `CwdChanged`, `FileChanged`.

Two operational notes worth carrying into any gate we build: multiple hooks on one event **run in
parallel** and the most restrictive result wins; and a `PreToolUse` callback that exceeds its timeout
**blocks** the call (v2.1.210+), where earlier versions reported it as a user rejection and stalled
unattended sessions.

### Not verifiable from an agent session

- **`claude`'s sandbox** — not supported on native Windows, which is the only host available here.
- **`agy`'s `command(...)` regex-vs-literal conflict** — requires writing rules into the operator's
  real `~/.gemini/antigravity-cli/settings.json`. Needs an explicit decision about how to test safely.
- **Channels, workflows** — gated on plan/preview availability, and channels need a plugin install.

---

## The Agent SDK docs specify most of what 0015 is designing

The single highest-value page in the sweep. It also **explains the `auto`-mode finding mechanically**
rather than leaving it as an observation.

### The permission evaluation order — six steps, in this order

1. **Hooks** (`PreToolUse`) — "A hook can deny the call outright." A hook deny applies **even in
   `bypassPermissions`**.
2. **Deny rules** — block "even in `bypassPermissions` mode".
3. **Ask rules** — fall through to the approval callback "even in `bypassPermissions` mode".
4. **Permission mode** — `auto` is here: "A model classifier approves or denies permission prompts."
5. **Allow rules**.
6. **`canUseTool` callback** — only reached if nothing above resolved the call.

**This is why `--permission-mode auto` silently bypassed our gate.** `auto` resolves at step 4;
`--permission-prompt-tool` is the step-6 callback. The measurement was right and the mechanism is
documented:

> "**Auto-approved tools never reach `canUseTool`.** A tool call approved at any earlier step… skips
> your `canUseTool` callback, so permission checks you put there are **silently bypassed** for that
> tool. For checks that must run on every tool call, use a **`PreToolUse` hook**: hooks run before
> every other step, and a hook deny applies even in `bypassPermissions` mode."

**So AER's gate should be a `PreToolUse` hook, not only a permission-prompt tool.** That is a
materially better mechanism than the one [0015](decisions/0015-three-kinds-of-needs-you.md) chose, it
closes the hole recorded in #514, and it was documented the whole time. An `ask` rule is the second
always-fires instrument.

### `defer` — the durable-gate problem, already solved

0015 devotes a section to a pause outliving the process holding it, because "the process holding it
open is the one a crash kills". The SDK has a name for this:

> "The callback can stay pending indefinitely… If a user might take longer to respond than your
> process can reasonably stay running, return the **`defer` hook decision**, which lets the process
> exit and resume later from the **persisted session**."

That is the exact requirement 0015 states, with a shipped mechanism. It must be evaluated before we
build our own.

### `PermissionRequest` hook — the notify half of "needs you"

> "You can also use the `PermissionRequest` hook to send external notifications (Slack, email, push)
> when Claude is waiting for approval."

[0018](decisions/0018-attention-is-the-primary-signal.md)'s attention signal has a vendor-side hook.

### `AskUserQuestion` — the "decision" kind, with a wire format

0015's three kinds are permission / decision / approval. The **decision** kind is a shipped tool with
a defined schema: a `questions` array of `{ question, header (≤12 chars), options[{label, description}],
multiSelect }`, answered by returning `answers` keyed by question text, plus an optional freeform
`response`. TypeScript can request `preview` per option (`markdown` or `html`) via
`toolConfig.askUserQuestion.previewFormat`.

It **always reaches the callback even when an allow rule matches** — as do MCP tools marked
`_meta["anthropic/requiresUserInteraction"]` and connector tools an org set to `ask`. In `dontAsk`
mode all three are denied instead.

Limits: 1–4 questions, 2–4 options each; **not available in subagents**.

### The answer shapes we measured are the full documented set

`{ behavior: "allow", updatedInput }` / `{ behavior: "deny", message }`, plus a third we had not
found: **`updatedPermissions`**, echoing back a suggested `PermissionUpdate` from the callback's
`suggestions` argument. A suggestion with the `localSettings` destination writes the rule to
`.claude/settings.local.json` so future sessions skip the prompt.

That is **"approve and remember"** as a first-class concept, and the documented response vocabulary is
richer than approve/reject: *approve · approve with changes · approve and remember · reject · suggest
alternative · redirect entirely*. Worth comparing against
[0022](decisions/0022-permission-ladder-and-denial-is-an-answer.md) before designing our own set.

## Agent teams — the blockers model, shipping

The fan-out design settled in #503 items 4–5 on GitHub-style blockers, with parallelism emergent.
`CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1` enables a feature that already works this way:

> "Tasks can also **depend on other tasks**: a pending task with unresolved dependencies **cannot be
> claimed** until those dependencies are completed."
> "When a teammate completes a task that other tasks depend on, **blocked tasks unblock without
> manual intervention**."

Also: three task states (pending / in progress / completed), **file locking** on claim to prevent two
teammates taking the same task, self-claim as well as lead-assign, and a mailbox per agent at
`~/.claude/teams/{team}/inboxes/{agent}.json`. Task lists persist at `~/.claude/tasks/{team}/` and
survive resumption; team config does not.

**The owner's blockers design is independently validated by a shipping implementation.** That is a
good outcome for #503 items 4–5 and removes the concern that it was novel.

Security detail worth copying: a teammate **cannot approve a permission prompt on your behalf**, and
"in auto mode, the classifier treats an approval claim relayed from another agent as **untrusted
input** rather than confirmation from you." Teammate prompts surface in the **lead** session.

Hooks that gate the loop: `TeammateIdle`, `TaskCreated`, `TaskCompleted` — each blocks with exit
code 2 and sends feedback. Limits: no nested teams, one team per session, lead is fixed, permissions
set at spawn, no session resumption with in-process teammates.

---

## `agy` has hooks, and an SDK — the two pages that change the most

Both were on the unread Tier 1 list in [`vendor-coverage.md`](vendor-coverage.md), flagged as the
largest hole. They were, and reading them overturns a claim repeated throughout this audit.

> **Superseded in part — read [§5 of the Tier 1 + Tier 2 read](#5-agys-cli-hooks-work--and-the-gate-is-symmetric).**
> This section's "CLI support is not verified" caveat, and the four candidate explanations below,
> are resolved: **agy's CLI hooks work.** They load from `<workspace>/.agents/hooks.json` and from
> `~/.gemini/config/hooks.json`, fire `PreToolUse`, and enforce `deny`. The original negative was an
> artifact of a broken hook *command*, not of hook support.

### `agy` documents a `PreToolUse` hook that can deny

**Every statement in this audit about the gate being claude-only, or about `agy` having "none of
these" instruments, was made without reading this page.** `agy` documents five hook events:

| event | fires |
|---|---|
| `PreToolUse` | before a tool is executed |
| `PostToolUse` | after a tool completes |
| `PreInvocation` | before the model is called |
| `PostInvocation` | after tool calls finish |
| `Stop` | when the execution loop terminates |

`PreToolUse` returns a **`decision`** of `allow` · `deny` · `ask` · **`force_ask`**, plus `reason` and
`permissionOverrides`. `force_ask` is documented as *"always prompts, ignoring cached permissions"* —
a **stronger** always-fires guarantee than anything claude documents, if it holds. `Stop` can return
`continue` to prevent termination.

Hooks are configured in `hooks.json` under `.agents/` or `~/.gemini/config/`, receive
`conversationId`, `workspacePaths`, `transcriptPath` and `artifactDirectoryPath` on stdin, and reply
on stdout.

**So the gate design may be symmetric after all**, which is the opposite of what
[0015](decisions/0015-three-kinds-of-needs-you.md) and #517 currently assume. That has to be settled
before either is rewritten.

#### Not verified on the CLI — and this is a negative claim, so it is scoped

A `PreToolUse` hook was placed at `.agents/hooks.json` in a scratch workspace with a guessed schema.
On an auto-allowed workspace file read — a call that demonstrably reached a tool, since the read
succeeded — **the hook did not fire**.

That is **not** evidence that `agy`'s CLI lacks hooks. Four candidate explanations, in rough order of
likelihood:

1. **The schema was guessed.** The documentation summary paraphrased the file as "maps hook names to
   event configurations" without giving a schema. The most likely explanation is simply that the file
   was malformed and silently ignored.
2. **Discovery uses `agy`'s own working directory, not `--add-dir`.** `agy -p` is already recorded as
   ignoring cwd and running under its install directory — so `.agents/` may never have been looked for
   where it was written.
3. `~/.gemini/config/hooks.json` may be the CLI's real location. No such file exists on this host, and
   creating one means writing to the operator's configuration.
4. The docs are written for Antigravity 2.0 and hooks may genuinely not be wired into the CLI.

**What would settle it:** the actual `hooks.json` schema from the page itself, then a re-test at both
locations. Until then this row is *"not observed on the surfaces tried"*, not *"absent"*.

### The Python SDK answers all three of #508's open questions

`pip install google-antigravity` — a Python framework, documented as exposing:

| what | why it matters |
|---|---|
| **per-turn and cumulative token usage** | this is the usage/cost data for **#479**, which we recorded as unavailable on `agy` |
| **streamed structured events**, "live model reasoning and output chunks", Pydantic-typed results | routing on structured events instead of parsed stdout — **Architecture Rule 1, structurally** |
| **`deny()` / `allow()` / `ask_user()`**, "declarative deny-by-default policy" | a permission surface with a human-approval primitive |
| nine lifecycle hook points, "Inspect / Decide / Transform" | the same gate shape, in-process |
| headless by design | usable the way AER would use it |

**That is #508's three unknowns — structured events, usage data, and permission control — answered by
one page, on the surface the audit had already nominated as the most promising and never read.**

It also reframes the `agy` half of the design: everything recorded so far assumes AER shells out to a
CLI. A Python SDK is a different integration shape entirely, with different guarantees, and the choice
between them was never made because one option was invisible.

---

## Should AER drive SDKs instead of CLIs? — **No, and the reason is contractual**

Both vendors ship an SDK, and on the technical merits the SDK path looks strictly better than shelling
out to a CLI. It was worth evaluating properly rather than assuming. **It is foreclosed, by policy
rather than capability**, and the constraint is one sentence in the setup steps:

> "**Unless previously approved, Anthropic does not allow third party developers to offer claude.ai
> login or rate limits for their products, including agents built on the Claude Agent SDK. Please use
> the API key authentication methods described in this document instead.**"

The Agent SDK authenticates with `ANTHROPIC_API_KEY`, Bedrock, Vertex, or Foundry. **AER Flow's stated
premise is subscriptions, not API keys** (CLAUDE.md: *"the project's whole point is working against
**subscriptions**, not API keys"*, and *"dropping in an API key to make a gate pass would test a
different auth path than the one the project exists to support"*).

So the existing architecture is **correct, and for the right reason**. Shelling out to an
already-authenticated CLI is not a compromise forced by ignorance of the SDK — it is the only path
consistent with the product's premise. That is worth stating plainly, because every other section of
this audit corrects something; this one confirms a decision that was already right.

### What the SDK would have bought, so the cost of the constraint is visible

| | SDK | CLI (what we have) |
|---|---|---|
| gate | in-process `canUseTool` + hook callbacks | hook via `--settings`, or an MCP server |
| `--bare` conflict (#521) | does not exist | real, and forecloses the gate |
| routing signal | typed events, Pydantic/TS types | `stream-json` parsed from stdout |
| session persistence | **`SessionStore` interface** — `append`/`load`, with S3/Redis/Postgres reference adapters | `~/.claude/projects/*.jsonl` on the local disk |
| cost/usage | per-turn and cumulative, exposed | `total_cost_usd` in the stream; `/usage` headless |
| language | Python / TypeScript | any |

`SessionStore` is the sharpest loss: it is a pluggable durable transcript store with a published
conformance suite, dual-write semantics, and a `mirror_error` event — very close to what the room model
needs, and unavailable on the terms we operate under.

### `agy`'s SDK — same answer, and this one is enforced in code

`agy` ships a Python SDK (`pip install google-antigravity`, 0.1.8). It was checked rather than
assumed, and it does not offer an escape from the constraint — it is **stricter**, because the
restriction is not a policy sentence but a code path.

The package was downloaded and **read without being run** (`pip download --no-deps`).
`google/antigravity/models.py` defines exactly two endpoint types, and validation *raises* without a
key:

```python
class GeminiAPIEndpoint(ModelEndpoint):
  def validate_endpoint(self) -> None:
    if not (self.api_key or os.environ.get("GEMINI_API_KEY")):
      raise ValueError(
          "A Gemini API key is required. Set it via"
          " GEMINI_API_KEY environment variable or via"
          " LocalAgentConfig(api_key=...)."
      )

class VertexEndpoint(ModelEndpoint):        # project + location, i.e. GCP ADC
  def validate_endpoint(self) -> None:
    if not (self.project and self.location):
      raise ValueError("For Vertex AI, a GCP project and location must be set.")
```

A search of every `.py` in the package for `keyring`, `oauth`, `keychain`, `secret_service`, or the
CLI's credential store returns **nothing**. The CLI stores its login in the OS keyring (Windows
Credential Manager / Keychain / Secret Service); the SDK never reads it. There is no subscription
path to find, so `Evidence.Absent` is safe here in a way it usually is not — the surface list is the
package's entire source, not a guessed flag name.

Both SDKs bundle a native binary (`google/antigravity/bin/localharness.exe`, 120 MB; the claude SDKs
"bundle a native Claude Code binary for your platform"). The SDK is not a different transport — it is
the same local harness with a typed wrapper and a **different auth requirement**. That is the whole
of the difference, and it is the part that disqualifies it.

**Conclusion: CLI for both vendors.** Not a compromise — the only subscription-compatible path
either vendor offers. The SDK question is closed.

### One thing to take from the SDK regardless

The SDK's `SessionStore` contract is a good design to copy even while implementing it ourselves:
required `append`/`load` keyed by `{projectKey, sessionId, subpath}`, optional `listSessions` /
`delete` / `listSubkeys`, entries treated as opaque ordered JSON, mirror writes best-effort with the
local copy authoritative, and a distinct event when mirroring fails. That is a well-worn shape for
exactly the problem 0015's durable gate and the room store both have.

---

## The Tier 1 + Tier 2 read (#527) — method, and what it overturned

### Method: breadth and depth stopped competing

Reading ~250 pages one at a time is not repeatable, and a summarizing fetch is lossy — it produced
the first, wrong reading of `defer`. But **every finding that changed a decision was one sentence**,
and those sentences share a grammar: *skips, only, cannot, must, requires, before v, will become*.

`pixi run vendor-survey` mirrors both corpora from their published indexes (claude `llms.txt` → 172
raw `.md` pages; agy `llms.txt` + `sitemap.xml` → 77 server-rendered pages) and harvests that
sentence class corpus-wide: **249 pages / 7.0 MB → 1,475 unique constraint sentences**, tagged against
AER's open questions with page:line provenance, plus a ledger giving **every page a disposition**.
100% page coverage at ~1% of the bytes.

It justified itself immediately: several findings below come from `glossary`, `channels`, `chrome`,
`context-window`, and `desktop-scheduled-tasks` — pages no depth-first ranking would have opened.

### 1. The strongest gate primitive is one we were not considering

`_meta["anthropic/requiresUserInteraction"]` — which **AER's own MCP server can set** — survives
every mechanism that defeats the alternatives: allow rules, *all* permission modes including
`bypassPermissions`, the auto-mode classifier, and a permissive hook. It offers no "don't ask again".
That is 0015's "needs you" semantic enforced by the vendor rather than by our discipline.

**The catch is architectural:** headless converts it to a hard deny (`MCP tool requires user
interaction; not supported via --permission-prompt-tool`), and `dontAsk` denies it too. A perfect
gate where a human surface exists; a hard block where none does. AER must decide which invocations
carry a human surface.

Second primitive, for non-MCP tools: **a hook's `"ask"` forces a prompt even in auto mode** — "the
classifier can still deny the tool call, but it can't approve the call silently."

### 2. `defer` cannot carry the durable gate

> "`defer` only works when Claude makes a single tool call in the turn."

The earlier end-to-end verification used a single-tool turn, so it passed. `defer` **silently does
not apply** when the model batches tool calls, which is the common case.

### 3. The blocking-MCP gate can be reaped mid-wait

An MCP call that sends **no response and no progress notification** for the idle window aborts. A
per-server `timeout` ≥ 1000 acts as a floor, or the server must emit progress. Without one of those,
a slow human produces an error instead of a gate. (Real permission *prompts*, by contrast, never
auto-resolve on idle.)

### 4. An API key silently disables the features AER is built on

> "Remote Control, `/schedule`, claude.ai MCP connectors, and notification preferences are **disabled
> when `ANTHROPIC_API_KEY` / `apiKeyHelper` / `ANTHROPIC_AUTH_TOKEN` is set, even if a Claude.ai
> login also exists**."

This upgrades **Credential Isolation (architecture rule 4)** from a premise to a *functional*
invariant: a stray key does not degrade gracefully, it removes AER's entire remote story.

### 5. agy's CLI hooks work — and the gate **is** symmetric

Recorded first as "not observed", then wrongly narrowed to "IDE-only". Both were wrong.

| location | result |
|---|---|
| `<workspace>/.agents/hooks.json` (workspace registered via `--add-dir`) | **loads and fires** |
| `~/.gemini/config/hooks.json` | **loads, fires, `deny` enforced** |
| `~/.gemini/antigravity-cli/hooks.json` | does not load — see [antigravity-cli#49](https://github.com/google-antigravity/antigravity-cli/issues/49) |

Verified positive: `Tool execution was blocked by the system gate policy (AER global gate test).`

That last row matches an open upstream issue: the CLI's own `/hooks` command *writes* to
`antigravity-cli/` while the loader *reads* `config/`, so anyone configuring hooks through the CLI
gets a file that is never read.

So agy's control surface is real and CLI-reachable: `allow`/`deny`/`ask`/`force_ask`,
`permissionOverrides`, `PreInvocation.injectSteps`, `PostInvocation.terminationBehavior`,
`Stop.decision:"continue"`, `Stop.fullyIdle`. **On several axes it is stronger than claude's.**

**Why it was called wrong twice, and the durable fix.** The hook command was `sh "<path>"`; JSON
quote-escaping added a leading backslash and `sh` exited 127. The hook fired every time. The detector
("did the side-effect file appear") could not distinguish *never fired* from *fired, command failed*
— **a negative from an instrument that cannot separate those is not evidence.**

`agy` had been writing the answer to disk the whole time, on every start:

> `hooks_manager.go:53] loaded 0 named hooks from 0 hooks.json file(s)`

**Check the vendor's own logs before concluding absence.** Same lesson as reading the docs before
reverse-engineering, and as grepping the repo before re-deriving a known finding — third costume.

Also newly on the permanent source list: **the CLI has a public issue tracker and changelog**
(`google-antigravity/antigravity-cli`) that this audit had never consulted.

### 6. Fan-out limits are now concrete (#503 items 4–5)

Concurrent subagents cap **20** (`CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS`); nested subagents **off by
default**; nested teams **impossible**; per-teammate modes **cannot be set at spawn**; a parent's
`bypassPermissions`/`acceptEdits`/`auto` **applies to every subagent and cannot be overridden**.

Decisive for the design: **a teammate's background work cannot outlive the lead's process**, so
teammates make the lead a single point of failure — while `--bg` sessions survive detach entirely.

### 7. The vendor's changelog is a test plan for AER's own supervisor

`Aer.Daemon` supervises workers and survives restarts; the vendor built the same thing and shipped
the bugs. Stale lock **whose PID the OS reused**; auto-upgrade **silently killing all live sessions**;
sleep/wake needing **clock-jump detection** rather than elapsed-idle; workers inheriting a **stale
`PATH`** and a **stale model** from the daemon rather than the dispatching shell and `settings.json`;
daemon handover judged by **embedded build timestamp**, not version string.

### 8. Smaller corrections worth carrying

- **`--add-dir` grants file access, not configuration.** Hooks load only from the process's own cwd
  `.claude/`, with **no parent-directory fallback** — so AER must control the worker's working
  directory or pass `--settings`. With #521 (`--bare` disables hooks even via `--settings`), the
  viable gate combinations are narrow.
- **Hooks on Windows run through Git Bash** and historically failed *silently* there. Windows is the
  primary development host.
- `PreToolUse`'s top-level `decision`/`reason` are **deprecated**; `PermissionRequest` fires **only in
  auto mode**; `PreToolUse` never fires for `EndConversation`, and typing `/skillname` bypasses it.
- **Two processes cannot write one transcript** — AER must serialise per session.
- **`usage.output_tokens` excludes subagent tokens**; whole-tree accounting needs `modelUsage`.
- **`errorCode: "credits_required"`** is a typed signal for *subscription* quota exhaustion (#479).
- **Transcript parsing is explicitly unsupported** — verified that AER does not do it.
- agy: **credentials are coupled to its background daemon** ("If the background daemon is locked or
  headless, the CLI cannot read credentials") — a headless agy daemon is an *auth* failure.
- Two more agy doc inaccuracies: `--cwd` is documented and **does not exist** (the real flag is
  `--add-dir`), and the live `PreToolUse` payload carries an **undocumented `modelName`** field with
  `transcriptPath` pointing at `transcript_full.jsonl`, not the documented `transcript.jsonl`.

## Sources

- Claude Code docs index — https://code.claude.com/docs/llms.txt
- [CLI reference](https://code.claude.com/docs/en/cli-reference) ·
  [Sandboxing](https://code.claude.com/docs/en/sandboxing) ·
  [Permissions](https://code.claude.com/docs/en/permissions) ·
  [Workflows](https://code.claude.com/docs/en/workflows) ·
  [Channels](https://code.claude.com/docs/en/channels) ·
  [Agent teams](https://code.claude.com/docs/en/agent-teams) ·
  [SDK permissions](https://code.claude.com/docs/en/agent-sdk/permissions) ·
  [SDK user input](https://code.claude.com/docs/en/agent-sdk/user-input) ·
  [SDK overview](https://code.claude.com/docs/en/agent-sdk/overview) ·
  [SDK session storage](https://code.claude.com/docs/en/agent-sdk/session-storage)
- Antigravity CLI — [overview](https://antigravity.google/docs/cli/overview) ·
  [reference](https://antigravity.google/docs/cli/reference) ·
  [permissions](https://antigravity.google/docs/cli/permissions) ·
  [sandbox](https://antigravity.google/docs/cli/sandbox) ·
  [usage](https://antigravity.google/docs/cli/commands/usage) ·
  [install & auth](https://antigravity.google/docs/cli/install)
- Antigravity SDK / terms — [SDK overview](https://antigravity.google/docs/sdk/overview) ·
  [plans](https://antigravity.google/docs/plans) ·
  [FAQ](https://antigravity.google/docs/faq) ·
  [terms](https://antigravity.google/terms)
- `google-antigravity` 0.1.8 wheel, read via `pip download --no-deps`
- **Machine-readable indexes** (the whole corpus, mirrored by `pixi run vendor-survey`) —
  [claude `llms.txt`](https://code.claude.com/docs/llms.txt) ·
  [agy `llms.txt`](https://antigravity.google/llms.txt) ·
  [agy `sitemap.xml`](https://antigravity.google/sitemap.xml)
- **`google-antigravity/antigravity-cli`** — the CLI's public
  [issue tracker](https://github.com/google-antigravity/antigravity-cli/issues) and
  [changelog](https://github.com/google-antigravity/antigravity-cli/blob/main/CHANGELOG.md).
  Never consulted before 2026-07-25; [#49](https://github.com/google-antigravity/antigravity-cli/issues/49)
  documents the hooks path misalignment independently. **Check it before recording any agy negative.**
- **`agy`'s own logs** — `~/.gemini/antigravity-cli/log/cli-*.log`. Reports hook discovery
  (`hooks_manager.go`), hook execution failures (`command_hook_executor.go`), and the resolved
  workspace on every start. The fastest disproof of an "it isn't there" claim.
