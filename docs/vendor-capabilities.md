# Vendor capabilities — what each worker CLI can actually do

**Status: verified reference, with a version split that matters.** Where a claim rests on a live run,
the observation is quoted. Where it rests on inspecting help text or the shipped binary, it says so —
and where a row says something is *absent*, it names the surfaces that absence was established on.

| established | against | covers |
|---|---|---|
| 2026-07-24, `#504` | `claude` 2.1.219, `agy` **1.1.7** | the rows the probe suite regenerates: usage, per-turn cost, structured output, `--permission-prompt-tool`, effort, `--add-dir`, plus the subcommand findings below |
| 2026-07-24, `#472` | `claude` 2.1.219, `agy` **1.1.6** | everything else — the permission grammar, `--sandbox` enforcement, the cwd finding, `--remote-control`, the blocking-MCP proof |

**`agy` moved from 1.1.6 to 1.1.7 partway through that same day** — the superseded binary is still on
disk as `agy.exe.<timestamp>.old` — and nothing noticed until the probe suite recorded a version. The
`#472` rows have **not** been re-verified against 1.1.7. They are not thereby wrong; they are
unattributed to the CLI that is installed, which is a different and quieter problem. `pixi run
vendor-check` going green means only "no CLI has moved since the last probe" — never "every row here
is verified."

This exists because the M25 design assumed capabilities in several places, and design that assumes
wrongly is worse than design that knows its limits. Four assumptions were **wrong** and are corrected
below. Two of the four were this document's own rows, which is the honest reason the probes are now
[a program](../tools/Aer.VendorProbe/) rather than a habit: `pixi run vendor-probe` regenerates the
findings, and `pixi run vendor-check` (free — it only reads `--version`) tells you when a vendor has
moved out from under them.

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

| | `claude` 2.1.219 | `agy` 1.1.7 |
|---|---|---|
| Headless flag | `-p` / `--print` | `-p` / `--print` |
| Effort | `--effort low\|medium\|high\|xhigh\|max` | `--effort low\|medium\|high` |
| Extra directories | `--add-dir` | `--add-dir` (repeatable) |
| MCP | `mcp` subcommand, `--mcp-config`, `--strict-mcp-config` | **config file only** — `~/.gemini/config/mcp_config.json` |
| Permission modes | `--permission-mode acceptEdits\|auto\|bypassPermissions\|manual\|dontAsk\|plan` | `--mode accept-edits\|plan` |
| Per-call tool grant | `--allowedTools` / `--disallowedTools` | **none** — grants must be persisted to settings |
| `--permission-prompt-tool` | **honoured** — consults a named MCP tool (undocumented) | **rejected**: `flags provided but not defined` |
| Bypass permissions | `--permission-mode bypassPermissions`, `--dangerously-skip-permissions` | **`--dangerously-skip-permissions`** |
| Sandbox | referenced in help only | **`--sandbox`, and it enforces** |
| Resume | `--resume`, `-c` / `--continue` | `-c` / `--continue`, `--conversation <id>` |
| Structured output | `--output-format stream-json --verbose` | **a local gRPC/HTTP server** — reachable, service surface not yet enumerated |
| Running-session registry | **`claude agents --json`** | not found on: `--help`, subcommand list |
| Permission policy engine | **`claude auto-mode`** — allow / soft_deny / hard_deny | not found on: `--help`, subcommand list |
| Model enumeration | not found on: `--help`, subcommand list | **`agy models`** |
| Plan usage & reset | **`/usage` (and `/cost`) — works headlessly, see below** | **none** — `/usage` is not a real command |
| Per-turn cost | **`total_cost_usd` in every `stream-json` result** | none |
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

## `--permission-prompt-tool` — honoured by `claude`, and 0015 assumed it absent

**Corrected 2026-07-24.** This document recorded the flag as **absent on both vendors**, established
from `--help` alone. [0015](decisions/0015-three-kinds-of-needs-you.md) inverted its entire mechanism
to a blocking MCP tool on that premise. The premise does not hold for `claude`.

