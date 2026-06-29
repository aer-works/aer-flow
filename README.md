# AER Flow

AER Flow is the workflow execution engine layer for the AER (Agent Execution Runtime) ecosystem.

Built in .NET, it reads structured workflow definitions, dispatches them to Workers (via `aer-core`), and bridges outputs back to the engine.

## Documentation
- [Agent Instructions](CLAUDE.md) - Architectural rules and development workflows for AI agents.
- [Behavioral Specs](spec/) - The source of truth for engine routing and adapter behaviors.

## Quickstart
```bash
# Install the Pixi environment (includes dotnet-sdk)
pixi install

# Run tests
pixi run test

# Format code
pixi run fmt
```
