# Vendor capabilities — what each worker CLI can actually do

**Status: verified reference. Every row was observed, not read off a help text.** Probed 2026-07-24
(`#472`) against `claude` 2.1.219 and `agy` 1.1.6 on Windows. Where a claim rests on a live run, the
observation is quoted. Where it rests on inspecting the shipped binary or vendor docs, it says so.

This exists because the M25 design assumed capabilities in several places, and design that assumes
wrongly is worse than design that knows its limits. Two assumptions were **wrong** and are corrected
below. Re-run the probes before trusting this after a vendor update — both CLIs self-update.

## Why the probe method matters

A nested `claude` invoked from inside a Claude Code session inherits the parent's environment and
therefore its **tool set and MCP servers**, which no daemon-spawned worker ever has. An early probe
that stripped only `^CLAUDE_CODE_` missed `CLAUDECODE`, `CLAUDE_EFFORT`, `CLAUDE_PID` and
`CLAUDE_JOB_DIR`, and produced a result we nearly wrote down as fact.

Strip **every** `^CLAUDE` variable, and verify the strip worked by reading `permissionMode` and the
`tools` array out of the `system:init` event — not by trusting the flags you passed:

```sh
STRIP=$(env | grep -o '^CLAUDE[A-Z_]*' | sed 's/^/-u /' | tr '\n' ' ')
env $STRIP claude -p --output-format stream-json --verbose "..."
```

## Capability matrix

| | `claude` 2.1.219 | `agy` 1.1.6 |
|---|---|---|
| Headless flag | `-p` / `--print` | `-p` / `--print` |
| Effort | `--effort low\|medium\|high\|xhigh\|max` | `--effort low\|medium\|high` |
| Extra directories | `--add-dir` | `--add-dir` (repeatable) |
| MCP | `mcp` subcommand, `--mcp-config`, `--strict-mcp-config` | **config file only** — `~/.gemini/config/mcp_config.json` |
| Permission modes | `--permission-mode acceptEdits\|auto\|bypassPermissions\|manual\|dontAsk\|plan` | `--mode accept-edits\|plan` |
| Per-call tool grant | `--allowedTools` / `--disallowedTools` | **none** — grants must be persisted to settings |
| `--permission-prompt-tool` | **absent** | **absent** |
| Sandbox | referenced in help only | **`--sandbox`, and it enforces** |
| Resume | `--resume`, `-c` / `--continue` | `-c` / `--continue`, `--conversation <id>` |
| Structured output | `--output-format stream-json --verbose` | not found |
| Other | `--agents <json>` | `--remote-control`, `--agent`, `--project` |

## Corrections to earlier assumptions

**`claude -p` does not auto-approve.** The opposite. With a clean environment, in a neutral directory:

| invocation | wrote the file? | `permissionMode` reported |
|---|---|---|
| `claude -p` (no flags) | no — denied | `default` |
| `claude -p --permission-mode manual` | no — denied | **`default`** |
| `claude -p --permission-mode acceptEdits` | yes | — |
| `claude -p --allowedTools Write` | yes | — |

**Both vendors fail closed**, which is the safer asymmetry to have been wrong about. Note also that
**`manual` is a no-op headless** — the session still reports `default` and no prompt is ever issued.

**MCP is not Claude-only.** `agy` loads MCP servers from `~/.gemini/config/mcp_config.json`
(`mcpServers`, stdio via `command`/`args`/`env`, or remote via `serverUrl`), and plugins may ship
their own. Observed spawning our server and running `server/discover` → `initialize` → `tools/list`.
Permission-by-consultation is therefore **uniform across vendors**.

## Denials are machine-readable

`claude`'s final result event carries the whole denied call, replayable once a human answers:

```json
"permission_denials":[{"tool_name":"Write","tool_use_id":"toolu_01…","tool_input":{"file_path":"…","content":"BANANA"}}]
```

`agy` denies with prose on stderr naming the missing permission and the rule that would grant it
("a tool required the `mcp` permission that headless mode cannot prompt for, so it was auto-denied").
Less structured, but it names the remedy.

## A blocking MCP tool holds a turn open — on both vendors

The mechanism [0015](decisions/0015-three-kinds-of-needs-you.md) depends on. A dependency-free stdio
MCP server exposed one tool whose handler did not reply until an out-of-band answer file appeared. A
watcher minted a random token **after** observing the call start, so a correct answer proves the turn
genuinely waited.

| vendor | blocked for | call metadata returned |
|---|---|---|
| `claude` | 10.9 s | `claudecode/toolUseId`, `progressToken` |
| `agy` | 10.3 s | `antigravity.google/conversation_id`, `artifacts_dir`, `progressToken` |

Two implementation constraints fall out:

