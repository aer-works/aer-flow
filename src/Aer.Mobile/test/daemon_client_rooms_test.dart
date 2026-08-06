import 'dart:convert';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import 'package:aer_mobile/daemon/daemon_client.dart';

void main() {
  group('DaemonClient M24 Phase 5 Task Lifecycle Endpoints', () {
    test('listRooms returns the fleet list and defaults includeArchived to false', () async {
      final mockClient = MockClient((request) async {
        expect(request.url.path, '/api/rooms');
        expect(request.url.queryParameters['includeArchived'], 'false');
        return http.Response(
          jsonEncode([
            {
              'roomDirectoryPath': 'C:/Users/pbree/.aer/tasks/foo',
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

      final items = await client.listRooms();
      expect(items, hasLength(1));
      expect(items.single.friendlyName, 'foo');
      expect(items.single.isArchived, isFalse);
    });

    test('listRooms passes includeArchived through as a query parameter', () async {
      final mockClient = MockClient((request) async {
        expect(request.url.queryParameters['includeArchived'], 'true');
        return http.Response(jsonEncode([]), 200);
      });

      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await client.listRooms(includeArchived: true);
    });

    test('archiveRoom posts the directory path', () async {
      final mockClient = MockClient((request) async {
        expect(request.url.path, '/api/rooms/archive');
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        expect(body['directoryPath'], 'C:/Users/pbree/.aer/tasks/foo');
        return http.Response('', 200);
      });

      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await client.archiveRoom('C:/Users/pbree/.aer/tasks/foo');
    });

    test('unarchiveRoom posts the directory path', () async {
      final mockClient = MockClient((request) async {
        expect(request.url.path, '/api/rooms/unarchive');
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        expect(body['directoryPath'], 'C:/Users/pbree/.aer/tasks/foo');
        return http.Response('', 200);
      });

      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await client.unarchiveRoom('C:/Users/pbree/.aer/tasks/foo');
    });

    test('deleteRoom posts the directory path', () async {
      final mockClient = MockClient((request) async {
        expect(request.url.path, '/api/rooms/delete');
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        expect(body['directoryPath'], 'C:/Users/pbree/.aer/tasks/foo');
        return http.Response('', 200);
      });

      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await client.deleteRoom('C:/Users/pbree/.aer/tasks/foo');
    });

    test('deleteRoom throws DaemonException on a non-2xx response', () async {
      final mockClient = MockClient((request) async {
        return http.Response('DirectoryPath must be inside ~/.aer/rooms.', 400);
      });

      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      expect(() => client.deleteRoom('C:/outside'), throwsA(isA<DaemonException>()));
    });
  });
}
