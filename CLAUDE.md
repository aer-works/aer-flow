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
