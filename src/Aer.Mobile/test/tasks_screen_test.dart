import 'dart:convert';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import 'package:aer_mobile/daemon/daemon_client.dart';
import 'package:aer_mobile/tasks_screen.dart';

/// Bulk select (issue #288) widget-level coverage — the Flutter counterpart of
/// `Aer.Ui.Tests`' `TasksViewModelTests.cs`. Exercises long-press-to-select, the bulk archive/delete
/// app bar actions, and the "Delete N tasks?" confirm, all against a [MockClient] rather than a real
/// daemon (same approach `daemon_client_tasks_test.dart` already uses for the single-item calls this
/// screen was built on).
void main() {
  Map<String, dynamic> fleetItemJson(String path, {bool archived = false}) => {
        'taskDirectoryPath': path,
        'friendlyName': path.split('/').last,
        'typeLabel': 'solo-run-template',
        'statusText': 'Idle',
        'pausedStepCount': 0,
        'isArchived': archived,
      };

  group('TasksScreen bulk select (#288)', () {
    testWidgets('Long-pressing a card enters selection mode and shows the selected count', (tester) async {
      final mockClient = MockClient((request) async {
        if (request.method == 'GET' && request.url.path == '/api/tasks') {
          return http.Response(jsonEncode([fleetItemJson('/tasks/a'), fleetItemJson('/tasks/b')]), 200);
        }
        return http.Response('unexpected request: ${request.method} ${request.url}', 500);
      });
      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await tester.pumpWidget(MaterialApp(home: TasksScreen(client: client)));
      await tester.pumpAndSettle();

      expect(find.text('Tasks'), findsOneWidget);
      expect(find.byType(Checkbox), findsNothing);

      await tester.longPress(find.text('a'));
      await tester.pumpAndSettle();

      expect(find.text('1 selected'), findsOneWidget);
      expect(find.byType(Checkbox), findsNWidgets(2));
    });

    testWidgets('Archive selected only archives the selected, not-yet-archived items and exits selection mode', (tester) async {
      final archiveRequests = <String>[];
      final mockClient = MockClient((request) async {
        if (request.method == 'GET' && request.url.path == '/api/tasks') {
          return http.Response(jsonEncode([fleetItemJson('/tasks/a'), fleetItemJson('/tasks/b')]), 200);
        }
        if (request.method == 'POST' && request.url.path == '/api/tasks/archive') {
          final body = jsonDecode(request.body) as Map<String, dynamic>;
          archiveRequests.add(body['directoryPath'] as String);
          return http.Response('', 200);
        }
        return http.Response('unexpected request: ${request.method} ${request.url}', 500);
      });
      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await tester.pumpWidget(MaterialApp(home: TasksScreen(client: client)));
      await tester.pumpAndSettle();

      await tester.longPress(find.text('a'));
      await tester.pumpAndSettle();

      await tester.tap(find.byTooltip('Archive selected'));
      await tester.pumpAndSettle();

      expect(archiveRequests, ['/tasks/a']);
      expect(find.text('Tasks'), findsOneWidget);
      expect(find.byType(Checkbox), findsNothing);
    });

    testWidgets('Delete selected asks one confirm naming the count, then deletes every selected item', (tester) async {
      final deleteRequests = <String>[];
      final mockClient = MockClient((request) async {
        if (request.method == 'GET' && request.url.path == '/api/tasks') {
          return http.Response(jsonEncode([fleetItemJson('/tasks/a'), fleetItemJson('/tasks/b')]), 200);
        }
        if (request.method == 'POST' && request.url.path == '/api/tasks/delete') {
          final body = jsonDecode(request.body) as Map<String, dynamic>;
          deleteRequests.add(body['directoryPath'] as String);
          return http.Response('', 200);
        }
        return http.Response('unexpected request: ${request.method} ${request.url}', 500);
      });
      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await tester.pumpWidget(MaterialApp(home: TasksScreen(client: client)));
      await tester.pumpAndSettle();

      await tester.longPress(find.text('a'));
      await tester.pumpAndSettle();
      await tester.tap(find.byType(Checkbox).last);
      await tester.pumpAndSettle();
      expect(find.text('2 selected'), findsOneWidget);

      await tester.tap(find.byTooltip('Delete selected'));
      await tester.pumpAndSettle();

      // One confirm for the whole batch, not one per item.
      expect(find.text('Delete 2 tasks?'), findsOneWidget);
      expect(deleteRequests, isEmpty);

      await tester.tap(find.text('Delete'));
      await tester.pumpAndSettle();

      expect(deleteRequests, unorderedEquals(['/tasks/a', '/tasks/b']));
      expect(find.text('Tasks'), findsOneWidget);
    });

    testWidgets('Cancelling the bulk delete confirm deletes nothing', (tester) async {
      final mockClient = MockClient((request) async {
        if (request.method == 'GET' && request.url.path == '/api/tasks') {
          return http.Response(jsonEncode([fleetItemJson('/tasks/a')]), 200);
        }
        return http.Response('unexpected request: ${request.method} ${request.url}', 500);
      });
      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await tester.pumpWidget(MaterialApp(home: TasksScreen(client: client)));
      await tester.pumpAndSettle();

      await tester.longPress(find.text('a'));
      await tester.pumpAndSettle();

      await tester.tap(find.byTooltip('Delete selected'));
      await tester.pumpAndSettle();
      expect(find.text('Delete 1 task?'), findsOneWidget);

      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();

      // Still in selection mode with the same selection -- cancelling the confirm is not the same
      // as exiting selection mode.
      expect(find.text('1 selected'), findsOneWidget);
    });
  });
}
