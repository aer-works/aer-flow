# Vendor coverage register — what we have read, what we have verified, what we have not

**Purpose: mark every gap explicitly**, so that "we didn't check" is never mistaken for "it isn't
there". That mistake produced twelve corrections in a single session; this file exists so the next one
is visible before it becomes a decision.

Companion to [`vendor-doc-audit.md`](vendor-doc-audit.md) (the findings) and
[`vendor-capabilities.md`](vendor-capabilities.md) (the reference). Started 2026-07-24 against
`claude` 2.1.220 and `agy` 1.1.7.

## Status legend

| mark | meaning |
|---|---|
| **R** | read |
| **V** | verified by a run on this host |
| **·** | **not read — a gap, not an absence** |
| **X** | cannot be established from an agent session here (reason given) |

---

## A. `claude` — documentation coverage

Index: `https://code.claude.com/docs/llms.txt` (~170 pages). **9 of ~170 read.**

### Read

| | page | what we took from it |
|---|---|---|
| R | `cli-reference` | full flag/subcommand surface; `--bg`, `--max-budget-usd`, `--json-schema`, remote control |
| R | `sandboxing` | OS-enforced sandbox; **not on native Windows** |
| R | `permissions` | fetched, **59 KB persisted to a file that was never read** — counts as unread below |
| R | `workflows` | `agent()`/`pipeline()`, 16 concurrent / 1000 per run, no mid-run input |
| R | `channels` | events pushed into a live session; **permission relay** |
| R | `agent-teams` | shared task list with **dependencies**, file locking, mailboxes |
| R | `agent-sdk/permissions` | **the six-step evaluation order** |
| R | `agent-sdk/user-input` | `canUseTool`, `AskUserQuestion`, `updatedPermissions` |
| R | `agent-sdk/hooks` | `defer` ends the query; full hook event list; precedence |

### Not read — grouped by how much design rests on them

**Tier 1 — load-bearing, unread:**

`·` `settings` · `permissions` (re-read properly) · `hooks` (full reference — matcher patterns, every
event schema) · `hooks-guide` · `permission-modes` · `auto-mode-config` · `mcp` (the
`requiresUserInteraction` annotation) · `managed-mcp` · `sessions` · `agent-view` · `agents` ·
`sub-agents` · `remote-control` · `headless` · `costs` · `monitoring-usage` · `env-vars` ·
`tools-reference` · `errors` · `model-config` · `context-window` · `checkpointing` ·
`sandbox-environments` · `security` · `server-managed-settings`

**Tier 2 — likely relevant:**

`·` `agent-sdk/overview` · `agent-sdk/sessions` · `agent-sdk/session-storage` ·
`agent-sdk/cost-tracking` · `agent-sdk/structured-outputs` · `agent-sdk/streaming-output` ·
`agent-sdk/streaming-vs-single-mode` · `agent-sdk/custom-tools` · `agent-sdk/subagents` ·
`agent-sdk/todo-tracking` · `agent-sdk/file-checkpointing` · `agent-sdk/observability` ·
`agent-sdk/typescript` · `agent-sdk/python` · `agent-sdk/mcp` · `agent-sdk/agent-loop` ·
`agent-sdk/secure-deployment` · `agent-sdk/tool-search` · `agent-sdk/plugins` ·
`agent-sdk/slash-commands` · `agent-sdk/skills` · `agent-sdk/claude-code-features` ·
`goal` · `routines` · `scheduled-tasks` · `worktrees` · `deep-links` · `artifacts` ·
`channels-reference` · `claude-directory` · `commands` · `interactive-mode` · `memory` ·
`output-styles` · `skills` · `statusline` · `plugins` · `plugins-reference` · `prompt-caching` ·
`fast-mode` · `feature-availability` · `how-claude-code-works` · `glossary` · `data-usage`

**Tier 3 — probably not relevant to AER, listed so the list is complete:**

