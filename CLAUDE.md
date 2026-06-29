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
├── .github/workflows/
│   ├── ci.yml              lint + fmt + test on win + linux
│   └── release-please.yml  versioning and changelog
└── pixi.toml               task runner and toolchain manager
```

---

## Running tasks

Always use `pixi run <task>`. Never invoke `dotnet` directly in CI or development.

| Task | Command |
|---|---|
| `build` | `dotnet build` |
| `test` | `dotnet test` |
| `lint` | `dotnet build -warnaserror` |
| `fmt` | `dotnet format` (fix) |
| `fmt-check` | `dotnet format --verify-no-changes` (CI) |

Pixi manages the .NET SDK (`dotnet-sdk` from `conda-forge`) — no separate system-wide .NET install needed.

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
