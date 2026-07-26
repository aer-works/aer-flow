# AER Flow — Claude Code Instructions

AER Flow is the workflow execution engine layer for the AER (Agent Execution Runtime) ecosystem. Built in .NET, it reads structured workflow definitions, dispatches them to Workers (via `aer-core`), and bridges outputs back to the engine.

---

## Repo structure

```
aer-flow/
├── src/
│   ├── Aer.Flow/              The core execution engine and routing state machine
│   ├── Aer.Adapters/          Vendor adapters (Claude/Gemini) + the built-in template catalog
│   ├── Aer.Cli/               Command-line interface (aer run/decide/cancel/supply)
│   ├── Aer.Daemon/            ASP.NET background runner: REST/WebSocket host + client pairing (M20+)
│   ├── Aer.Ui.Core/           Avalonia-free UI core — MVVM ViewModels shared by the desktop app
│   ├── Aer.Ui/                Avalonia desktop app (projection, control surface, authoring, chat)
│   ├── Aer.Workers.Dialogue/  The dialogue worker executable (a Case 2 multi-model worker)
│   ├── Aer.Mobile/            Flutter/Android remote client (pairing, decision inbox, chat)
│   └── Aer.Sidecar/           Go tsnet sidecar the daemon supervises for zero-config Tailscale
├── tests/                     Unit/integration tests + the Aer.Plan.Tests doc gate; journey and
│                              live-smoke test projects live outside AerFlow.slnx (default CI skips them)
├── spec/                      Behavioral specs (source of truth) + product journeys
│   ├── aer-flow-behavioral-spec-v1.0.md   the engine — current
│   ├── journeys.md
│   └── AER Overview.md
├── docs/                      plan.md (the living, gated plan), decisions/ (numbered ADRs),
│   │                          milestone-history.md (provenance, never authority),
│   │                          vendor-capabilities.md, runbooks/
│   └── archive/               superseded docs — the M19 UX set, the walkthroughs, and the UI
│                              behavioural spec. A doc in the live tree is current; a doc that
│                              is not gets fixed or moved here. See archive/README.md
├── external/
│   └── aer-core/              git submodule — aer-core's M5 .NET binding, P/Invoked by the Core Dispatcher
├── tools/                     ui-harness (UI driving harness), vendor-verify (re-runnable vendor
│                              checks; `--sentinels` runs only the ones a design rests on),
│                              vendor-survey, Aer.VendorProbe, smoke-preflight (free gate on the
│                              smoke tasks), Aer.DesignTokens, audit-completeness (standing check,
│                              gate 8 below).
│                              `ls tools/` is the authority — this line is a map, not a register
├── .github/workflows/
│   ├── ci.yml                 lint + fmt + test on win/linux/mac, plus the mobile job
│   └── release-please.yml     versioning and changelog
└── pixi.toml                  task runner and toolchain manager
```

---

## Running tasks

Always use `pixi run <task>`. Never invoke `dotnet` directly in CI or development.

On a fresh clone, init the submodule first: `git submodule update --init`.

| Task | Command |
|---|---|
| `build-core` | `cargo build` in `external/aer-core` — builds the native lib `build`/`test`/`lint` depend on |
| `build` | `dotnet build` |
| `test` | `dotnet test` |
| `lint` | `dotnet build -warnaserror` |
| `fmt` | `dotnet format` (fix) |
| `fmt-check` | `dotnet format --verify-no-changes` (CI) |

