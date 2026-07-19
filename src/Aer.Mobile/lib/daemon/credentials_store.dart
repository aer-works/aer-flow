import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// The pairing token is a bearer credential, not app preference data — kept in the platform
/// keystore (Android Keystore-backed EncryptedSharedPreferences) rather than shared_preferences'
/// plaintext XML.
class DaemonCredentials {
  final String host;
  final String token;

  const DaemonCredentials({required this.host, required this.token});
}

class CredentialsStore {
  static const _hostKey = 'daemon_host';
  static const _tokenKey = 'daemon_token';

  final _storage = const FlutterSecureStorage();

  Future<DaemonCredentials?> load() async {
    final host = await _storage.read(key: _hostKey);
    final token = await _storage.read(key: _tokenKey);
    if (host == null || token == null) return null;
    return DaemonCredentials(host: host, token: token);
  }

  Future<void> save(DaemonCredentials credentials) async {
    await _storage.write(key: _hostKey, value: credentials.host);
    await _storage.write(key: _tokenKey, value: credentials.token);
  }

  Future<void> clear() async {
    await _storage.delete(key: _hostKey);
    await _storage.delete(key: _tokenKey);
  }
}