`·` `accessibility` · `admin-setup` · `advisor` · `amazon-bedrock` · `analytics` · `authentication` ·
`best-practices` · `champion-kit` · `changelog` · `chrome` · `claude-apps-gateway*` (5 pages) ·
`claude-code-on-the-web` · `claude-platform-on-aws` · `claude-security` · `code-review` ·
`common-workflows` · `communications-kit` · `computer-use` · `corporate-launcher` ·
`debug-your-config` · `desktop*` (6 pages) · `devcontainer` · `discover-plugins` ·
`features-overview` · `fullscreen` · `gateways` · `github-actions` · `github-enterprise-server` ·
`gitlab-ci-cd` · `google-vertex-ai` · `jetbrains` · `keybindings` · `large-codebases` ·
`legal-and-compliance` · `llm-gateway*` (4 pages) · `microsoft-foundry` · `mobile` ·
`network-config` · `overview` · `platforms` · `plugin-dependencies` · `plugin-hints` ·
`plugin-marketplaces` · `plugin-relevance` · `prompt-library` · `quickstart` · `security-guidance` ·
`setup` · `slack` · `terminal-config` · `third-party-integrations` · `troubleshoot-install` ·
`troubleshooting` · `ultraplan` · `ultrareview` · `voice-dictation` · `vs-code` · `web-quickstart` ·
`whats-new/*` (18 pages) · `zero-data-retention`

---

## B. `agy` — documentation coverage

Index: `https://antigravity.google/docs/...`. **7 of ~60 read.** The `agy` side is audited far more
shallowly than `claude`, which is itself a risk: **our knowledge asymmetry is now larger than the
products' asymmetry**, and that biases every design toward claude's model.

### Read

| | page | what we took from it |
|---|---|---|
| R | `cli/overview` | nav structure |
| R | `cli/reference` | slash commands, keybindings, `settings.json` keys |
| R | `cli/permissions` | `action(target)`, **three lists incl. `ask`**, `Deny > Ask > Allow`, regex claim |
| R | `cli/sandbox` | `enableTerminalSandbox`; **AppContainer on Windows** |
| R | `cli/commands/usage` | `/usage`, `/quota` — TUI only |

### Not read — Tier 1, load-bearing

`R` **`/docs/hooks`** — **read 2026-07-24.** `agy` documents `PreToolUse` with
`allow`/`deny`/`ask`/`force_ask`, five events, `hooks.json` in `.agents/` or `~/.gemini/config/`.
**The gate design may be symmetric after all.** CLI support unverified — a guessed schema did not
fire; see the audit for the four candidate reasons. **Still needed: the real `hooks.json` schema.**
`R` **`/docs/sdk/overview`** — **read 2026-07-24.** `pip install google-antigravity`. Per-turn and
cumulative token usage, streamed structured events, Pydantic-typed results, `deny()`/`allow()`/
`ask_user()`, headless. **Answers all three of #508's open questions.**
`·` `cli/settings` — full settings reference · `cli/modes` — execution modes ·
`cli/subagents` · `cli/projects` · `cli/credits` · `cli/conversations` · `cli/artifacts` ·
`cli/using` · `cli/features` · `docs/permissions` (product-level) · `docs/agent-settings` ·
`docs/mcp` · `docs/subagents` · `docs/sidecars` · `docs/hooks`

### Not read — Tier 2

`·` `cli/install` · `cli/getting-started` · `cli/tutorial` · `cli/prompting` · `cli/plugins` ·
`cli/statusline` · `cli/title` · `cli/gcli-migration` · `cli/best-practices` · `cli/troubleshooting` ·
`cli/commands/{agents,codesearch,credits,diff,permissions,resume,statusline,title}` ·
`docs/{models,projects,settings,skills,rules-workflows,plugins,artifacts,implementation-plan}` ·
`docs/{plans,enterprise,faq}`

### Not read — IDE surface (~18 pages)

`·` `docs/ide/*` — not obviously relevant to a CLI worker, listed for completeness. One exception
worth a look: `ide/allowlist-denylist`, which may document the same permission grammar from the other
side.

---

## C. Claims we hold, and their evidence class

### Verified by a run on this host

| claim | where |
|---|---|
| `--permission-prompt-tool` is accepted **and honoured**; full request/response contract | #509, #512 |
| `--permission-mode auto` **silently bypasses** it | #514 |
| `PreToolUse` hook fires under `auto` **and** `bypassPermissions` | #519 |
| `defer` ends the query (`terminal_reason: tool_deferred`); `--resume` completes the work | #520 |
| **`--bare` disables hooks even when passed via `--settings`** | #521 |
| `--bg` sessions appear in `claude agents --json`; states `working`/`idle`/`blocked`/`stopped` | #516 |
| `claude -p "/usage"` reports percent + reset instants; `total_cost_usd` per turn | #472 |
| `--allowedTools` patterns enforce; `Bash(git *)` minus `Bash(git push*)` works | #515 |
| Both vendors fail closed headless | #472 |
| Blocking MCP tool holds a turn open on both vendors | #472 |
| `agy --sandbox` enforces (file write + network blocked) | #472 |
| `agy -p` ignores cwd | #472 |