- **The server is spawned twice by `claude`** — once to enumerate tools (killed straight after
  `tools/list`), then again for the real turn. It must be cheap to start and hold **no** in-memory
  state across spawns.
- **`agy` hands us the resume key at gate time.** `antigravity.google/conversation_id` is exactly what
  `agy --conversation <id>` resumes. A gate persisted with that id survives a host crash.

## `agy` permission grammar

Rules live in `~/.gemini/antigravity-cli/settings.json` under `permissions.allow` / `.deny`. This is
the **only** settings path — there is no project-local override file. Prefixes (from vendor docs):
`read_file`, `write_file`, `read_url`, `execute_url`, `command`, `unsandboxed`, `mcp`.

MCP rules take `mcp(server/tool)`, `mcp(server/*)` or `mcp(*)`. Observed: `mcp(aerhuman)` — the bare
server name — **does not match**; `mcp(aerhuman/*)` does.

**Command matching is stricter than the docs imply.** With the command line
`node C:/…/escape.js`, a rule of `command(node)` was **denied**; the exact full command line was
**granted**. Do not assume a bare binary name pre-authorises its invocations.

## `agy --sandbox` genuinely enforces

The only real enforcement primitive on either CLI. Same command, same allow-rule, sandbox the only
variable:

| | file write outside workspace | network |
|---|---|---|
| no `--sandbox` | `OK` (file created) | `OK status=200` |
| `--sandbox` | **blocked** | **blocked** |

Under `--sandbox` the run demanded a *separate* `unsandboxed(<target>)` grant on top of the already
granted `command(...)` — two independent gates, not one. Internally it is a `sandboxproxy` with a CEL
policy enforcer, blocked-request handling and OAuth2 credential brokering; vendor docs describe
`enableTerminalSandbox` as restricting execution to "OS containment rings".

This matters for [0004](decisions/0004-permission-scopes.md): a project-level ceiling is
*enforceable* on `agy` and only *advisory* on `claude`. Say so honestly when a worker is chosen,
rather than implying a guarantee we cannot keep.

## Sharp edges

**`agy -p` ignores the working directory.** It runs the agent in its own scratch directory
(`~/.gemini/antigravity-cli/scratch`), not the shell's cwd. Observed: a run launched from a workspace
could not find a file sitting in that workspace and began a recursive search of the whole home
directory. **Bind the room's directory explicitly with `--add-dir`** — never rely on cwd. Any adapter
that assumes cwd is silently pointing the worker somewhere else.

**`agy` emits PowerShell on Windows**, not POSIX shell — its `run_command` steps carry PowerShell
command lines. Pre-authorisation rules must match what it actually emits.

**`agy` has no per-call grant flag.** Every grant is a persisted edit to a global settings file. AER
cannot scope a grant to one run the way `--allowedTools` does for `claude`, so a per-run ceiling has
to come from `--sandbox` or from the MCP consultation path, not from flags.

## `--remote-control` — not yet characterised

Present in the binary and undocumented publicly. Static reading only: it flips a **persisted** setting
(`_remote_control_enabled`, `_remote_control_hostname`), generates a default hostname, and maintains an
outbound WebChannel connection to a Google-hosted relay (`newWebChannelHandler` /
`…V2`, `startRemoteControlConnection` / `…V2`, `UpdateInstanceMetadata`), with a warning path about
binding to a public IP. Outbound-only, so it would traverse NAT without port forwarding.

**Attempted, and it will not enable non-interactively.** `agy --remote-control -p …` reports
*"No valid authentication found"* and starts a **fresh OAuth login**, requesting scopes
(`cloud-platform`, `cclog`, `experimentsandconfigs`, `aicode`, userinfo) beyond what an ordinary
authenticated session holds — then fails with *"You are not logged into Antigravity"* without writing
any state. So remote control sits behind a **separate, interactive consent**, not merely a flag. An
ordinary `agy -p` still authenticates normally afterwards; the attempt does not disturb the existing
token. Enabling it therefore needs a human at a browser, which also means **AER cannot turn it on for
the operator** — worth knowing before designing any flow that assumes it.

Vendor forum discussion as of the probe date describes no *official* mobile
remote control, while several third-party clients exist; one reportedly speaks **Connect RPC to the
Antigravity language server** directly. That, plus the public Python SDK
(`pip install google-antigravity`) exposing streamed strongly-typed `ToolCall` events, thought deltas
and read-only-by-default capabilities, suggests a structured local integration path that would beat
parsing stdout — and would satisfy CLAUDE.md Architecture Rule 1 structurally, since Flow would never
need to read conversation text to route. Worth a feasibility spike before the adapter shape is fixed.

Related: `#472` (this probe), `#445` (the permission-request mechanism),
[0004](decisions/0004-permission-scopes.md), [0015](decisions/0015-three-kinds-of-needs-you.md).
