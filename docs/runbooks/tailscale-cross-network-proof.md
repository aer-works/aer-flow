# Runbook: Cross-network proof via Tailscale (M21 Phase 4)

M21 Phase 4's completion gate (#240): prove that the pair → approve flow Phase 2 already proved
over LAN also works "from anywhere" — phone and desktop on genuinely different networks, joined
only by a personal Tailscale tailnet, zero new code. This de-risks Phase 5 (zero-config tailnet
embedding) by confirming the daemon's remote-control API has no LAN-only assumption baked into it.

**This is always a human-run step, not something an agent session can close on its own** — same
category as the live-vendor smoke gates documented in `CLAUDE.md`'s "Live-vendor smoke tests"
section: it needs a real phone, a real second network, and a real Tailscale account, none of which
exist inside an agent session.

## Don't scan the QR — use manual entry

`Aer.Ui`'s Enable Remote Access view generates its QR from `LanAddress.TryGetPrimary()`
(`src/Aer.Ui.Core/LanAddress.cs`), which **deliberately filters out Tailscale's virtual adapter** (it's
built to avoid picking a VPN/virtual adapter over the real LAN one). The QR will always encode the
desktop's LAN IP, never its tailnet `100.x` address — scanning it from a phone on a different
network will behave exactly like a garbled scan: it hangs and never connects.

Use `Aer.Mobile`'s existing manual host + code fields instead (the same fields Phase 2 built before
Phase 3 added QR scanning), with the desktop's **Tailscale** IP, not its LAN IP.

## Prerequisites

- Tailscale installed and logged into the same personal tailnet on both the desktop and the phone
  (the official Tailscale app on both — this phase proves the manual-install path; zero-config is
  Phase 5).
- The desktop's tailnet IPv4 address: `tailscale ip -4`, or the Tailscale app's device list.
- The phone on cellular data (or any network other than the desktop's LAN) — the point is to prove
  connectivity with no shared physical network, not just a second Wi-Fi on the same router.
- The usual repo prerequisites to run the desktop side (`.NET 10` SDK, submodule initialized — see
  the root `README.md`), plus `Aer.Mobile` already built and installed on the phone per
  `docs/runbooks/` and `IMPLEMENTATION_PLAN.md`'s Phase 2 setup notes.

## Running it

1. Start the daemon in remote mode (or use `Aer.Ui`'s Enable Remote Access toggle, which does the
   same shutdown-and-respawn under the hood): `aer daemon --remote`.
2. Open `Aer.Ui`'s Enable Remote Access view to generate a pairing code. Ignore the QR.
3. On the phone, open `Aer.Mobile`'s pairing screen and use the manual host/code fields: host =
   the desktop's Tailscale IP from the Prerequisites step, code = the 6-digit code from step 2.
4. Confirm pairing succeeds.
5. From the desktop, run a workflow that reaches a `PausePoint` (any fixture or real workflow with
   a declared pause is fine — this only exercises the transport, not a new engine path).
6. Approve (or Reject) the paused step from the phone's decision inbox.

## What "green" means

- Pairing completes from the manually-entered tailnet IP + code, with the phone on a different
  network than the desktop.
- The paused step appears in the phone's inbox over the WS connection.
- Approving from the phone resolves the step and the desktop's own projection reaches `Terminal`,
  the same second-broadcast confirmation Phase 2's real-hardware run already verified over LAN.

## If it fails

- **Pairing itself fails or times out**: check that the phone can actually reach the desktop's
  tailnet IP at all — `ping`/`tailscale ping` from the phone's Tailscale app, independent of
  `Aer.Mobile`. If the tailnet link itself isn't up, this isn't an `aer` bug.
- **Pairing succeeds but no paused step appears**: this is the same WS-auth path Phase 2's runbook
  work already proved over LAN (`app.UseWebSockets()` ordering, `DirectoryPath` on the WS payload) —
  a failure here on Tailscale but not LAN would point at something tailnet-specific (MTU, port
  reachability) rather than the auth/projection code itself.
- **Everything else**: this phase changes no code, so any failure here is either a tailnet
  connectivity problem (out of this repo's control) or a real regression in Phase 2/3's already-proven
  LAN path — re-run the LAN case from `IMPLEMENTATION_PLAN.md`'s Phase 2 verification notes to
  isolate which side broke.

## Recording a green run

M21 Phase 4 is complete once this has been run successfully at least once. Record the date, the
device/network setup, and the Tailscale client versions used in `IMPLEMENTATION_PLAN.md`'s Phase 4
entry — this file only documents *how* to run it, not a rolling log of every run.

**Recorded green run:** 2026-07-19, desktop Tailscale client 1.98.9 (Windows), phone a Pixel 10
Pro on Tailscale for Android, cellular data (no shared LAN with the desktop). Manual host entry
(`100.101.139.26:5000`) + pairing code succeeded on the first attempt. A live `Aer.Daemon --remote`
instance (not through `Aer.Ui`'s toggle) ran a two-step Claude-only workflow so the proof didn't
depend on a second vendor's CLI being authenticated on this host; `review` declared a `PausePoint`
and reached `Paused` (its own execution incidentally also failed transiently — unrelated to this
phase, see below — which is itself a valid pause-worthy outcome per the engine's contract). The
paused card appeared in the phone's inbox with no manual refresh, live over the WS connection
established at pairing. Reject from the phone resolved it; the daemon's own projection (queried via
`/api/tasks/open`) confirmed `Terminal`/`Rejected` and a decision record, matching the second-WS-
broadcast confirmation Phase 2 already proved over LAN.

Windows Firewall had a pre-existing `aer.daemon.exe` allow rule for the Private/Public profiles, and
the Tailscale adapter is itself categorized Private by Windows — no firewall changes were needed for
this specific machine, but a fresh machine may need to grant `Aer.Daemon.exe` through the firewall on
first `--remote` run (Windows' authenticated app prompt handles this in the normal `Aer.Ui` toggle
flow).

**Unrelated flake noticed, not a Phase 4 finding**: the first attempt's `draft` step (Claude,
`--allowedTools Write`) wrote a fully correct output file but the CLI process still exited 1,
failing the step outright under `MaxAttempts: 1`. Retried with `MaxAttempts: 3` and it succeeded on
the next attempt. Likely a transient/host-contention issue with invoking a second `claude` CLI
process from a session that is itself a running `claude` process — not investigated further since
it's orthogonal to this phase's actual proof (transport over Tailscale, not adapter reliability).
