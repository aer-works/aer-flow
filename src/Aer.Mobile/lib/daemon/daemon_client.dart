import 'dart:convert';

import 'package:http/http.dart' as http;
import 'package:tailscale/tailscale.dart';
import 'package:web_socket_channel/web_socket_channel.dart';

import 'models.dart';
import 'tailnet_gateway.dart';
import 'ws_client.dart';

class DaemonException implements Exception {
  final String message;
  DaemonException(this.message);

  @override
  String toString() => message;
}

typedef TsnetDialFn = Future<TailscaleConnection> Function(String host, int port);

/// REST + WebSocket client for Aer.Daemon's remote API (src/Aer.Daemon/Program.cs). `host` is an
/// authority string like `192.168.1.23:5050` — no scheme, matching how the pairing screen collects
/// it (the user types/scans host+port, not a full URL).
class DaemonClient {
  final String host;
  final String token;
  final bool tsnetRouted;
  final http.Client? httpClient;
  final TsnetDialFn? tsnetDialFn;

  DaemonClient({
    required this.host,
    required this.token,
    this.tsnetRouted = false,
    this.httpClient,
    this.tsnetDialFn,
  });

  /// The only unauthenticated endpoint besides GET /api/version. Returns the raw paired-client
  /// token — shown/stored exactly once, same as the desktop UI's own pairing flow.
  ///
  /// [httpClient] routes the request over the embedded tailnet node (M21 Phase 7, #246) instead of
  /// the phone's regular network stack — required when [host] is a tailnet-only address, since
  /// `tsnet` is a userspace network stack with no OS-level route for it. Omit for a plain LAN host.
  static Future<String> pair({
    required String host,
    required String code,
    required String clientName,
    http.Client? httpClient,
  }) async {
    final response = httpClient != null
        ? await httpClient.post(
            Uri.http(host, '/api/pairing/pair'),
            headers: {'Content-Type': 'application/json'},
            body: jsonEncode({'code': code, 'clientName': clientName}),
          )
        : await http.post(
            Uri.http(host, '/api/pairing/pair'),
            headers: {'Content-Type': 'application/json'},
            body: jsonEncode({'code': code, 'clientName': clientName}),
          );
    final body = caseInsensitive(
      jsonDecode(response.body) as Map<String, dynamic>,
    );
    if (response.statusCode != 200) {
      throw DaemonException(
        body['error']?.toString() ??
            'Pairing failed (HTTP ${response.statusCode}).',
      );
    }
    return body['token'].toString();
  }

  /// The other unauthenticated endpoint. Used as the tsnet connectivity proof (M21 Phase 7,
  /// #246) — a successful call here means the phone joined the tailnet and actually routed to the
  /// sidecar, before trusting a pairing attempt on top of that same [httpClient].
  static Future<void> version({
    required String host,
    http.Client? httpClient,
  }) async {
    final response = httpClient != null
        ? await httpClient.get(Uri.http(host, '/api/version'))
        : await http.get(Uri.http(host, '/api/version'));
    if (response.statusCode != 200) {
      throw DaemonException(
        'Could not reach $host (HTTP ${response.statusCode}).',
      );
    }
  }

  Map<String, String> get _authHeader => {'Authorization': 'Bearer $token'};

  Future<http.Response> _get(Uri url) {
    if (httpClient != null) return httpClient!.get(url, headers: _authHeader);
    if (tsnetRouted) return TailnetGateway().client.get(url, headers: _authHeader);
    return http.get(url, headers: _authHeader);
  }

  Future<http.Response> _post(Uri url, {Object? body, Map<String, String>? headers}) {
    final mergedHeaders = {..._authHeader, ...?headers};
    if (httpClient != null) return httpClient!.post(url, headers: mergedHeaders, body: body);
    if (tsnetRouted) return TailnetGateway().client.post(url, headers: mergedHeaders, body: body);
    return http.post(url, headers: mergedHeaders, body: body);
  }

  /// A full TaskProjection snapshot on connect (if a task is currently open server-side) and again
  /// on every state change thereafter — never a diff. See TaskProjection's doc comment for why the
  /// snapshot alone doesn't tell this client which directory it's for.
  Stream<TaskProjection> watch() {
    if (tsnetRouted) {
      return _watchOverTsnet();
    }
    final channel = WebSocketChannel.connect(
      Uri.parse('ws://$host/api/ws?token=$token'),
    );
    return channel.stream.map(
      (raw) => TaskProjection.fromJson(
        jsonDecode(raw as String) as Map<String, dynamic>,
      ),
    );
  }

