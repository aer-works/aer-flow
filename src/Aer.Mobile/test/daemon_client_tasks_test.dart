import 'dart:convert';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import 'package:aer_mobile/daemon/daemon_client.dart';

void main() {
  group('DaemonClient M24 Phase 5 Task Lifecycle Endpoints', () {
    test('listTasks returns the fleet list and defaults includeArchived to false', () async {
      final mockClient = MockClient((request) async {
        expect(request.url.path, '/api/tasks');
        expect(request.url.queryParameters['includeArchived'], 'false');
        return http.Response(
          jsonEncode([
            {
              'taskDirectoryPath': 'C:/Users/pbree/.aer/tasks/foo',
              'friendlyName': 'foo',
              'typeLabel': 'solo-run-template',
              'statusText': 'Running',
              'pausedStepCount': 0,
              'isArchived': false,
            },
          ]),
          200,
        );
      });

      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      final items = await client.listTasks();
      expect(items, hasLength(1));
      expect(items.single.friendlyName, 'foo');
      expect(items.single.isArchived, isFalse);
    });

    test('listTasks passes includeArchived through as a query parameter', () async {
      final mockClient = MockClient((request) async {
        expect(request.url.queryParameters['includeArchived'], 'true');
        return http.Response(jsonEncode([]), 200);
      });

      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await client.listTasks(includeArchived: true);
    });

    test('archiveTask posts the directory path', () async {
      final mockClient = MockClient((request) async {
        expect(request.url.path, '/api/tasks/archive');
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        expect(body['directoryPath'], 'C:/Users/pbree/.aer/tasks/foo');
        return http.Response('', 200);
      });

      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await client.archiveTask('C:/Users/pbree/.aer/tasks/foo');
    });

    test('unarchiveTask posts the directory path', () async {
      final mockClient = MockClient((request) async {
        expect(request.url.path, '/api/tasks/unarchive');
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        expect(body['directoryPath'], 'C:/Users/pbree/.aer/tasks/foo');
        return http.Response('', 200);
      });

      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await client.unarchiveTask('C:/Users/pbree/.aer/tasks/foo');
    });

    test('deleteTask posts the directory path', () async {
      final mockClient = MockClient((request) async {
        expect(request.url.path, '/api/tasks/delete');
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        expect(body['directoryPath'], 'C:/Users/pbree/.aer/tasks/foo');
        return http.Response('', 200);
      });

      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await client.deleteTask('C:/Users/pbree/.aer/tasks/foo');
    });

    test('deleteTask throws DaemonException on a non-2xx response', () async {
      final mockClient = MockClient((request) async {
        return http.Response('DirectoryPath must be inside ~/.aer/tasks or ~/.aer/sessions.', 400);
      });

      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      expect(() => client.deleteTask('C:/outside'), throwsA(isA<DaemonException>()));
    });
  });
}