The flag is genuinely undocumented in `claude --help`, so the original reading was not careless — it
was *incomplete*, in the same way the `/usage` row was. What settles it is a **control flag**: pass
something that certainly does not exist, and see whether the CLI discriminates at all.

| invocation | exit | output |
|---|---|---|
| `claude --definitely-not-a-real-flag-xyz -p hi` | **1** | `error: unknown option '--definitely-not-a-real-flag-xyz'` |
| `claude --permission-prompt-tool noop -p hi` | **0** | the turn runs normally |
| `agy --definitely-not-a-real-flag-xyz -p hi` | **2** | `flags provided but not defined` |
| `agy --permission-prompt-tool noop -p hi` | **2** | `flags provided but not defined: -permission-prompt-tool` |

`claude` rejects unknown flags and accepts this one; `agy` rejects both, so *its* absence is real and
now rests on something firmer than help text. Without the control row, a zero exit is not evidence —
"accepted" and "silently ignored" are indistinguishable — which is why
[`FlagProbe`](../tools/Aer.VendorProbe/FlagProbe.cs) establishes the baseline before judging any flag.

### It is honoured, not merely parsed

Accepting a flag is not honouring it, and the table above only proves it *parses* — the prompt `hi`
triggers no tool call, so it can never reach a permission decision. The check that discriminates is a
turn that forces one, with a tool name that exists nowhere:

```
claude --permission-prompt-tool aer_probe_no_such_tool -p --output-format stream-json --verbose \
  "Use the Write tool to create a file named x.txt containing BANANA in the current directory."
```

```
Error calling tool (Write): Error: MCP tool aer_probe_no_such_tool
(passed via --permission-prompt-tool) not found. Available MCP tools: …
```

The CLI reached the permission path, looked for the tool **by the name we invented**, and said so.
A name that exists nowhere could not have come from anywhere but the flag, which is what makes this a
measurement rather than an inference. Without the flag, the identical prompt is simply denied and the
call lands in `permission_denials`.

**So `--permission-prompt-tool` routes permission decisions to an MCP tool** — the same mechanism
[0015](decisions/0015-three-kinds-of-needs-you.md) already chose, but as the vendor's designated entry
point, consulted for *every* decision, rather than a tool the model must elect to call. That
difference is not cosmetic: a gate the model chooses to invoke is discipline resting on model
behaviour, which is what Architecture Rule 1 exists to forbid. This one is structural.

0015 is therefore not wrong in its mechanism — MCP consultation is proven on both vendors (below) and
is the only path `agy` has at all. What is wrong is its stated justification, that no vendor offers a
permission callback. Whether the decision changes belongs in the decision, not in this reference.

### The full contract, measured

A stdio MCP server registered via `--mcp-config … --strict-mcp-config` and named as
`--permission-prompt-tool mcp__aerperm__approve` receives the whole call:

```json
{ "name": "approve",
  "arguments": {
    "tool_name": "Write",
    "input": { "file_path": "…\\x.txt", "content": "BANANA\n" },
    "tool_use_id": "toolu_01A6fPfyebEFF5judLv4Ug4S" },
  "_meta": { "claudecode/toolUseId": "toolu_01A6…", "progressToken": 2 } }
```

Both replies were exercised in a clean environment where `claude -p` otherwise denies an ungranted
`Write`:

| reply | observed |
|---|---|
| `{"behavior":"allow","updatedInput":{…}}` | call proceeded — **file written** |
| `{"behavior":"deny","message":"…"}` | **file not written**; the message reached the model verbatim, and the call still landed in `permission_denials` with its full `tool_input` |