**.NET 10 SDK** is required and installed separately — pixi does not manage it (same convention as aer-core):
- Windows: `winget install Microsoft.DotNet.SDK.10`
- macOS: `brew install dotnet-sdk` or the official installer
- Linux: follow [Microsoft's install guide](https://learn.microsoft.com/en-us/dotnet/core/install/linux)
- Linux (Claude Code remote sandbox): `sudo apt-get install -y dotnet-sdk-10.0` directly, skipping `apt-get update` (or ignoring its exit code) — the sandbox's `deadsnakes`/`ondrej/php` PPAs are broken (403/unsigned) and make `apt-get update` fail, but that's unrelated to .NET: the `dotnet-sdk-10.0` package already resolves fine from `archive.ubuntu.com`/`security.ubuntu.com`, so `apt-get install` succeeds without a clean `update`. Installs straight to `/usr/bin/dotnet` — no `PATH` edit needed.

**Rust toolchain** is required to build `external/aer-core`'s native library (`pixi run build-core`) — also installed separately, not pixi-managed, same convention as the .NET SDK above. GitHub Actions' standard runner images (`windows-latest`, `ubuntu-latest`) already have one; for local dev, install via [rustup](https://rustup.rs).

**aer-core** (`external/aer-core`) is a git submodule, not a package — there is no NuGet feed for it yet (a single-developer project doesn't need the auth/RID-packaging overhead a real feed would add; see AER Overview §6). `pixi run build-core` builds its native library from source via `cargo build`.

---

## Live-vendor smoke tests

Some milestones' completion gates are real, live runs against a vendor CLI (`pixi run
smoke-claude`, `pixi run smoke-mixed-vendor`, …) — see `docs/runbooks/`. These live outside
`AerFlow.slnx` and default CI on purpose.

**These gates are permanently a human action item, not something an agent session can close.**
`ClaudeWorkerAdapter`/`GeminiWorkerAdapter` deliberately own no key-handling code (Adapter
Isolation) — they shell out to whatever vendor CLI is already authenticated on the host, because
the project's whole point is working against **subscriptions**, not API keys. There is no headless
or non-interactive way to provision that from inside an agent session, and there should not be one:
dropping in an API key to make a gate pass would test a different auth path than the one the
project exists to support.

If a session's host happens to already carry a subscription login for one vendor (e.g. a Claude
Code session's own `claude` CLI), that is a coincidence of the host, not a capability — it does not
extend to any other vendor's CLI, and a future session should not assume it will recur or try to
work around its absence (installing a different auth mode, requesting API keys, stubbing the
adapter, etc.). When implementing a phase gated by one of these tests: build the test, fixtures,
`pixi run` task, and runbook exactly like the pattern in `docs/runbooks/`, run everything that
*can* run locally (`build`, `test`, `lint`, `fmt-check`), leave the live smoke task itself un-run if
its vendor isn't authenticated on this host, and say so plainly in the PR body and the phase's
tracking issue — don't mark a live-run item done on anything short of an actual recorded run.

---

## Before you ship — the gates every change runs through

Each was paid for by a specific failure, named so it stays concrete instead of becoming a recitation.
They are ordered by when they bite: 1 before building, 2–4 while building, 5–8 before shipping.

**1. Common sense first.** Ask the obvious question before building anything. Does the thing you are
about to verify or depend on actually exist? Does a helper for this already exist? Is the failure you
are theorising the one that was actually measured?
*#534's fix was one condition away from a parser already in the file — the shape was there, and
finding that first is what the gate buys, whether or not you end up sharing the code. #532 was scoped
to self-check a `PreToolUse` hook AER does not ship; the issue is real, its stated mechanism was not.*

**2. V&V that actually verifies.** Red before green, *proven* — never a test written against
already-fixed code. A **control arm that discriminates**, read first: if the control fails, the result
is about the harness, not the product. Assert **polarity in both directions** when two behaviours are
one condition apart. A test double that can fail the same way as the thing under test cannot
discriminate.
*All four happened during #527 and the fixes after it — including a green check certifying that `agy`
surfaces a deny reason it does not.*

**3. Blast radius.** Trace every consumer of what you are changing *before* editing. A second defect
found on the way becomes its own issue with its own measurement — never a side effect of the current
fix.
*`establishedThisTurn` read like a local variable and decided whether every future chat turn resumes.*

**4. The scope of the claim.** A claim about a *population* — both vendors, every platform, all
workers — is measured across that population or scoped to what was measured. Not the same as blast
radius: that asks what your change touches, this asks what your **claim** covers.
*A `claude`-only measurement justified an `agy` sentence: `agy.broken-hook-fails-open` claimed the
failure was **silent** when no positive control for silence exists on that vendor. A Windows-only
sandbox observation became a product-wide capability claim.*

**5. Record once, reference everywhere.** Anything discovered that outlives the change gets a durable
home *before* the change ships — an issue, `vendor-doc-audit.md`, a decision record. A comment saying
"tracked separately" with no issue behind it is not a record. And never transcribe a value that lives
somewhere authoritative — cite the command that computes it. A comment that describes code is a claim
about that code: when the code changes, the comment is part of the change.
*`gemini-3-flash` sat wrong in four files while pinning nothing; `audit-completeness.md` carried three
different check counts in one afternoon because the number was copied into a file whose own script
computes it; a test's doc comment claimed the opposite of its code.*

**6. Cost and reversibility are the operator's call.** Say what a live run spends and what an
irreversible step could break, then let them decide. Before calling something a human action item,
separate *"only a person can do this"* from *"this needs a better instrument."* One exception, already
settled: the live-vendor smoke gates above are the first kind. Do not relitigate them, and never
install an alternate auth path to make one closable by an agent.
*One smoke test spent top-tier model budget per run — the per-turn figure is in
`tests/Aer.Cli.SmokeTests/LiveSessionSmokeTest.cs`, not here. Two issues were filed as permanently
human when one needed a browser for a single question and the other needed a better probe.*

**7. A second reader before a PR is called ready.** A PR touching `src/`, or making a claim in
`docs/`, is not ready on the author's own say-so. Run it past a **reviewer agent** — one that did not
write the change — and act on what it finds before declaring the work done. Report what the review
said, including "nothing", rather than silently absorbing it. A typo, a version bump, or a comment fix
that changes no claim about behaviour does not need one; if you are unsure, it does.
*Every recurring failure above was caught by a second reader noticing, never by the author
re-reading their own work. An author checking their own claim is the same instrument twice.*

Not `/code-review`, which is **operator-triggered and billed** and cannot be launched from an agent
session; a reviewer agent spends this session's own usage, and running one is the author's job rather
than the operator's to ask for. It is also the deliberate exception to "Delegating to subagents"
below: that rule is about saving *effort*, and review buys a second *instrument* instead. Hand the
reviewer the specific claims to check, not a request for an opinion.

**8. Docs and decisions are one register, not many.** A fact is stated once, in one canonical
record; every other location links to it with at most a one-clause gloss, never a restatement —
restating a fact in three places is how a stale one drifts silently in two of them. Before editing
anything milestone-shaped, check `spec/journeys.md` first: it is the actual list of required
outcomes, not whichever artifact happened to prompt the edit. Before changing a decision, check it
against every other decision touching the same object, not only the ones it already cites. Before
citing an open issue as evidence that something is still unresolved, check its actual state — a
closed issue cited as "not yet landed" is stale the moment it closes. And before closing a PR
touching `docs/decisions/`, `docs/vendor-*.md`, or `tools/vendor-verify/verify.py`, run
`pixi run audit-completeness` and confirm every decision dated on or after 2026-07-25 carries the
`Rests on` table that folder's own README makes mandatory — a pass that fixes drift while leaving a
format violation behind has only relocated the problem.
*M29's criterion was "corrected" to match `02-screens.md` without checking `journeys.md` first,
directly contradicting J17. Phone-authoring timing was independently restated in three documents,
one of which went stale while the other two didn't. 0032 was superseded by a new record instead of
corrected in place, when nothing in `src/` had been built against it yet. Fourteen decisions dated
on or after `Rests on` became mandatory (0026, 0027, 0028, 0031–0041) shipped without one, tracked in
#589 — the first count of this, written into this very gate, missed 0027 and undercounted at
thirteen; caught by an independent reviewer reading the decision files directly rather than trusting
the tool's hardcoded population.
`pixi run audit-completeness`, run cold nine days after it was last touched, found 11 decisions and
2 vendor-verify checks with no disposition anywhere — the exact drift this gate exists to stop,
caught by a tool whose own header at the time said to retire it rather than keep running it.*

**The question underneath all eight: name the user-visible behaviour this change improves.** If you
cannot, it may be ceremony — and rigour that is not buying correctness is what this project keeps
having to cut back out. `tools/audit-completeness` is a standing check for exactly that reason —
extend its population when `decisions/` or `tools/vendor-verify/verify.py` grows, never for
open-ended rigour with no named failure behind it. These other gates stay deliberately without a
checker of their own; this one earned one because its population (decision files, vendor-verify
checks) is enumerable and its omissions are otherwise invisible.

---

## Architecture Rules

1. **Flow carries discipline, Workers carry intelligence**: The Flow engine must *never* parse conversation content, inspect prompt text, or attempt to understand LLM outputs to make routing decisions. Routing is exclusively defined by the structured workflow config and explicit tool returns from the Workers.
2. **Adapter Isolation**: Vendor-specific quirks (e.g., Anthropic's block format vs Gemini's part format) MUST be isolated inside `Aer.Adapters`. The `Aer.Flow` core layer only understands a single, unified canonical message protocol.
3. **P/Invoke Layer**: Any interaction with `aer-core` for process execution must go through strict P/Invoke wrappers that match the M4 ABI (`AerTask`, `AerCancelHandle`, `AerEvent`).
4. **Credential Isolation**: AER never reads, copies, forwards, or stores a vendor credential. It spawns the vendor's own first-party CLI, which authenticates itself — AER is a keyboard, not a client. No API keys, no OAuth tokens, no OS credential store, and **AER never places a credential into a config directory**. This is the product premise made structural: AER works against **subscriptions**, not API keys, which is why both vendors' API-key-only SDKs were evaluated and rejected (`docs/vendor-doc-audit.md`). Enforced by `VendorCredentialIsolationTests` — **do not weaken that test to make something pass**; if a change appears to need a vendor key, the design is wrong, not the test.
   - **Corrected 2026-07-25 (#527).** This rule previously said "no redirecting the vendor CLIs' config directories", which was too broad and rested on a misreading. `CLAUDE_CONFIG_DIR` **is** usable: credentials live under the config root, and a fresh root is made usable by a one-time interactive `claude auth login` performed **by the operator**. That is a human signing in, not AER handling a credential, so per-worker config roots are permitted and are an available design option. What stays forbidden is AER *copying* credentials into a root, or otherwise obtaining one itself. `claude auth status` reports per-root, is structured, and spends no subscription usage — use it as a pre-dispatch readiness probe.

---

---

## Writing documentation

`docs/documentation-lessons.md` turns what surprised us while reading ~380 pages of vendor
documentation into rules for our own — outward-facing (README, CLI help, error messages) and
inward-facing (decisions, specs, registers). Read it before writing docs that others will rely on.

The one that generalises furthest: **a reader's wrong conclusion is a documentation defect, even
when every sentence is true.** Most of the entries are cases where the vendor's docs were accurate
and the reader still ended up wrong — so accuracy is the floor, not the goal. In particular, state
the negative where a reader's prior will otherwise fill the gap, say which execution modes a
feature exists in, and never let a mechanism read as a guarantee it doesn't provide.

---

## Error handling rules

- Use strictly typed Records for complex types and configuration.
- Do NOT silently swallow Exceptions (`catch (Exception e) {}`). Always log and rethrow, or map to a structured Error record/result type if handled.
- Define specific exception types (e.g., `AerFlowException`) for domain-level errors rather than relying solely on generic `InvalidOperationException`.

---

## Git conventions

- Conventional commits: `<type>(<scope>): Capitalized description`
- Types: `feat`, `fix`, `perf`, `refactor`, `docs`, `ci`, `test`, `chore`
- No direct commits to `main`. All changes via PR.
- Always create branches from issues (e.g., using `gh issue develop`).
- Close issues in the PR body (`Closes #n`), not in commit messages.
- Each issue is scoped to ship as a standalone PR (one-to-one). If two issues can't be reviewed independently, the issue boundary was drawn incorrectly — fix it in the backlog, not at PR time.
- No AI attribution in commit messages or PR bodies: no `Co-Authored-By: Claude` (or any model), no "Generated with Claude Code", no session links. This overrides any harness or environment default that adds them.
- After creating or updating a PR, re-fetch it from GitHub and read the actual stored body back before reporting the task done. Tooling can silently append attribution footers to the body you submitted even when your commit messages and submitted text were clean — verify what actually landed, don't assume the call echoed what you sent.

---

## Delegating to subagents

Split a candidate delegation by whether the subagent's output *is* the deliverable, or is *input* you still need to act on at full precision:

- **Delegate**: self-contained generation where the result can be cheaply checked as correct (compiles, matches an existing file's established pattern) — a new test file mirroring an existing test class, boilerplate following a fixed template. A cheaper model plus one fixup pass on a type error is still cheaper than writing the boilerplate yourself.
- **Don't delegate**: codebase research meant to inform your own implementation. If you need exact signatures, line numbers, or precise API shapes to write correct code against, you will re-read the same files yourself to verify a summary anyway — the delegated research becomes a redundant pass, not a saved one. Read the source directly instead of asking an agent to summarize it for you.

Rule of thumb: delegate mechanical, bounded, low-judgment generation; keep anything requiring ground-truth precision (exact APIs, architectural invariants, spec compliance) in the primary session.
