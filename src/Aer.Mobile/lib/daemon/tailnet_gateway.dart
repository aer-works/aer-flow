import 'dart:async';

import 'package:http/http.dart' as http;
import 'package:path_provider/path_provider.dart';
import 'package:tailscale/tailscale.dart';

/// Embeds `tsnet` directly in Aer.Mobile (M21 Phase 7, issue #246) so a phone reaches the desktop
/// sidecar's tailnet address without a separate Tailscale app install — mirrors the desktop
/// sidecar's own "Sign in to Tailscale" flow (`Aer.Sidecar/main.go`), not a second app.
///
/// `Tailscale.instance` is a process-wide singleton; [init] must run once before [ensureUp] or
/// [client] are touched (called from `main()` before `runApp`).
class TailnetGateway {
  static bool _initialized = false;

  static Future<void> init() async {
    if (_initialized) return;
    final supportDir = await getApplicationSupportDirectory();
    Tailscale.init(stateDir: supportDir.path);
    _initialized = true;
  }

  Stream<NodeState> get onStateChange => Tailscale.instance.onStateChange;

  /// An [http.Client] that routes every request over the embedded tailnet node. Only valid once
  /// [ensureUp] has reported [NodeState.running] — `tsnet`'s own `http.client` getter throws
  /// otherwise.
  http.Client get client => Tailscale.instance.http.client;

  /// Brings the embedded node up. On a device with no persisted tailnet identity yet, the
  /// `tailscale` package's worker refuses to start at all without a non-empty [authKey] (confirmed
  /// against its vendored source, `worker/entrypoint.dart`) — so first enrollment needs a real key,
  /// not the empty-string-then-`needsLogin` flow this package's docs describe for *reconnecting* a
  /// device whose credentials expired. [authKey] is expected to come from the desktop's pairing QR
  /// (a reusable key configured once in `Aer.Daemon`, M21 Phase 7 follow-up) rather than typed by
  /// hand. Once a device has enrolled, later launches reconnect from persisted state with no key
  /// and no browser step.
  Future<TailscaleStatus> ensureUp({String authKey = ''}) {
    return Tailscale.instance.up(hostname: 'aer-mobile', authKey: authKey);
  }

  /// Waits for the node to reach [NodeState.running], for use after [ensureUp] returned
  /// `needsLogin` and the caller opened [TailscaleStatus.authUrl].
  Future<void> waitUntilRunning({
    Duration timeout = const Duration(minutes: 5),
  }) {
    return onStateChange
        .firstWhere((state) => state == NodeState.running)
        .timeout(timeout);
  }

  /// Resolves a `needsLogin` snapshot from [ensureUp] into either a login URL to open, or `null` if
  /// the node reaches [NodeState.running] on its own first.
  ///
  /// `up()` resolves as soon as it observes the first `needsLogin` snapshot, which can land well
  /// before the control plane finishes processing the registration — confirmed live: with a valid
  /// auth key, the device registers successfully (visible immediately in the Tailscale admin
  /// console) while the client still reports `needsLogin` for some seconds afterward, with
  /// [TailscaleStatus.authUrl] never populating at all because no interactive step is actually
  /// needed. Only throws if neither outcome arrives in time, since an authKey-based join can
  /// legitimately still fall through to a real interactive login (e.g. a re-registration the
  /// control plane didn't accept for some other reason).
  Future<Uri?> resolveNeedsLogin({Duration timeout = const Duration(seconds: 45)}) async {
    final deadline = DateTime.now().add(timeout);
    while (true) {
      final status = await Tailscale.instance.status();
      if (status.state == NodeState.running) return null;
      if (status.authUrl != null) return status.authUrl;
      if (DateTime.now().isAfter(deadline)) {
        throw TimeoutException('Tailscale did not finish signing in.');
      }
      await Future.delayed(const Duration(milliseconds: 300));
    }
  }
}

/// Tailscale's CGNAT range (100.64.0.0/10, see `TailscaleStatus.tailscaleIPs`'s doc comment) — the
/// desktop sidecar's pairing host (`SidecarTailnetHost` in `RemoteViewModel.cs`) is always a raw IP
/// in this range, never a MagicDNS name, so checking the address against it is sufficient to tell
/// a tailnet-routed pairing apart from a plain LAN one without needing an explicit flag in the QR
/// payload.
bool isTailnetHost(String hostAndPort) {
  final host = hostAndPort.split(':').first;
  final octets = host.split('.');
  if (octets.length != 4) return false;
  final parsed = octets.map(int.tryParse).toList();
  if (parsed.any((o) => o == null || o < 0 || o > 255)) return false;
  return parsed[0] == 100 && parsed[1]! >= 64 && parsed[1]! <= 127;
}
