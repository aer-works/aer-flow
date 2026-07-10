# AER Flow

AER Flow is the workflow execution engine layer for the AER (Agent Execution Runtime) ecosystem.

Built in .NET, it reads structured workflow definitions, dispatches them to Workers (via `aer-core`), and bridges outputs back to the engine.

## Documentation
- [Agent Instructions](CLAUDE.md) - Architectural rules and development workflows for AI agents.
- [Behavioral Specs](spec/) - The source of truth for engine routing and adapter behaviors.

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
