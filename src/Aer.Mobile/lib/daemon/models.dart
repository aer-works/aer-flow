/// Aer.Daemon's TaskProjection, as pushed over /api/ws and returned by /api/tasks/open.
///
/// REST payloads are camelCase; WS payloads are PascalCase (Aer.Daemon's own
/// `SendStateAsync` builds a bare JsonSerializerOptions with no naming policy — see
/// src/Aer.Daemon/Program.cs). Every fromJson below reads through [caseInsensitive],
/// which normalizes both to the same lowercase keys, rather than guessing casing per field.
library;

Map<String, dynamic> caseInsensitive(Map<String, dynamic> json) =>
    json.map((key, value) => MapEntry(key.toLowerCase(), value));

/// One step's static definition, from TaskProjection.Snapshot.Steps.
class StepDefinition {
  final String stepId;
  final String worker;
  final List<String> supersedeTargets;

  StepDefinition({required this.stepId, required this.worker, required this.supersedeTargets});

  factory StepDefinition.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    final pausePoint = j['pausepoint'] as Map<String, dynamic>?;
    final targets = pausePoint == null
        ? <String>[]
        : ((caseInsensitive(pausePoint)['supersedetargets'] as List<dynamic>?) ?? [])
            .map((t) => t.toString())
            .toList();
    return StepDefinition(
      stepId: j['stepid'].toString(),
      worker: j['worker'].toString(),
      supersedeTargets: targets,
    );
  }
}

/// One step's live status, from TaskProjection.State.Steps.
class WorkflowStepState {
  final String stepId;
  final String status;
  final String? latestExecutionId;

  WorkflowStepState({required this.stepId, required this.status, required this.latestExecutionId});

  bool get isPaused => status == 'Paused';

  factory WorkflowStepState.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    return WorkflowStepState(
      stepId: j['stepid'].toString(),
      status: j['status'].toString(),
      latestExecutionId: j['latestexecutionid']?.toString(),
    );
  }
}

/// One execution's artifact-directory contents, from TaskProjection.Lineage.Executions.
class ExecutionArtifacts {
  final String executionId;
  final String worker;
  final List<String> outputFiles;

  ExecutionArtifacts({required this.executionId, required this.worker, required this.outputFiles});

  factory ExecutionArtifacts.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    return ExecutionArtifacts(
      executionId: j['executionid'].toString(),
      worker: j['worker'].toString(),
      outputFiles: ((j['outputfiles'] as List<dynamic>?) ?? []).map((f) => f.toString()).toList(),
    );
  }
}

/// A projection Aer.Daemon pushes for one task directory. Aer.Daemon still has only one
/// "current" task server-side (TaskSession.CurrentTaskDirectoryPath) and broadcasts every
/// change to every connected WS client regardless of which directory it's for — but this app
/// filters incoming pushes against InboxScreen's own `_openDirectoryPath` before applying one
/// (fixed alongside issue #262's chat work; see `_connect`'s listener), so a different client
/// opening a different task no longer silently changes what this phone shows. directoryPath
/// comes from the DirectoryPath sibling property Aer.Daemon adds to the WS payload (M21 Phase 2,
/// #232) — it is not part of TaskProjection itself, since /api/tasks/decide and /api/tasks/cancel
/// need it and a WS-only client (this app) has no other way to learn it, and it's also this
/// filter's join key.
class TaskProjection {
  final String? directoryPath;
  final String workflowTemplateId;
  final String status;
  final List<StepDefinition> stepDefinitions;
  final List<WorkflowStepState> steps;
  final List<ExecutionArtifacts> executions;
  final Map<String, String> workerAdapters;

  TaskProjection({
    required this.directoryPath,
    required this.workflowTemplateId,
    required this.status,
    required this.stepDefinitions,
    required this.steps,
    required this.executions,
    required this.workerAdapters,
  });

  List<WorkflowStepState> get pausedSteps => steps.where((s) => s.isPaused).toList();

  StepDefinition? definitionFor(String stepId) =>
      stepDefinitions.where((d) => d.stepId == stepId).cast<StepDefinition?>().firstWhere((_) => true, orElse: () => null);

  ExecutionArtifacts? executionFor(String? executionId) => executionId == null
      ? null
      : executions.where((e) => e.executionId == executionId).cast<ExecutionArtifacts?>().firstWhere((_) => true, orElse: () => null);

  factory TaskProjection.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    final snapshot = caseInsensitive(j['snapshot'] as Map<String, dynamic>);
    final state = caseInsensitive(j['state'] as Map<String, dynamic>);
    final lineage = j['lineage'] == null ? <String, dynamic>{} : caseInsensitive(j['lineage'] as Map<String, dynamic>);

    final workerAdapters = <String, String>{};
    if (j['workeradapters'] is Map<String, dynamic>) {
      (j['workeradapters'] as Map<String, dynamic>).forEach((k, v) {
        if (v != null) workerAdapters[k] = v.toString();
      });
    }

    return TaskProjection(
      directoryPath: j['directorypath']?.toString(),
      workflowTemplateId: snapshot['workflowtemplateid'].toString(),
      status: state['status'].toString(),
      stepDefinitions:
          ((snapshot['steps'] as List<dynamic>?) ?? []).map((s) => StepDefinition.fromJson(s as Map<String, dynamic>)).toList(),
      steps: ((state['steps'] as List<dynamic>?) ?? []).map((s) => WorkflowStepState.fromJson(s as Map<String, dynamic>)).toList(),
      executions: ((lineage['executions'] as List<dynamic>?) ?? [])
          .map((e) => ExecutionArtifacts.fromJson(e as Map<String, dynamic>))
          .toList(),
      workerAdapters: workerAdapters,
    );
  }
}
