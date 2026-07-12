# AER Flow — Claude Code Instructions

AER Flow is the workflow execution engine layer for the AER (Agent Execution Runtime) ecosystem. Built in .NET, it reads structured workflow definitions, dispatches them to Workers (via `aer-core`), and bridges outputs back to the engine.

---

## Repo structure

```
aer-flow/
├── src/
│   ├── Aer.Flow/           The core execution engine and routing state machine
│   ├── Aer.Adapters/       Vendor-specific adapters (Claude/Gemini)
│   └── Aer.Cli/            Command-line interface
├── tests/                  Unit and integration tests
├── spec/                   Behavioral specs (source of truth)
│   ├── aer-flow-behavioral-spec-v1.0.md
│   └── aer-flow-ui-behavioral-spec-v0.7.md
├── external/
│   └── aer-core/           git submodule — aer-core's M5 .NET binding, P/Invoked by the Core Dispatcher
├── .github/workflows/
│   ├── ci.yml              lint + fmt + test on win + linux
│   └── release-please.yml  versioning and changelog
└── pixi.toml               task runner and toolchain manager
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

## Architecture Rules

1. **Flow carries discipline, Workers carry intelligence**: The Flow engine must *never* parse conversation content, inspect prompt text, or attempt to understand LLM outputs to make routing decisions. Routing is exclusively defined by the structured workflow config and explicit tool returns from the Workers.
2. **Adapter Isolation**: Vendor-specific quirks (e.g., Anthropic's block format vs Gemini's part format) MUST be isolated inside `Aer.Adapters`. The `Aer.Flow` core layer only understands a single, unified canonical message protocol.
3. **P/Invoke Layer**: Any interaction with `aer-core` for process execution must go through strict P/Invoke wrappers that match the M4 ABI (`AerTask`, `AerCancelHandle`, `AerEvent`).

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
