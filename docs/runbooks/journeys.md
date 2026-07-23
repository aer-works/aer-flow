# Runbook — journey tests

The journey tests are the executable half of [`spec/journeys.md`](../../spec/journeys.md): each
product promise, driven against the *real* surface a person uses, so a green view-model can't cover
for a broken screen. This runbook is how you run them, read their result, and add one.

Journey tests were introduced in #313 (framework + the driveable-now legs). #314 turns the
reconcile check below into a required CI gate.

## The one rule that explains everything

**A journey test is a red-spec.** A promise the product does not keep yet *fails its journey test on
purpose*. So `pixi run journey-desktop` and `pixi run journey-mobile` exit non-zero today — that red
is the honest status of the product, not a broken build. When a capability lands, its journey test
goes green; a journey passes only when **every** leg passes.

That is why journey tests are kept out of the default suites — `Aer.Journeys.Tests` lives outside
`AerFlow.slnx` (so `pixi run test` ignores it) and the phone journey tests carry the `journey` tag
excluded from `flutter test` (see `src/Aer.Mobile/dart_test.yaml`). Same reasoning, and same
mechanism, as the live-vendor `smoke-*` tasks.

## The runners

A journey crosses surfaces; each surface is a *leg*, driven by whichever runner fits it. A journey
can be **partially covered** — one leg driven now, another pending or human-attested.

| Runner | What it drives | In CI? | Where |
|---|---|---|---|
| **desktop** | Avalonia **view-mounted headless** — the real shipped views, rendered offscreen | can be (run on demand today) | `tests/Aer.Journeys.Tests` |
| **phone** | Flutter **widget test** — the real Dart widget tree, no emulator, no tsnet sidecar | can be (run on demand today) | `src/Aer.Mobile/test/journeys` |
| **engine** | Adapter / Flow-level .NET test, no UI | can be (run on demand today) | `tests/Aer.Journeys.Tests` |
| **attest** | A real cross-device / live-vendor walk — a dated **human** sign-off | never | this runbook |

The two UI runners render the actual surface in process, so they catch view-composition defects
(output under the wrong tab, a control that never appears) that API-level and view-model tests miss
— which is the whole reason journeys exist. Only the genuinely cross-process / cross-device / live
legs fall to **attest**.

## Running them

```sh
pixi run journey-desktop     # desktop + engine legs (.NET); red today (J6 safety spec fails)
pixi run journey-mobile      # phone legs (Flutter widget); red today (J8 phone empty-state fails)
pixi run journey-all         # both
pixi run journey-reconcile   # the reconcile gate — GREEN; meant to pass (see below)
```

Filter to one journey on the .NET side with `dotnet test tests/Aer.Journeys.Tests --filter
Journey=J6`. Every journey test carries `[Trait("Journey", "Jn")]`.

**Mobile prerequisites.** `flutter test` builds the `tailscale` package's Go **native asset**
first, which needs Go **and a C compiler** (cgo) on the host, and Flutter's hook runner scrubs the
environment before invoking Go — so plain `pixi run journey-mobile` fails to *build* on a host
without those, before any test runs. Run it through the same `go` PATH shim `scripts/mobile-build.sh`
uses (#303), on a machine with a full mobile toolchain. `flutter analyze` needs neither and is the
quick correctness check. CI's Go+cgo environment builds it fine.

## The reconcile gate (`journey-reconcile`)

The one journey task that is **meant to pass**. It asserts the prose `spec/journeys.md` and the
code-side `Journeys` registry (`tests/Aer.Journeys.Tests/Journeys.cs`) never drift: the same set of
journeys, each with a byte-identical `**Status:**` line. Edit a journey's status in one place and
not the other and this fails.

This is the **structural** half. #314 adds the **behavioural** half — comparing each declared status
against the journey tests' *actual* pass/fail, so a journey that starts passing but still reads
"Fails" (or the reverse) also breaks the build. That is what makes the statuses in `spec/journeys.md`
un-rottable: they cannot lie about a test that exists.

## Human-attested legs (permanent action items)

These cannot run in process and stand on a dated sign-off recorded against the journey — the same
footing as the live-vendor `smoke-*` gates (`docs/runbooks/live-*-smoke.md`). They are **not**
something an agent session closes:

- **J1 / J5** — desk-started work appearing on, and driven from, a real paired **phone** (the
  broadcast + inbox-scope path across two devices).
- **J4** — a fresh physical phone **pairing** on a real LAN.
- **J7** — a real device **losing the daemon** (network drop / port change) and recovering.
- **J2 / J6 (end-to-end)** — the *live-vendor* quality of a review, and a live worker actually being
  **refused** a denied tool. J6's engine leg (the grant must be enforced at the dispatch boundary,
  not merely displayed) *is* automated and red today; the live refusal on top of it is the attest
  leg.

## Adding a journey (or a leg)

1. Write the promise in `spec/journeys.md` — a person's outcome across surfaces, with a `**Status:**`
   line and a *Passes when* bar.
2. Add it to the `Journeys` registry with the **same** id, title, and status. The reconcile gate
   enforces this pairing; that coupling is the point.
3. Write the test on the leg's runner, tagged `[Trait("Journey", "Jn")]` (.NET) or under
   `test/journeys` with `@Tags(['journey'])` (Flutter). Assert the *Passes when* bar against the real
   surface. It is red until the capability lands — that is correct.
4. For an attest leg, add it to the list above with its walkthrough instead of a test.
