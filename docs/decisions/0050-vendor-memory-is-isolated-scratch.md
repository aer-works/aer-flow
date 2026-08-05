# 0050 — Vendor memory is isolated scratch; room memory is the only durable layer

Status: accepted
Date: 2026-08-05

Builds on [0016](0016-memory-is-room-owned.md) ("Memory belongs to the room, not the worker"), [0044](0044-memory-belongs-to-the-room-and-changes-only-by-decision.md) ("Memory belongs to the room and changes only by decision"), and [0009](0009-session-lifecycle-and-retention.md)/[0011](0011-token-based-context-management.md) (cross-vendor worker memory continuity).

## Context

Vendor CLI tools automatically maintain state and auto-memory channels outside AER's room boundary. Investigation on issue #442 identified two distinct hazards across vendors:

1. **Claude Code hazard**: Auto-memory keys on the sanitized working directory (`cwd`) under the active configuration root, accumulating project memory and instructions across independent runs in the user's host profile unless redirected.
2. **Google Antigravity (agy) hazard**: Maintains a host-global conversation store under `~/.gemini/antigravity-cli/` with live cross-conversation retrieval, allowing fresh conversations to search and retrieve text from past worker transcripts across all tasks.

These unconsented persistence channels violate 0016 and 0044, which establish that room memory is the sole durable memory layer in AER.

## Decision

**Vendor auto-memory is treated as isolated, throwaway scratch space. Room memory remains the only durable layer.**

1. **Claude isolation mechanism (shared AER config root, default OFF):**
   - When the operator sets `AER_CLAUDE_CONFIG_ROOT=<abs path>` in the AER process environment, AER injects `CLAUDE_CONFIG_DIR=<path>` into every spawned Claude child environment (`ClaudeWorkerAdapter.Resolve` and `BuildGate`).
   - `cwd` is unchanged to preserve `CLAUDE.md` discovery, chat shell semantics, and relative path resolution.
   - **Default OFF**: Because a fresh `CLAUDE_CONFIG_DIR` requires a one-time interactive operator login (`claude auth login`), the knob defaults to OFF (`AER_CLAUDE_CONFIG_ROOT` unset) so today's behavior is preserved until the operator configures and authenticates the shared root.

2. **agy isolation mechanism (`HOME`/`USERPROFILE` redirect, non-shell scoped):**
   - For agy worker bindings whose grant does **NOT** include shell command execution (`grant.RunShellCommands == false`), AER injects `HOME` and `USERPROFILE` pointing at an AER-owned state directory.
   - **Batch dispatch lifetime**: State directory is created per-execution under `AER_OUTPUT_DIR` (`%AER_OUTPUT_DIR%\.gemini_home`), ensuring a fresh isolated brain per execution cleaned up automatically with execution artifacts.
   - **Daemon session lifetime**: State directory is per-session (`<session_dir>\.gemini_home`), keeping conversation state stable across turns of the same interactive session.
   - **Shell-granted scoping exception**: Shell-granted workers (`RunShellCommands == true`) are deliberately **NOT** redirected in this decision, because a redirected profile hides the operator's `.gitconfig` from worker `git commit`. This open remainder stays recorded on #1019.
   - **Dialogue participant scoping exception**: agy participants spawned by the dialogue worker are also **NOT** redirected. The redirect travels the dispatch path only — the batch value is a placeholder `CoreDispatcher` expands at dispatch time, and the dialogue worker spawns vendor CLIs from a gated config with neither `AER_OUTPUT_DIR` nor an expansion step, so it would receive the token literally. This remainder also stays recorded on #1019.

3. **Continuity successor**:
   - Cross-vendor worker continuity and durable context remain governed by 0009, 0011, and 0044 via AER's room memory, index files, and structured MCP proposal tools — never by vendor-native auto-memory.

## Rests on

| fact | how we know | if false |
|---|---|---|
| Redirecting `CLAUDE_CONFIG_DIR` isolates state but breaks subscription auth until a one-time operator login | **measured** — `durability.config-dir-redirect-breaks-auth` (issue #527) | `CLAUDE_CONFIG_DIR` could be enabled default-ON without requiring manual operator authentication |
| Redirecting `HOME`/`USERPROFILE` isolates agy state store without breaking authentication | **measured** — `durability.agy-home-redirect-isolates-state` (issue #442 comments) | agy redirects would fail auth or fail to isolate state |

## Consequences

**Easier.** Vendor auto-memory cannot leak sensitive codebase facts or cross-conversation transcripts across worker invocations; room memory is strictly enforced as the sole durable layer.

**Harder.** The operator must perform a one-time `claude auth login` under `AER_CLAUDE_CONFIG_ROOT` before enabling Claude config isolation; shell-granted agy workers remain open on #442 to preserve git commit identity.

**Obliges us to** maintain the `AER_CLAUDE_CONFIG_ROOT` opt-in knob, document the one-time login runbook in `docs/runbooks/claude-shared-config-root.md`, and enforce non-shell agy HOME/USERPROFILE redirects.