Two properties worth designing around: **`updatedInput` lets an answer modify the call**, not merely
permit it; and **the denial message is acted on by the worker** — on deny it reported stopping *"rather
than routing around it with a shell write."* A denial can therefore carry a reason, which is what
[0022](decisions/0022-permission-ladder-and-denial-is-an-answer.md) means by "denial is an answer".

`agy` has no equivalent flag, so on that vendor the same MCP server must be reached by the model
electing to call it — a weaker guarantee, and one the surface should not hide.

## The subcommand surface — three `claude` subcommands nobody had opened

**Probed 2026-07-24.** Every capability above was probed on `--help` and, where relevant, the slash
commands. **`<subcommand> --help` is a third surface**, and three of `claude`'s subcommands turned out
to hold capabilities the M25 design was building from scratch.

### `claude agents --json` — a live registry of running sessions

Machine-readable, explicitly *"for scripting; does not require a TTY"*. Observed:

```json
[
  { "id": "6567d8cf", "cwd": "…\\source\\repos\\aer", "kind": "background",
    "startedAt": 1784902257007, "sessionId": "…", "name": "Reevaluate user experience from ground up",
    "state": "blocked" },
  { "pid": 18272, "cwd": "…\\source\\repos\\aer", "kind": "interactive",
    "startedAt": 1784925162327, "sessionId": "…", "name": "…", "status": "busy" }
]
```

Every field the room list needs: identity, working directory, a background/interactive distinction, a
start time, a human-readable name the vendor generated, and **a state**. Note `"state": "blocked"` —
the vendor already models *waiting on a human* as a first-class state, which is the distinction
0020's state machine draws and #462's queued-message problem lives inside.

`claude agents` also accepts `--permission-mode`, `--effort`, `--model`, `--mcp-config`, `--add-dir`
and `--settings` as **defaults for dispatched sessions**, plus `--allow-dangerously-skip-permissions`
("make bypass available without defaulting to it") — which is precisely
[0028](decisions/0028-no-permissive-control-is-the-default.md)'s shape, already expressible.

This is the fan-out surface. It deserves a real feasibility read before AER builds its own.

### `claude auto-mode` — a three-rung permission classifier that already exists

`claude auto-mode defaults` prints ~62 KB of JSON with exactly four keys:

| key | rules | what it is |
|---|---|---|
| `allow` | 17 | carve-outs that are explicitly *not* violations |
| `soft_deny` | 65 | blocked, but overridable — each names what it must cite |
| `hard_deny` | 1 | Data Exfiltration. Never overridable |
| `environment` | 20 | questions about the operator's context that condition the rest |

The rules are **natural-language**, evaluated by a classifier, and user-overridable via an `autoMode`
section in the settings file (`auto-mode config` shows the effective merge, `auto-mode reset` removes
the override, `auto-mode critique` gives AI feedback on custom rules).

Two consequences worth sitting with:

- **A soft/hard denial ladder is not something AER has to invent.** 0022 designed one independently,
  and the vendor's `soft_deny` / `hard_deny` split is the same distinction — a denial you can answer
  versus one that is the end of the conversation.
- **This is content classification driving a permission decision**, which is exactly what
  Architecture Rule 1 forbids *Flow* from doing. It does not forbid Flow from **delegating** it to the
  worker's own classifier. That is a genuinely better answer than reimplementing it, and it is only
  available because the surface was looked at.

### `claude project purge`

Deletes all Claude Code state for a project — transcripts, tasks, file history, config entry. Relevant
to whatever AER does when a room is deleted, and to any claim we make about what "removing a room"
actually removes on disk.

## `agy models` — effort and model are not orthogonal

`agy models` enumerates what the CLI will actually accept:

```
gemini-3.6-flash-high     gemini-3.6-flash-medium   gemini-3.6-flash-low
gemini-3.5-flash-high     gemini-3.5-flash-medium   gemini-3.5-flash-low
gemini-3.1-pro-high       gemini-3.1-pro-low
claude-sonnet-4-6         claude-opus-4-6-thinking  gpt-oss-120b-medium
```

