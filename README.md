# AER Flow

AER Flow is the workflow execution engine layer for the AER (Agent Execution Runtime) ecosystem.

Built in .NET, it reads structured workflow definitions, dispatches them to Workers (via `aer-core`), and bridges outputs back to the engine.

## Documentation
- [The plan](docs/plan.md) - The living, gated plan: the bar, the decisions in force, and the work by phase.
- [Milestone history & decisions of record](docs/decisions-of-record.md) - What each completed milestone shipped and the durable decisions it left behind.
- [Agent Instructions](CLAUDE.md) - Architectural rules and development workflows for AI agents.
- [Behavioral Specs](spec/) - The source of truth for engine routing and adapter behaviors.
- [Walkthroughs](docs/walkthroughs/) - Guided, end-to-end usage of the shipped stack, starting with your first real workflow.
- [Runbooks](docs/runbooks/) - Manual, key-gated operational procedures not covered by CI.

## Prerequisites

- **[pixi](https://pixi.sh)** — task runner.
- **.NET 10 SDK** — install separately (not managed by pixi), same as aer-core:
  - Windows: `winget install Microsoft.DotNet.SDK.10`
  - macOS: `brew install dotnet-sdk` or the official installer
  - Linux: follow [Microsoft's install guide](https://learn.microsoft.com/en-us/dotnet/core/install/linux)

## Quickstart
```bash
# Install the Pixi environment
pixi install

# Run tests
pixi run test

# Format code
pixi run fmt
```

## Installing `aer`

`aer` is distributed as a self-built, unpublished `dotnet tool` — there is no public NuGet feed
(a single-developer project doesn't need one; see `spec/AER Overview.md` §6). Build a local nupkg
and install from it directly:

```bash
# Build the nupkg (embeds the native aer_core library for every OS CI already built one for)
pixi run pack

# Install it as a global tool from that local folder
dotnet tool install --global --add-source bin/pack aer

# Run it
aer run <workflow-file> --bindings <bindings-file>

# Remove it
dotnet tool uninstall --global aer
```

`pixi run verify-pack` runs this exact install → run → uninstall round trip end to end against a
trivial fixture (no live vendor call) — it's the same check CI runs unattended on every push.
