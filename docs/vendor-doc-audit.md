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

**Evidence class: contradicted, unresolved.** One of these is wrong: the docs, our test, or the
version. It must be re-established before 0004 is rewritten again — and it **cannot be tested by an
agent session unattended**, because `agy` grants live only in the operator's real
`~/.gemini/antigravity-cli/settings.json`, which the probe suite is forbidden to touch.

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

### Not verifiable from an agent session

- **`claude`'s sandbox** — not supported on native Windows, which is the only host available here.
- **`agy`'s `command(...)` regex-vs-literal conflict** — requires writing rules into the operator's
  real `~/.gemini/antigravity-cli/settings.json`. Needs an explicit decision about how to test safely.
- **Channels, workflows** — gated on plan/preview availability, and channels need a plugin install.

---

## Sources

- Claude Code docs index — https://code.claude.com/docs/llms.txt
- [CLI reference](https://code.claude.com/docs/en/cli-reference) ·
  [Sandboxing](https://code.claude.com/docs/en/sandboxing) ·
  [Permissions](https://code.claude.com/docs/en/permissions) ·
  [Workflows](https://code.claude.com/docs/en/workflows) ·
  [Channels](https://code.claude.com/docs/en/channels)
- Antigravity CLI — [overview](https://antigravity.google/docs/cli/overview) ·
  [reference](https://antigravity.google/docs/cli/reference) ·
  [permissions](https://antigravity.google/docs/cli/permissions) ·
  [sandbox](https://antigravity.google/docs/cli/sandbox) ·
  [usage](https://antigravity.google/docs/cli/commands/usage)