Two things the design assumed otherwise:

- **Effort is baked into the model name**, *and* `--effort low|medium|high` exists as a separate flag.
  Two overlapping controls, and the interaction between them is unprobed.
- **The grid has holes.** `gemini-3.1-pro` has `high` and `low` but **no `medium`**. A UI offering
  model × effort as a matrix would offer combinations the CLI rejects. This sharpens
  [0023](decisions/0023-effort-and-models-are-named-by-behaviour.md): naming by behaviour is right,
  but the available set is per-model, so it has to be *enumerated*, not assumed.
- `agy` serves **Anthropic and OpenAI models too**, not only Gemini. "The Gemini worker" is the wrong
  mental model for it.

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

## Usage, cost and quota — the asymmetry that matters most

**Probed 2026-07-24.** An earlier pass concluded *"neither vendor exposes remaining quota or a reset
time."* **That was wrong, and it was wrong for a methodological reason worth recording: it probed the
CLI's `--help` and subcommand list, not the in-session slash commands.** Those are different surfaces,
and the answer lives in the second one.

> **Probe both surfaces.** A capability absent from `--help` may still exist as a slash command, and
> a slash command may still work under `-p`. Checking one and concluding about the other is how the
> first pass produced a confident wrong answer about the single number this product runs on.
>
> **On Windows, do not probe slash commands through Git Bash.** MSYS path conversion rewrites a
> leading `/usage` into `C:/Program Files/Git/usage` *before it reaches the CLI*, and the model then
> answers about that path — which reads exactly like "the command does not exist." Use PowerShell, or
> `MSYS_NO_PATHCONV=1`.

### `claude` — everything needed, headlessly

`claude -p "/usage"` and `claude -p "/cost"` both return the same live report:

```
Current session: 21% used · resets Jul 25, 12:09am (America/New_York)
Current week (all models): 67% used · resets Jul 27, 5:59am (America/New_York)
Current week (Fable): 0% used
Last 24h · 1811 requests · 21 sessions
  88% of your usage came from subagent-heavy sessions
  82% of your usage was at >150k context
```

So all four things [0026](decisions/0026-running-out-of-plan-is-a-state-not-a-failure.md) and `#479`
needed are available: **percent consumed, a real reset instant, a per-model breakdown, and request
counts** — plus behavioural attribution (*what* is spending the plan), which nothing in the design
anticipated and which is more actionable than the percentage alone.

The corpus's mockup number — *"Claude plan · 72% of this week's limit"* — was **not** a designed
placeholder. It is the shape of a number the CLI already reports.

**One caveat the surface must carry**, in the CLI's own words: *"Approximate, based on local sessions
on this machine — does not include other devices or claude.ai."* The figure is **machine-local**, so
AER must not present it as an account-wide truth.

Separately, every `stream-json` result event carries `input_tokens`, `output_tokens`,
`cache_creation_input_tokens`, `cache_read_input_tokens`, `model`, `service_tier` and
**`total_cost_usd`** — the API-equivalent cost, computed by the CLI. No price table to maintain and no
drift to chase. Observed on a trivial *"reply with ok"* turn: **$0.2463**, of which essentially all was
24,619 cache-creation tokens. Cache writes dominate, which is worth knowing before designing a
per-turn cost display.

### `agy` — nothing

`agy -p "/usage"` is **not a built-in command**. Headless, it tried to run a shell tool and was denied;
re-run sandboxed with permissions bypassed, the model simply answered *conversationally* — active
model, config path, telemetry state. No tokens, no percentage, no reset. `agy -p "/cost"` likewise
produced prose claiming the status bar shows it, which is an interactive-only surface and, being the
model talking rather than the CLI, is not evidence of anything.

Not found on the surfaces checked: `--help`, the subcommand list, the slash surface, `--log-file`
(13 KB, **zero** token keys), and `~/.gemini/antigravity-cli/cache/conversation_metadata.json`
(`NumSteps`, `Title`, `UpdatedAt` — no usage of any kind).

