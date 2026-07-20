import 'dart:convert';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import 'package:aer_mobile/daemon/daemon_client.dart';

void main() {
  group('DaemonClient M22 Template Endpoints', () {
    test('listTemplates returns catalog and available vendors', () async {
      final mockClient = MockClient((request) async {
        expect(request.url.path, '/api/templates');
        expect(request.headers['Authorization'], 'Bearer fake-token');
        return http.Response(
          jsonEncode({
            'templates': [
              {
                'id': 'solo-run',
                'title': 'Solo Run',
                'description': 'Single-step execution',
                'requiresSecondaryVendor': false,
              },
              {
                'id': 'review-run',
                'title': 'Review Run',
                'description': 'Two-step execution',
                'requiresSecondaryVendor': true,
              }
            ],
            'availableVendors': [
              {'adapterName': 'claude', 'binaryName': 'claude', 'isAvailable': true},
              {'adapterName': 'gemini', 'binaryName': 'agy', 'isAvailable': true},
            ]
          }),
          200,
        );
      });

      final client = DaemonClient(
        host: 'localhost:5000',
        token: 'fake-token',
        httpClient: mockClient,
      );

      final result = await client.listTemplates();
      final templates = result['templates'] as List;
      expect(templates.length, 2);
      expect(templates[0]['id'], 'solo-run');
    });

    test('runTemplate sends parameters and returns materialized path', () async {
      final mockClient = MockClient((request) async {
        expect(request.url.path, '/api/templates/run');
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        expect(body['templateId'], 'solo-run');
        expect(body['primaryAdapter'], 'claude');
        return http.Response(
          jsonEncode({'taskDirectoryPath': '/home/user/.aer/tasks/task-123'}),
          200,
        );
      });

      final client = DaemonClient(
        host: 'localhost:5000',
        token: 'fake-token',
        httpClient: mockClient,
      );

      final dirPath = await client.runTemplate(
        templateId: 'solo-run',
        primaryAdapter: 'claude',
      );

      expect(dirPath, '/home/user/.aer/tasks/task-123');
    });

    test('decide with artifactReference formats payload correctly', () async {
      final mockClient = MockClient((request) async {
        expect(request.url.path, '/api/tasks/decide');
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        expect(body['decisionType'], 'Supersede');
        expect(body['targetStepId'], 'draft');
        expect(body['artifactReference'], {
          'executionId': 'exec_2',
          'fileName': 'draft.md',
        });
        return http.Response('', 200);
      });

      final client = DaemonClient(
        host: 'localhost:5000',
        token: 'fake-token',
        httpClient: mockClient,
      );

      await client.decide(
        directoryPath: '/home/user/.aer/tasks/task-123',
        stepId: 'review',
        executionId: 'exec_2',
        decisionType: 'Supersede',
        targetStepId: 'draft',
        artifactReference: {
          'executionId': 'exec_2',
          'fileName': 'draft.md',
        },
      );
    });
  });
}