  Stream<TaskProjection> _watchOverTsnet() async* {
    final parts = host.split(':');
    final targetHost = parts[0];
    final targetPort = parts.length > 1 ? int.parse(parts[1]) : 5050;

    final dial = tsnetDialFn ?? ((h, p) => Tailscale.instance.tcp.dial(h, p));
    final connection = await dial(targetHost, targetPort);
    final wsChannel = await TsnetWsChannel.connect(
      socket: TailscaleWsSocket(connection),
      host: host,
      path: '/api/ws?token=$token',
    );

    try {
      yield* wsChannel.stream.map(
        (raw) => TaskProjection.fromJson(
          jsonDecode(raw) as Map<String, dynamic>,
        ),
      );
    } finally {
      await wsChannel.close();
    }
  }

  Future<List<String>> recentTasks() async {
    final response = await _get(Uri.http(host, '/api/tasks/recent'));
    _throwIfFailed(response);
    return (jsonDecode(response.body) as List<dynamic>)
        .map((d) => d.toString())
        .toList();
  }

  /// Reassigns which task is "current" for every connected client, desktop included — see
  /// TaskProjection's doc comment. Only call this from an explicit user action (the recent-tasks
  /// picker), never automatically, so the phone doesn't silently steal the desktop's view.
  Future<void> openTask(String directoryPath) async {
    final response = await _post(
      Uri.http(host, '/api/tasks/open'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'directoryPath': directoryPath}),
    );
    _throwIfFailed(response);
  }

  /// Text content of one execution's output file, or null if the daemon has no such file (already
  /// deleted, or the execution/fileName pair doesn't match its recorded OutputFiles). See
  /// src/Aer.Daemon/Program.cs's /api/tasks/artifact handler (M21 Phase 2, #232).
  Future<String?> fetchArtifact({
    required String directoryPath,
    required String executionId,
    required String fileName,
  }) async {
    final uri = Uri.http(host, '/api/tasks/artifact', {
      'directoryPath': directoryPath,
      'executionId': executionId,
      'fileName': fileName,
    });
    final response = await _get(uri);
    if (response.statusCode == 404) return null;
    _throwIfFailed(response);
    final body = caseInsensitive(
      jsonDecode(response.body) as Map<String, dynamic>,
    );
    return body['content'].toString();
  }

  /// Lists available built-in workflow templates and vendor CLI presence.
  Future<Map<String, dynamic>> listTemplates() async {
    final response = await _get(Uri.http(host, '/api/templates'));
    _throwIfFailed(response);
    return jsonDecode(response.body) as Map<String, dynamic>;
  }

  /// Runs a built-in template on the daemon and returns the materialized task directory path.
  Future<String> runTemplate({
    required String templateId,
    String? primaryAdapter,
    String? secondaryAdapter,
    String? taskName,
    String? customPrompt,
    String? secondaryCustomPrompt,
  }) async {
    final response = await _post(
      Uri.http(host, '/api/templates/run'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({
        'templateId': templateId,
        'primaryAdapter': primaryAdapter,
        'secondaryAdapter': secondaryAdapter,
        'taskName': taskName,
        'customPrompt': customPrompt,
        'secondaryCustomPrompt': secondaryCustomPrompt,
      }),
    );
    _throwIfFailed(response);
    final body = caseInsensitive(jsonDecode(response.body) as Map<String, dynamic>);
    return (body['taskdirectorypath'] ?? body['taskDirectoryPath'])?.toString() ?? '';
  }

  /// decisionType is one of "Resume" | "Reject" | "Supersede" | "RetryWithRevision".
  /// Supports optional [artifactReference] ({'executionId': ..., 'fileName': ...}) for server-side
  /// resolution when the client has no local filesystem access.
  Future<void> decide({
    required String directoryPath,
    required String stepId,
    required String executionId,
    required String decisionType,
    String? targetStepId,
    String? revisionFilePath,
    Map<String, String>? artifactReference,
  }) async {
    final payload = <String, dynamic>{
      'directoryPath': directoryPath,
      'stepId': stepId,
      'executionId': executionId,
      'decisionType': decisionType,
      // ignore: use_null_aware_elements
      if (targetStepId != null) 'targetStepId': targetStepId,
      // ignore: use_null_aware_elements
      if (revisionFilePath != null) 'revisionFilePath': revisionFilePath,
      // ignore: use_null_aware_elements
      if (artifactReference != null) 'artifactReference': artifactReference,
    };
    final response = await _post(
      Uri.http(host, '/api/tasks/decide'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode(payload),
    );
    _throwIfFailed(response);
  }

  /// executionId null cancels the whole run; non-null cancels just that execution.
  Future<void> cancelRun({
    required String directoryPath,
    String? executionId,
  }) async {
    final response = await _post(
      Uri.http(host, '/api/tasks/cancel'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({
        'directoryPath': directoryPath,
        'executionId': executionId,
      }),
    );
    _throwIfFailed(response);
  }

  void _throwIfFailed(http.Response response) {
    if (response.statusCode < 200 || response.statusCode >= 300) {
      throw DaemonException(
        'Request failed (HTTP ${response.statusCode}): ${response.body}',
      );
    }
  }
}