**One surface remains unchecked, and it is the promising one.** See below: `agy` runs a local RPC
server, and no usage query has been put to it. Read this row as "not found yet", not "does not exist".

### `agy` has a local RPC server — the structured surface we recorded as absent

**Corrected 2026-07-24.** "Structured output: not found" was established on `--help` and on trying
`--output-format`. It missed that **every `agy` run starts a local server and prints its ports**:

```
Starting language server process with pid 29564
Language server version: 1.1.7
Language server listening on random port at 50871 for HTTPS (gRPC)
Language server listening on random port at 50872 for HTTP
```

Confirmed live: an HTTP request to that port during a run returns a real Go HTTP response
(`404`, `Vary: Origin`, `X-Content-Type-Options: nosniff`) rather than a connection refusal. The
server is there, it is reachable, and the port is discoverable from `--log-file`.

**Not yet enumerated:** the service and method names. A guessed Connect RPC path 404s, and scanning
the 166 MB binary for `*.Service` paths found none, so the service surface is likely in the spawned
language-server process rather than the CLI binary. This is a partial, not an absence.

That matters more than a convenience feature. A typed local RPC stream would let Flow route on
**structured events instead of parsed stdout** — satisfying Architecture Rule 1 *structurally* rather
than by discipline. Combined with the public Python SDK (`pip install google-antigravity`, which
exposes streamed strongly-typed `ToolCall` events), there are now two independent signals pointing at
the same integration path, and neither has been probed. **This is the highest-value open probe on
`agy`**, and it is the one that would decide whether the usage/cost asymmetry above is permanent or
merely unmeasured.

### The design consequence

**Do not fake parity.** Exact plan usage, reset times and per-turn cost for one vendor; nothing found
yet for the other. That asymmetry has to be visible in the interface rather than smoothed into a
single half-trustworthy number — the same rule
[0023](decisions/0023-effort-and-models-are-named-by-behaviour.md) applies to effort levels, where a
collapse is disclosed rather than silently faked.

**But design the surface for "not measured", not for "does not exist".** Two unprobed `agy` surfaces
could still produce real numbers — the local RPC server and the public Python SDK — so a UI that
hard-codes *"agy has no usage"* would bake in a claim that two open probes might overturn next week.
The honest element is one that can say *"no usage data from this worker"* and later carry a figure
without being redesigned.

## `agy` permission grammar

Rules live in `~/.gemini/antigravity-cli/settings.json` under `permissions.allow` / `.deny`. This is
the **only** settings path — there is no project-local override file. Prefixes (from vendor docs):
`read_file`, `write_file`, `read_url`, `execute_url`, `command`, `unsandboxed`, `mcp`.

MCP rules take `mcp(server/tool)`, `mcp(server/*)` or `mcp(*)`. Observed: `mcp(aerhuman)` — the bare
server name — **does not match**; `mcp(aerhuman/*)` does.

**Command rules are matched literally, against the whole command line.** This is the single most
consequential finding for the permission surface, and it contradicts the vendor's own docs (which
describe `command(git)` as covering "standard git commands"). Four runs against the identical command
`node --version`, each changing only the rule:

| rule | result |
|---|---|
| `command(node)` | **denied** — a bare binary name does not cover its invocations |
| `command(node .*)` | **denied** — so the match is not a regex, despite the docs' `command(npm run (build\|lint\|test))` example |
| `command(node --version)` | **granted, ran** |
| `command(node C:/…/escape.js)` (exact, separate run) | **granted** |

**Consequence: AER cannot pre-authorise a *family* of commands on `agy`, only enumerate exact command
lines.** A ceiling like "this room may run git, but not push" is not expressible as an allow-rule.
Where a family-shaped grant is needed, the enforceable instrument is `--sandbox` plus targeted
`unsandboxed(…)` escapes, or the MCP consultation path — not `permissions.allow`. Design the
permission surface accordingly rather than assuming prefix semantics.

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

