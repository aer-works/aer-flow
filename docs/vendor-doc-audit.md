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

## `agy` documentation — not yet audited

The equivalent sweep for `agy` has not been done. Everything currently recorded about it came from
`--help`, the binary, and live probing, which is exactly the method this audit exists to replace.
