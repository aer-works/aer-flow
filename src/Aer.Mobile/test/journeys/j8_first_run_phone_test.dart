@Tags(['journey'])
library;

import 'dart:convert';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import 'package:aer_mobile/daemon/daemon_client.dart';
import 'package:aer_mobile/tasks_screen.dart';

/// Journey J8 — "Open it for the first time and know what to do." Phone leg.
///
/// The Flutter counterpart of `J8_DesktopFirstRunTests` on the .NET side: it drives the real
/// [TasksScreen] widget over an empty task list (a [MockClient] returning `[]`, the same approach
/// `tasks_screen_test.dart` uses) and asserts the empty surface offers a real first action.
///
/// This is a RED-spec. It fails today on purpose: the empty [TasksScreen] renders only the bare
/// message "No tasks or sessions yet." with no way to start work — the #337-class dead-end J8
/// exists to close. When the empty state gains a start-work action (a button or FAB), this goes
/// green and J8's phone leg is kept. J8 overall still reads "Fails" in spec/journeys.md until every
/// leg — this one and the desktop leg — passes.
///
/// (The phone's first-run *pre-pairing* screen already routes to pairing via `main.dart`; the
/// remaining red surface is this empty task list, so that is what this leg drives.)
void main() {
  testWidgets('J8 (phone): an empty task list offers a real first action, not just a dead-end message', (tester) async {
    final mockClient = MockClient((request) async {
      if (request.method == 'GET' && request.url.path == '/api/tasks') {
        return http.Response(jsonEncode([]), 200);
      }
      return http.Response('unexpected request: ${request.method} ${request.url}', 500);
    });
    final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

    await tester.pumpWidget(MaterialApp(home: TasksScreen(client: client)));
    await tester.pumpAndSettle();

    // The dead-end as it stands: the surface tells you it's empty and stops there.
    expect(find.text('No tasks or sessions yet.'), findsOneWidget);

    // J8's bar: a truly empty surface must present a real primary next step — a way to start work —
    // not merely report emptiness. Operationalised as a primary action control (a prominent button
    // or a FAB); the AppBar's secondary Refresh icon does not count. Red until one exists.
    final primaryAction = find.byWidgetPredicate(
      (widget) =>
          widget is FloatingActionButton ||
          widget is FilledButton ||
          widget is ElevatedButton ||
          widget is OutlinedButton,
      description: 'a primary start-work action (FAB or prominent button)',
    );

    expect(
      primaryAction,
      findsWidgets,
      reason: 'The empty TasksScreen shows only "No tasks or sessions yet." with no primary action '
          'to start work — J8 requires a real first action on an empty surface (#337).',
    );
  });
}