**`agy -p` ignores the working directory.** It runs the agent under its own install directory, not the
shell's cwd. Observed twice, including in **the case the adapter will actually hit** — launched from
`aer-flow`, which *is* listed in the settings' `trustedWorkspaces`, the emitted command still carried
`"Cwd":"C:\\Users\\pbree\\.gemini\\antigravity-cli"`. From an untrusted temp directory it used
`…\antigravity-cli\scratch` and, unable to find a file sitting in the launch directory, began a
recursive search of the entire home folder. Workspace trust does not change the behaviour.
**Bind the room's directory explicitly with `--add-dir`** — never rely on cwd. Any adapter that
assumes cwd is silently pointing the worker somewhere else.

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

**It cannot be enabled non-interactively.** `agy --remote-control -p …` reports *"No valid
authentication found"* and starts a **fresh OAuth login**, requesting scopes (`cloud-platform`,
`cclog`, `experimentsandconfigs`, `aicode`, userinfo) beyond what an ordinary authenticated session
holds — then fails with *"You are not logged into Antigravity"* without writing any state. An ordinary
`agy -p` still authenticates normally afterwards; the attempt does not disturb the existing token. So
remote control sits behind a **separate, interactive consent**, and **AER cannot turn it on for the
operator** — worth knowing before designing any flow that assumes it.

**Enabled by the owner 2026-07-24**, which revealed where the state lands and what identity it uses:

```jsonc
// ~/.gemini/config/config.json  — the shared config, not the CLI's settings.json
"remoteControlEnabled": true,
"remoteControlHostname": "compy-2-plasma-mars"
```

The auto-generated hostname is **speakable, not a UUID or an IP**. That is worth copying: AER's own
pairing identity is a token today, and #326 shows what a machine-shaped identity costs a user when it
goes wrong (a raw `401`). A device you can *name out loud* is easier to recognise, to confirm over the
phone, and to tell apart from another machine on the same account.

Vendor forum discussion as of the probe date describes no *official* mobile
remote control, while several third-party clients exist; one reportedly speaks **Connect RPC to the
Antigravity language server** directly. That, plus the public Python SDK
(`pip install google-antigravity`) exposing streamed strongly-typed `ToolCall` events, thought deltas
and read-only-by-default capabilities, suggests a structured local integration path that would beat
parsing stdout — and would satisfy CLAUDE.md Architecture Rule 1 structurally, since Flow would never
need to read conversation text to route. Worth a feasibility spike before the adapter shape is fixed.

## Keeping this current

Both CLIs self-update, so every row here has a shelf life. The suite splits along cost:

| | what it does | cost | where it runs |
|---|---|---|---|
| `pixi run vendor-probe` | drives the live CLIs, regenerates the findings | **real subscription usage**, a few minutes | a human, on a machine with both vendors authenticated |
| `pixi run vendor-check` | compares installed `--version` against the recorded one | **nothing** — no session, no tokens | the ordinary dev loop, and `pixi run test` |

The free check is the trigger for the paid one. `pixi run vendor-probe` writes
`docs/vendor-probe.lock.json` recording the versions its findings were established against;
`VendorProbeStalenessTests` compares that against what is installed and fails the moment a CLI moves.

**This deliberately does not run in CI**, and not only because the probe spends usage. No runner has
an authenticated `claude` or `agy` on PATH, so a CI job would find both vendors absent and go green
forever — a pass meaning only "the vendors were never here". That green would be worse than no check,
because it looks like coverage. The check therefore *skips* where it cannot know, and says so.

Related: `#472` (the first probe), `#504` (the probe suite), `#445` (the permission-request mechanism),
[0004](decisions/0004-permission-scopes.md), [0015](decisions/0015-three-kinds-of-needs-you.md).
