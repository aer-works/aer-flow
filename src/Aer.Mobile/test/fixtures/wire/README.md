# Wire Contract Fixtures

These JSON files are golden contract fixtures generated directly by `Aer.Daemon`'s actual `JsonSerializerOptions` (via `WireFixtureGenerator` in `tests/Aer.Daemon.Tests`). Hand edits to these files are futile because `WireFixtureStalenessTests` in C# enforces byte-level equality against the daemon's serialization output. If daemon models or serializer options change, regenerate these fixtures by running the fixture generator in `tests/Aer.Daemon.Tests` and committing the result.
