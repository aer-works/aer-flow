# Aer.Journeys.Tests

The .NET half of the journey-test harness (#313) — the **desktop** legs (Avalonia view-mounted
headless) and **engine** legs (adapter / Flow-level, no UI) of the product journeys in
[`spec/journeys.md`](../../spec/journeys.md). The phone legs live in `src/Aer.Mobile/test/journeys`.

**This project is deliberately outside `AerFlow.slnx`** — the same exclusion the live-vendor
`smoke-*` tests use — so `pixi run test` never runs it. Journey tests are *red-specs*: a promise the
product does not keep yet fails its journey test on purpose, so they can't sit in the default,
must-stay-green suite. They compile on demand under `pixi run journey-*`.

| File | What |
|---|---|
| `Journeys.cs` | The journey registry — the code-side source of truth `ReconcileTests` checks against `spec/journeys.md`. |
| `ReconcileTests.cs` | The reconcile gate's structural half (the #314 seed): registry ↔ spec never drift. Green. |
| `J6_DeniedToolEnforcementTests.cs` | Engine leg, safety: a withheld tool must be enforced at the dispatch boundary. **Red** (decision 0004 / #331). |
| `J8_DesktopFirstRunTests.cs` | Desktop leg: an empty first-run Home offers real first actions, not a blank wall. **Green** (#190). |
| `PendingJourneys.cs` | Driveable-but-not-yet-written .NET legs, declared as skips so the suite enumerates them. |

Run them, read their result, and add one: [`docs/runbooks/journeys.md`](../../docs/runbooks/journeys.md).
