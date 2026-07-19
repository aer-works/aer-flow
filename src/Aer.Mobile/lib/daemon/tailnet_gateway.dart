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

  /// Brings the embedded node up. No `authKey` is passed — on a device with no persisted tailnet
  /// identity yet, `tsnet` returns `needsLogin` with [TailscaleStatus.authUrl] populated instead of
  /// throwing; the caller opens that URL in a browser and waits for [onStateChange] to reach
  /// [NodeState.running]. On later launches, persisted credentials reconnect with no auth key and
  /// no browser step at all.
  Future<TailscaleStatus> ensureUp() {
    return Tailscale.instance.up(hostname: 'aer-mobile');
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
