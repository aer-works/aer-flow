import 'package:flutter_test/flutter_test.dart';

import 'package:aer_mobile/daemon/models.dart';

void main() {
  // Aer.Daemon serializes REST responses camelCase and WS pushes PascalCase (see models.dart's
  // doc comment) — both must parse to the same result, since this app receives both.
  const camelCaseJson = {
    'directoryPath': 'C:/tasks/foo',
    'snapshot': {
      'workflowTemplateId': 'draft-and-review',
      'steps': [
        {
          'stepId': 'critic',
          'worker': 'agy',
          'pausePoint': {
            'supersedeTargets': ['architect'],
          },
        },
      ],
    },
    'state': {
      'status': 'Paused',
      'steps': [
        {'stepId': 'critic', 'status': 'Paused', 'latestExecutionId': 'c-1'},
      ],
    },
    'lineage': {
      'executions': [
        {
          'executionId': 'c-1',
          'worker': 'agy',
          'outputFiles': ['review.md'],
        },
      ],
    },
  };

  const pascalCaseJson = {
    'DirectoryPath': 'C:/tasks/foo',
    'Snapshot': {
      'WorkflowTemplateId': 'draft-and-review',
      'Steps': [
        {
          'StepId': 'critic',
          'Worker': 'agy',
          'PausePoint': {
            'SupersedeTargets': ['architect'],
          },
        },
      ],
    },
    'State': {
      'Status': 'Paused',
      'Steps': [
        {'StepId': 'critic', 'Status': 'Paused', 'LatestExecutionId': 'c-1'},
      ],
    },
    'Lineage': {
      'Executions': [
        {
          'ExecutionId': 'c-1',
          'Worker': 'agy',
          'OutputFiles': ['review.md'],
        },
      ],
    },
  };

  for (final entry in {'camelCase (REST)': camelCaseJson, 'PascalCase (WS)': pascalCaseJson}.entries) {
    test('TaskProjection.fromJson parses ${entry.key} the same way', () {
      final projection = TaskProjection.fromJson(entry.value);

      expect(projection.directoryPath, 'C:/tasks/foo');
      expect(projection.workflowTemplateId, 'draft-and-review');
      expect(projection.status, 'Paused');
      expect(projection.pausedSteps, hasLength(1));

      final step = projection.pausedSteps.single;
      expect(step.stepId, 'critic');
      expect(step.latestExecutionId, 'c-1');

      final definition = projection.definitionFor(step.stepId);
      expect(definition?.worker, 'agy');
      expect(definition?.supersedeTargets, ['architect']);

      final execution = projection.executionFor(step.latestExecutionId);
      expect(execution?.outputFiles, ['review.md']);
    });
  }

  test('TaskProjection.fromJson handles a task with no lineage yet (no executions recorded)', () {
    final projection = TaskProjection.fromJson({
      'Snapshot': {'WorkflowTemplateId': 'draft-and-review', 'Steps': []},
      'State': {'Status': 'Running', 'Steps': []},
    });

    expect(projection.pausedSteps, isEmpty);
    expect(projection.executions, isEmpty);
  });
}
