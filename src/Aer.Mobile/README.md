# Aer.Mobile

The Flutter/Android **remote client** for AER Flow — the phone half of the daemon's remote-control
story (M21–M24). It pairs with a running `Aer.Daemon`, then drives real work from anywhere:

- **Pairing** — QR-code scan or manual host/code entry, over zero-config Tailscale (embedded `tsnet`
  via Go CGO — no separate Tailscale app install; see `docs/decisions-of-record.md`, M21).
- **Decision inbox** — Approve / Reject / Cancel a paused step, with the artifact to review shown
  before deciding, and artifact-referenced send-back (Supersede) with no host filesystem access.
- **Live task & chat streaming** — task projection and in-turn progress pushed over WebSockets
  (`InboxScreen`, `ChatScreen`), filtered per client so two devices can view different work.
- **Start work** — the built-in template picker and Unified Task Creation (Chat / Codebase session /
  Two-Vendor Dialogue) front doors (M22, M24).

## Building

Built as a debug/sideload APK, not through `dotnet`. Use the repo's mobile tasks (`pixi run
mobile-build` / `mobile-test`, or `scripts/mobile-build.sh`) rather than `flutter` directly — they
shim the environment the `tailscale` package's native cgo build hook needs. Journey (widget) tests
carry the `journey` tag and are excluded from the default `flutter test` run; see
`tests/Aer.Journeys.Tests/README.md` and `docs/runbooks/journeys.md`.
