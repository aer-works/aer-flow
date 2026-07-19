import 'dart:convert';

import 'package:http/http.dart' as http;
import 'package:web_socket_channel/web_socket_channel.dart';

import 'models.dart';

class DaemonException implements Exception {
  final String message;
  DaemonException(this.message);

  @override
  String toString() => message;
}

/// REST + WebSocket client for Aer.Daemon's remote API (src/Aer.Daemon/Program.cs). `host` is an
/// authority string like `192.168.1.23:5050` — no scheme, matching how the pairing screen collects
/// it (the user types/scans host+port, not a full URL).
class DaemonClient {
  final String host;
  final String token;

  DaemonClient({required this.host, required this.token});

  /// The only unauthenticated endpoint besides GET /api/version. Returns the raw paired-client
  /// token — shown/stored exactly once, same as the desktop UI's own pairing flow.
  static Future<String> pair({required String host, required String code, required String clientName}) async {
    final response = await http.post(
      Uri.http(host, '/api/pairing/pair'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'code': code, 'clientName': clientName}),
    );
    final body = caseInsensitive(jsonDecode(response.body) as Map<String, dynamic>);
    if (response.statusCode != 200) {
      throw DaemonException(body['error']?.toString() ?? 'Pairing failed (HTTP ${response.statusCode}).');
    }
    return body['token'].toString();
  }

  Map<String, String> get _authHeader => {'Authorization': 'Bearer $token'};

  /// A full TaskProjection snapshot on connect (if a task is currently open server-side) and again
  /// on every state change thereafter — never a diff. See TaskProjection's doc comment for why the
  /// snapshot alone doesn't tell this client which directory it's for.
  Stream<TaskProjection> watch() {
    final channel = WebSocketChannel.connect(Uri.parse('ws://$host/api/ws?token=$token'));
    return channel.stream.map((raw) => TaskProjection.fromJson(jsonDecode(raw as String) as Map<String, dynamic>));
  }

  Future<List<String>> recentTasks() async {
    final response = await http.get(Uri.http(host, '/api/tasks/recent'), headers: _authHeader);
    _throwIfFailed(response);
    return (jsonDecode(response.body) as List<dynamic>).map((d) => d.toString()).toList();
  }

  /// Reassigns which task is "current" for every connected client, desktop included — see
  /// TaskProjection's doc comment. Only call this from an explicit user action (the recent-tasks
  /// picker), never automatically, so the phone doesn't silently steal the desktop's view.
  Future<void> openTask(String directoryPath) async {
    final response = await http.post(
      Uri.http(host, '/api/tasks/open'),
      headers: {..._authHeader, 'Content-Type': 'application/json'},
      body: jsonEncode({'directoryPath': directoryPath}),
    );
    _throwIfFailed(response);
  }

  /// Text content of one execution's output file, or null if the daemon has no such file (already
  /// deleted, or the execution/fileName pair doesn't match its recorded OutputFiles). See
  /// src/Aer.Daemon/Program.cs's /api/tasks/artifact handler (M21 Phase 2, #232).
  Future<String?> fetchArtifact({required String directoryPath, required String executionId, required String fileName}) async {
    final uri = Uri.http(host, '/api/tasks/artifact', {
      'directoryPath': directoryPath,
      'executionId': executionId,
      'fileName': fileName,
    });
    final response = await http.get(uri, headers: _authHeader);
    if (response.statusCode == 404) return null;
    _throwIfFailed(response);
    final body = caseInsensitive(jsonDecode(response.body) as Map<String, dynamic>);
    return body['content'].toString();
  }

  /// decisionType is one of "Resume" | "Reject" — RetryWithRevision and Supersede need a way to
  /// move file content onto the daemon host that this app doesn't yet have (deferred past Phase 2).
  Future<void> decide({required String directoryPath, required String stepId, required String executionId, required String decisionType}) async {
    final response = await http.post(
      Uri.http(host, '/api/tasks/decide'),
      headers: {..._authHeader, 'Content-Type': 'application/json'},
      body: jsonEncode({
        'directoryPath': directoryPath,
        'stepId': stepId,
        'executionId': executionId,
        'decisionType': decisionType,
      }),
    );
    _throwIfFailed(response);
  }

  /// executionId null cancels the whole run; non-null cancels just that execution.
  Future<void> cancelRun({required String directoryPath, String? executionId}) async {
    final response = await http.post(
      Uri.http(host, '/api/tasks/cancel'),
      headers: {..._authHeader, 'Content-Type': 'application/json'},
      body: jsonEncode({'directoryPath': directoryPath, 'executionId': executionId}),
    );
    _throwIfFailed(response);
  }

  void _throwIfFailed(http.Response response) {
    if (response.statusCode < 200 || response.statusCode >= 300) {
      throw DaemonException('Request failed (HTTP ${response.statusCode}): ${response.body}');
    }
  }
}
