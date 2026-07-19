import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// The pairing token is a bearer credential, not app preference data — kept in the platform
/// keystore (Android Keystore-backed EncryptedSharedPreferences) rather than shared_preferences'
/// plaintext XML.
class DaemonCredentials {
  final String host;
  final String token;

  /// Whether [host] is only reachable through the embedded tailnet node (M21 Phase 7, #246) —
  /// set when the paired host fell in Tailscale's CGNAT range at pairing time. A relaunch needs
  /// this to know whether to bring `tsnet` back up before reconnecting, since a plain LAN host
  /// needs no such step.
  final bool tsnetRouted;

  const DaemonCredentials({
    required this.host,
    required this.token,
    this.tsnetRouted = false,
  });
}

class CredentialsStore {
  static const _hostKey = 'daemon_host';
  static const _tokenKey = 'daemon_token';
  static const _tsnetRoutedKey = 'daemon_tsnet_routed';

  final _storage = const FlutterSecureStorage();

  Future<DaemonCredentials?> load() async {
    final host = await _storage.read(key: _hostKey);
    final token = await _storage.read(key: _tokenKey);
    if (host == null || token == null) return null;
    final tsnetRouted = await _storage.read(key: _tsnetRoutedKey);
    return DaemonCredentials(
      host: host,
      token: token,
      tsnetRouted: tsnetRouted == 'true',
    );
  }

  Future<void> save(DaemonCredentials credentials) async {
    await _storage.write(key: _hostKey, value: credentials.host);
    await _storage.write(key: _tokenKey, value: credentials.token);
    await _storage.write(
      key: _tsnetRoutedKey,
      value: credentials.tsnetRouted.toString(),
    );
  }

  Future<void> clear() async {
    await _storage.delete(key: _hostKey);
    await _storage.delete(key: _tokenKey);
    await _storage.delete(key: _tsnetRoutedKey);
  }
}
