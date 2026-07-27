# 0039 — A dialogue turn resumes the vendor's own session; it does not resend the transcript

Status: accepted
Date: 2026-07-26

## Context

`#581` and `#582` are two symptoms of the same design choice in `Aer.Workers.Dialogue`, confirmed by
reading the real, current (`#580`-rebased) source rather than the stale copy this session was
carrying:

`DialogueRunner.BuildPrompt` threads **the entire transcript so far** into every turn's prompt —
by design, per the class's own doc comment (*"Context threading is the full transcript so far, not a
sliding window"*). Two consequences follow directly, both already measured this session:

- **`#582`**: each turn's prompt is strictly longer than the last (measured 13KB→58KB over 10 turns
  in the live UX dialogue this session ran) — a fresh, stateless vendor CLI process per turn, so
  nothing is cached and the growth is quadratic in total bytes transmitted, not linear.
- **`#579`/`#580`**: a long enough transcript overflows Windows' ~32,767-character argv limit. `#580`
  fixed the crash by adding a second placeholder, `DialogueParticipant.PromptFilePlaceholder`
  (`{PROMPT_FILE}`), and `DialogueParticipantPresets.For` now instructs **both** vendors' participants
  to read their prompt from that file (*"Read instructions from `{PROMPT_FILE}` and follow them."*).
- **`#581`**: that instruction is what breaks live. Measured directly against `agy`: told to read a
  file, the model reliably invokes an unrequested `Bash` tool call, soft-denied headless with zero
  output (`tool_confirmation_manager.go:183`), under both `--mode accept-edits` and `--mode plan`.
  **This is not `agy`-specific.** `docs/vendor-capabilities.md`'s own corrected reading of
  `--allowedTools` — *"a pre-approval and routing mechanism, not a security boundary... `Bash` alone
  defeats withheld writes, withheld reads"* — means `claude`'s preset (`--allowedTools Write,Read`,
  `Bash` not pre-approved) sits in the same failure class: an unrequested `Bash` call hits the
  standard permission prompt and is auto-denied the same way, headless. Neither vendor's dialogue
  preset survives the file-read instruction without `--dangerously-skip-permissions` or equivalent.

**The fix `#580` shipped treats the symptom.** It solved the argv-length crash but did so by asking
the model to perform a file read it doesn't need to be asked to perform at all — the actual defect is
upstream: nothing about a dialogue turn requires resending history the vendor CLI could instead
remember itself. `Aer.Adapters`' own `ClaudeWorkerAdapter`/`GeminiWorkerAdapter` already solve exactly
this for the Conversation/Pipeline shapes, via each vendor's native session continuation
(`--resume`/`--session-id` for `claude`, `--conversation` for `agy`) — `Aer.Workers.Dialogue` is the
one shape that reimplements its own stateless turn loop instead of using it.

## Decision

**A dialogue turn sends only its own increment — this turn's preamble plus the immediately preceding
reply — and resumes the vendor's own session for everything before that.** Concretely:

- `DialogueParticipant`/`IVendorTurnClient` gain a session-id concept, threaded the same way
  `WorkerInvocation.SessionId`/`ResumeSession` already work in `Aer.Adapters`: the first turn a
  participant takes starts a fresh vendor session; every later turn resumes it (`--resume <id>` /
  `--conversation <id>`), never re-threading prior turns as text.
- `DialogueRunner.BuildPrompt` no longer concatenates `priorTurns` — a turn's prompt is bounded by
  construction (one preamble plus one prior reply), not by the exchange's length.
- **This removes the reason `{PROMPT_FILE}` exists.** With no full-transcript resend, no turn's
  payload should ever approach the argv limit `#579` hit — direct argv text (`{PROMPT}`) is sufficient
  again, and with it, the file-read instruction that broke `#581` is no longer needed at all. The
  `{PROMPT_FILE}` mechanism should be removed once this ships, not kept as an unused fallback shape —
  a mechanism nothing exercises is exactly the kind of thing this project has had to cut back out
  before.
- **Keep the defensive guard `#581` separately asked for.** Even bounded per-turn payloads deserve a
  loud, fast-failing check in `ProcessVendorTurnClient.SendTurnAsync` if a substituted argument exceeds
  a safe threshold well under the platform limit (e.g. 16,000 characters) — not because this design
  should need it, but because a silent crash from an unbounded value is the exact failure class
  [[feedback-rebase-and-defense-in-depth]] already named: a structural fix that happens to avoid the
  common case still wants a guard for the case it didn't anticipate.

## Rests on

| fact | how we know | if false |
|---|---|---|
| `DialogueRunner.BuildPrompt` threads the entire transcript into every turn | **measured** — read directly, plus 13KB→58KB over ten turns in a live run | there is no growth to fix and #582 is not a real defect |
| A long enough transcript overflows Windows' argv limit | **measured** — #579, and #598 reproduced `os error 206` on the aer-core path; `CoreDispatcher` now refuses above 32,000 characters before spawning | the `{PROMPT_FILE}` workaround was unnecessary, and #581's breakage was self-inflicted rather than forced |
| Told to read a file, `agy` reliably invokes an unrequested `Bash` call, soft-denied headless with zero output | **measured** — #581, against the real CLI under both `--mode accept-edits` and `--mode plan` | `{PROMPT_FILE}` works, and continuation becomes an optimisation rather than the fix for a live breakage |
| Both vendors can resume a native session and retain prior context (claude `--resume`, gemini `--conversation`) | **assumed — and this is the row that would kill the record.** The flags are recorded in `docs/vendor-capabilities.md`, but **no `vendor-verify` check proves a resumed session retains the earlier turns** | the whole mechanism is unavailable, full-history resend is the only option, and #582's quadratic growth becomes structural rather than a bug |

## Consequences

**Easier.** One redesign closes both issues: `#582`'s cost/latency problem (no more quadratic
transmission — a fresh session per participant, resumed turn-to-turn, exactly as cheap as
Conversation/Pipeline already are) and `#581`'s live failure (no more instructing either vendor to
read a file, so no permission escalation is needed on either preset). Nothing here is new engine
work — `Aer.Adapters` already proves the mechanism; `Aer.Workers.Dialogue` adopts it.

**Harder.** `DialogueRunner`'s round-robin loop needs a session-id per participant (one each, since
the two sides are separate vendor sessions, not one shared thread) — a real code change, not a config
tweak, and `ProcessVendorTurnClientEndToEndTests`/`DialogueWorkerConfigParserTests` (added by `#580`)
need reworking alongside it since the config shape they test is exactly what this retires.

**Obliges us to.** File this as its own issue (following the `#585` pattern), scoped to
`Aer.Workers.Dialogue`/`ProcessVendorTurnClient`/`DialogueRunner`; close `#581`/`#582` as this record's
duplicates rather than independently, since fixing one without the other leaves a half-migrated
worker; add the defensive argv-length guard in the same change; verify against the real `agy`/`claude`
CLIs live (the same kind of run that found `#581` in the first place — the unit suite's stub scripts
have no LLM-driven tool-use behavior to catch this class of regression) before calling the fix done.

Relates: [0035](0035-aer-yield-is-a-structured-mcp-tool-not-a-sentinel.md) and
[0036](0036-shape-is-rendering-not-a-second-state-machine.md) (the Dialogue shape's other prerequisites,
also blocking M27), `docs/vendor-doc-audit.md` and `docs/vendor-capabilities.md` (the `--allowedTools`
finding this record generalizes from `claude`'s adapter dispatch to its dialogue preset).
