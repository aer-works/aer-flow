# Vendor coverage register — what we have read, what we have verified, what we have not

**Purpose: mark every gap explicitly**, so that "we didn't check" is never mistaken for "it isn't
there". That mistake produced twelve corrections in a single session; this file exists so the next one
is visible before it becomes a decision.

Companion to [`vendor-doc-audit.md`](vendor-doc-audit.md) (the findings) and
[`vendor-capabilities.md`](vendor-capabilities.md) (the reference). Started 2026-07-24 against
`claude` 2.1.220 and `agy` 1.1.7.

## How coverage is established (2026-07-25)

Page-at-a-time reading could not cover ~250 pages, and the summarizing fetch that a bulk read
implies is lossy — it is what corrupted the first `defer` reading. **Both vendors publish a
machine-readable index**, so the corpus is mirrored locally and read from source instead:

- **`claude`** — `https://code.claude.com/docs/llms.txt`, **172 pages**, each fetchable as raw `.md`.
- **`agy`** — `https://antigravity.google/llms.txt` (exists; previously unknown) and
  `sitemap.xml`, **77 doc pages** — more than the ~60 this register originally assumed. No `.md`
  variant, but pages are server-rendered, so `<main>` extraction preserves headings, code, tables.

**Breadth and depth are separated rather than traded off.** Every finding that changed a decision
was one sentence inside a large page ("Bare mode skips OAuth and keychain reads"), and those
sentences share a grammar — *skips, only, cannot, must, requires, before v, will become*. Harvesting
that class across the whole corpus gives **100% page coverage at ~1% of the bytes**; depth-reading
then goes only where they cluster on an open question.

`pixi run vendor-survey` (see `tools/vendor-survey/`) rebuilds this: **249 pages / 7.0 MB →
1,475 unique constraint sentences**, tagged against AER's open questions, with page+line provenance,
plus a ledger giving **every page a disposition** so coverage is checkable rather than asserted.

| disposition | pages | meaning |
|---|---|---|
| `PENDING-DEPTH` | 119 | constraints cluster here; depth-read as decisions require |
| `SCAN-ONLY` | 123 | touches an open question but thin; constraints harvested |
| `NO-SIGNAL` | 7 | no open-question vocabulary at all |

All 1,475 constraint sentences have been read across nine topics. **The per-page `·` tables below are
superseded by the ledger** and are kept only for the pages whose *contents* are summarized here.

**A doc page changing is a reason to re-verify, not a reason to believe the new page.** This audit
found four vendor statements to be wrong and two that contradicted each other, so every **V** below
rests on a run, not on a sentence. Those runs are no longer disposable: `pixi run vendor-verify`
(see `tools/vendor-verify/`) re-runs them, each with a control arm and each asserting on a sentinel
file rather than on a model's account of what it did. A `FAIL` there means a behaviour a decision
rests on has moved.

## Status legend

| mark | meaning |
|---|---|
| **R** | read |
| **V** | verified by a run on this host |
| **·** | **not read — a gap, not an absence** |
| **X** | cannot be established from an agent session here (reason given) |

---

## A. `claude` — documentation coverage

Index: `https://code.claude.com/docs/llms.txt` — **172 pages, all mirrored and swept** (`pixi run vendor-survey`). The tier lists below record the ORIGINAL triage and are kept because that triage was itself wrong: **23 of the 53 Tier 3 pages** — dismissed as "probably not relevant" — score `PENDING-DEPTH`, including `authentication` and `changelog`. Trust the ledger, not the tiers.

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

Index: `https://antigravity.google/llms.txt` + `sitemap.xml` — **77 doc pages, all mirrored and swept.** The asymmetry warning below still holds in *volume* (7.0 MB claude vs 310 KB agy), but it is no longer an asymmetry of coverage: both corpora are swept identically.

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

### Documented but **not verified** — the verification backlog