### Documented but **not verified**

`·` `--max-budget-usd` enforcement · `--json-schema` · `--tools` restriction · `--fork-session` ·
`--session-id` · `--include-partial-messages` · `--forward-subagent-text` · `--replay-user-messages` ·
`--input-format stream-json` queueing behaviour (#462) · `claude attach/logs/stop/rm/respawn` ·
`claude daemon stop --keep-workers` reconnect · `PermissionDenied` hook · `Notification` hook
(`permission_prompt`/`idle_prompt`) · `Elicitation` hooks · `ask` rules forcing a prompt in
`bypassPermissions` · `requiresUserInteraction` allow→deny conversion · 30 s `MCP_TIMEOUT` ·
`updatedPermissions` / `localSettings` persistence · agent-teams task dependencies · workflows
`agent()`/`pipeline()` · channels permission relay · `agy` `/usage` TUI · `agy` `ask` list ·
`agy` implicit read-on-write · `agy` Windows path normalisation

### Contradicted or unresolved

| | |
|---|---|
| ~~`agy` `command()`: regex or literal?~~ | **RESOLVED 2026-07-24 — literal.** Re-run on 1.1.7 with the operator's authorisation; both discriminating rules denied, including the docs' own alternation form. **The documentation is wrong** — the only such case in this audit. |
| **Does `defer` replay the identical `tool_use_id`?** | verified the session resumes and work completes; **not** verified the same call is replayed. Decides whether we can promise "the exact call you approved ran". |

### X — cannot be established from an agent session on this host

| | why |
|---|---|
| `claude` sandbox (any of it) | **not supported on native Windows**; this host is Windows 11 |
| ~~`agy` `command()` re-test~~ | **done 2026-07-24** — run on explicit authorisation, byte-exact backup, restore after every case, SHA-256 verified unchanged |
| Channels | research preview; needs a plugin install and org enablement |
| Workflows | plan-gated; needs `/config` opt-in on Pro |
| Agent teams | needs `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1` (env-only, safe — but untested) |
| Live smoke gates | permanently a human action item (CLAUDE.md) |

---

## D. Gaps that are not about the vendors

These are the ones most likely to be missed, because the audit has been pointed outward.

1. **Every finding is Windows-only.** `vendor-capabilities.md` was established entirely on this host.
   AER Flow ships cross-platform. The sandbox correction is the proof that this matters: a
   platform-scoped observation was generalised into a product claim. **Nothing has been verified on
   macOS or Linux.**

2. **The code has never been audited against any of this.** Twelve corrections landed in docs and
   decisions; `src/` was written against the same wrong premises. #521 is the first defect found by
   looking, and it was found in the first file checked — it should not be assumed to be the only one.
   Unaudited: `ClaudeWorkerAdapter`, `GeminiWorkerAdapter`, the dialogue worker presets, and anything
   that encodes a permission or capability assumption.

3. **The decisions have not been swept.** There are 28 records. This session touched 0004, 0015, 0023
   and 0026, each reactively. **Nobody has read 0001–0028 against the corrected vendor reality**, so
   the count of affected decisions is unknown rather than four.

4. **Vendor drift during a run is unhandled.** Both CLIs shipped a new version *inside this session*
   (`agy` 1.1.6→1.1.7, `claude` 2.1.219→2.1.220). What AER does when the binary changes under a
   running room is not designed, and `vendor-check` only detects drift between probe runs.

5. **`agy`'s knowledge deficit is a design bias.** 5 pages read versus 9 much deeper ones for claude,
   and `agy`'s hooks page is unread entirely. Any symmetry claim about the two vendors is currently
   unsupported.

---

## E. Order of work

Inventory first (this file), then verification, then re-derivation:

1. **Read Tier 1 on both vendors** — `agy`'s `/docs/hooks` and `/docs/sdk/overview` first, because
   they decide whether the gate design is symmetric, then `claude`'s `settings`, `hooks`,
   `permission-modes`, `mcp`, `costs`.
2. **Verify the "documented but not verified" list** above, in the order that decisions depend on it.
3. **Audit `src/` against the corrected reality** (#521 found the first defect; sweep the rest).
4. **Sweep 0001–0028** and record which rest on corrected premises.
5. **Resolve the two contradictions**, one of which needs an owner decision about safe testing.
6. **Establish cross-platform coverage**, or state plainly that every claim is Windows-scoped.