**Updated 2026-07-25 (#527).** The documentation sweep produced roughly 40 new claims while 15 were
verified, so this list **grew** during the audit. That is the expected dynamic and worth stating
plainly: **reading generates claims faster than verification consumes them.** Anything here is a
vendor assertion, and this audit has already found four vendor statements to be wrong.

Verified items move to the "Verified by running it" section of
[`vendor-doc-audit.md`](vendor-doc-audit.md). Nothing is deleted from here without either a run or
a reason it cannot be run.

#### A. Shapes a decision currently in flight

| claim | vendor | why it matters |
|---|---|---|
| `--add-dir` grants file access but loads **no** hooks/settings config | claude | decides **where** AER must place its gate |
| `usage.output_tokens` excludes subagent tokens; `modelUsage` is whole-tree | claude | any cost display under-reports every fan-out (#479) |
| a hook's `"ask"` forces a prompt in `auto` mode | claude | second always-fires path after exit-2 |
| ~~`PermissionRequest` fires **only** in auto mode~~ — **the row was wrong, not just unverified.** The docs say it fires "when a permission dialog appears"; `PermissionDenied` is the auto-classifier event. **Now verified: `PermissionRequest` never fires under `-p`.** (`PermissionDenied` also logged zero, but nothing established that a denial occurred — that one is still open.) | claude | 0018's notify hook has no event to hang on when the CLI is spawned headless |
| explicit `ask` rules force a prompt even in `bypassPermissions` | claude | third always-fires claim |
| `requiresUserInteraction` allow→deny under `--permission-prompt-tool` | claude | the *block* is verified; this conversion is not |
| an API key disables Remote Control, `/schedule`, connectors, notifications | claude | the functional half of Credential Isolation (rule 4) |
| `PostInvocation.terminationBehavior` (`force_continue` / `terminate`) | agy | the untested half of agy's loop control |

#### B. Fan-out — entirely documented-only

**#503 items 4–5 rest on every row here, and not one has been run.**

`·` 20 concurrent subagents (`CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS`) · nested subagents off by
default (`CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH`) · nested teams impossible · a teammate's
background work cannot outlive the lead · per-teammate modes cannot be set at spawn · parent
`bypassPermissions`/`acceptEdits`/`auto` overrides every subagent · workflows `agent()`/`pipeline()`,
16 concurrent / 1,000 per run, no mid-run input · agent-teams task dependencies and file locking ·
the `--bg` lifecycle (`attach`, `logs`, `stop`, `rm`, `respawn`) · `daemon stop --keep-workers`
reconnecting to live workers

#### C. Durability and sessions

`·` two processes cannot write one transcript · `--fork-session` starts without session grants while
`/branch` carries them · credential expiry stalls a long-running background session unrecoverably ·
`CLAUDE_CONFIG_DIR` isolating a supervisor instance · `cleanupPeriodDays` retention ·
`--no-session-persistence` · `--session-id` · `--fork-session`

#### D. `agy` — almost all of it new on 2026-07-25

`·` three permission scopes (Project / Shared / Global) and their merge order · the four
`toolPermission` presets (`request-review`, `proceed-in-sandbox`, `always-proceed`, `strict`) ·
"permission rules govern `run_command` across **all** execution modes" · subagents starting from a
clean slate and being unre-awakenable · AppContainer sandbox on Windows · the daemon↔credential
coupling ("if the background daemon is locked or headless, the CLI cannot read credentials") ·
`/usage` TUI-only · the `ask` list · implicit read-on-write · Windows path normalisation

#### E. Flags never once exercised

`·` `--max-budget-usd` · `--json-schema` · `--tools` · `--include-partial-messages` ·
`--forward-subagent-text` · `--replay-user-messages` · `--input-format stream-json` queueing (#462) ·
`PermissionDenied` hook · `Notification` hook (`permission_prompt` / `idle_prompt`) ·
`Elicitation` hooks · 30 s `MCP_TIMEOUT` · `updatedPermissions` / `localSettings` persistence ·
channels permission relay

#### F. Cannot be established from here — stated so they stop looking pending

| claim | why not |
|---|---|
| claude's OS-enforced sandbox | **does not exist on native Windows**; needs macOS or Linux |
| anything cross-platform | every observation in this repo is Windows-only |
| managed / org settings, connector `ask` policy | requires an organisation |
| Remote Control mobile push, Trusted Devices | requires the mobile app and a paired device |
| `defer`'s single-tool-call limit | three attempts could not make the model batch its calls; **untested, not refuted** |
| the MCP idle window's upper bound | 200 s survived; the ceiling is unknown |

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

Item 1 is **done** (#527): both indexes found, both corpora mirrored, all 1,110 constraint sentences
read. The gate-symmetry question it existed to answer is **settled, negatively** — see
`vendor-doc-audit.md`. Remaining, re-ordered by what the reading changed:

1. **Depth-read where constraints still cluster** — `sub-agents`, `hooks`, `agent-view`, `mcp`, and
   `errors`. Then mine `changelog` (200 constraints, 493 KB): the richest and noisiest source, and
   the one that doubles as a **failure-mode list for AER's own supervisor**.
2. **Verify the "documented but not verified" list** below, in the order decisions depend on it.
3. **Audit `src/` against the corrected reality** (#521 found the first defect; sweep the rest).
   Now includes: does anything sum top-level `usage.output_tokens` and thereby under-report fan-out?
4. **Sweep 0001–0028.** 0015 needs rewriting outright — its gate mechanism, its symmetry assumption,
   and its `defer`-based durability all changed. 0018's notify hook is narrower than assumed.
5. **Establish cross-platform coverage**, or state plainly that every claim is Windows-scoped.
   Newly concrete: claude's hooks run through **Git Bash on Windows** and historically failed
   *silently* there, and Windows is the primary development host.
6. **Re-run `pixi run vendor-survey` and `pixi run vendor-verify` on every vendor version bump.**
   The staleness gate already fires on a version change; the survey re-reads the corpus and reports
   which pages moved, and the verifier re-runs the behaviours the decisions actually rest on. Both
   exist so re-establishing coverage is a command rather than a fresh manual read.
