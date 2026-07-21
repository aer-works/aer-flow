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
/// filter's join key. sessionId is the same kind of sibling, added for the mobile chat UI so a
/// push that isn't self-started (seeded from another client, or picked from recent tasks) still
/// tells this phone it's looking at an interactive session and which id to fetch turns for.
class TaskProjection {
  final String? directoryPath;
  final String? sessionId;
  final String workflowTemplateId;
  final String status;
  final List<StepDefinition> stepDefinitions;
  final List<WorkflowStepState> steps;
  final List<ExecutionArtifacts> executions;
  final Map<String, String> workerAdapters;

  TaskProjection({
    required this.directoryPath,
    required this.sessionId,
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
      sessionId: j['sessionid']?.toString(),
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

/// One turn of an interactive session, from SessionMetadata.Turns (Aer.Adapters/InteractiveSessions.cs).
class SessionTurn {
  final int turnIndex;
  final String vendor;
  final String humanMessage;
  final String? assistantResponse;
  final DateTime executedAt;

  SessionTurn({
    required this.turnIndex,
    required this.vendor,
    required this.humanMessage,
    required this.assistantResponse,
    required this.executedAt,
  });

  factory SessionTurn.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    return SessionTurn(
      turnIndex: (j['turnindex'] as num?)?.toInt() ?? 0,
      vendor: j['vendor']?.toString() ?? '',
      humanMessage: j['humanmessage']?.toString() ?? '',
      assistantResponse: j['assistantresponse']?.toString(),
      executedAt: DateTime.tryParse(j['executedat']?.toString() ?? '') ?? DateTime.now(),
    );
  }
}

/// An interactive session's full state, from GET /api/sessions/{sessionId} (Aer.Daemon/Program.cs)
/// — REST-only, camelCase; unlike TaskProjection this is never pushed over /api/ws, so there is no
/// PascalCase/camelCase ambiguity to normalize, but this still reads through [caseInsensitive] for
/// consistency with every other model here.
class SessionMetadata {
  final String sessionId;
  final String taskDirectoryPath;
  final String currentAdapter;
  final int turnCount;
  final List<SessionTurn> turns;

  SessionMetadata({
    required this.sessionId,
    required this.taskDirectoryPath,
    required this.currentAdapter,
    required this.turnCount,
    required this.turns,
  });

  factory SessionMetadata.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    return SessionMetadata(
      sessionId: j['sessionid'].toString(),
      taskDirectoryPath: j['taskdirectorypath'].toString(),
      currentAdapter: j['currentadapter']?.toString() ?? '',
      turnCount: (j['turncount'] as num?)?.toInt() ?? 0,
      turns: ((j['turns'] as List<dynamic>?) ?? []).map((t) => SessionTurn.fromJson(t as Map<String, dynamic>)).toList(),
    );
  }
}

/// One live-streaming event from /api/ws/progress (M24 Phase 1, issue #262) — see TaskSession.cs's
/// ProgressFrame doc comment on desktop for why this is a dedicated socket/frame shape rather than
/// an overload of the /api/ws protocol. Broadcast to every connected progress socket regardless of
/// directory, same as TaskProjection pushes — callers must filter on directoryPath themselves.
class SessionProgressEvent {
  final String? directoryPath;
  final String? stepId;
  final String kind;
  final String text;
  final bool isPartial;

  SessionProgressEvent({
    required this.directoryPath,
    required this.stepId,
    required this.kind,
    required this.text,
    required this.isPartial,
  });

  factory SessionProgressEvent.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    return SessionProgressEvent(
      directoryPath: j['directorypath']?.toString(),
      stepId: j['stepid']?.toString(),
      kind: j['kind']?.toString() ?? '',
      text: j['text']?.toString() ?? '',
      isPartial: j['ispartial'] == true,
    );
  }
}

/// One vendor-discovered skill/command/agent/mode/plugin (M24 Phase 2 follow-up chat capability
/// picker) — the mobile counterpart of Aer.Ui.Core's ChatCapabilityItemViewModel. Only "command"/
/// "skill"/"agent" kinds are invokable; Gemini's "mode"/"plugin" kinds are informational only (see
/// ChatCapabilityItemViewModel's own remarks for why).
class ChatCapabilityItem {
  final String name;
  final String kind;
  final String description;
  final bool isRecentlyUsed;

  ChatCapabilityItem({required this.name, required this.kind, required this.description, required this.isRecentlyUsed});

  bool get isInvokable => kind == 'command' || kind == 'skill' || kind == 'agent';
}

/// GET /api/sessions/{id}/commands's shape: WorkerCapabilities's own fields plus the additive
/// RecentlyUsed sibling (same idiom as TaskProjection's DirectoryPath/WorkerAdapters siblings).
class SessionCommandsResult {
  final String vendor;
  final List<ChatCapabilityItem> items;
  final List<String> models;

  SessionCommandsResult({required this.vendor, required this.items, required this.models});

  factory SessionCommandsResult.fromJson(Map<String, dynamic> json) {
    final j = caseInsensitive(json);
    final recentlyUsed = ((j['recentlyused'] as List<dynamic>?) ?? []).map((n) => n.toString()).toSet();
    final rawItems = (j['items'] as List<dynamic>?) ?? [];
    return SessionCommandsResult(
      vendor: j['vendor']?.toString() ?? '',
      items: rawItems.map((raw) {
        final item = caseInsensitive(raw as Map<String, dynamic>);
        final name = item['name']?.toString() ?? '';
        return ChatCapabilityItem(
          name: name,
          kind: item['kind']?.toString() ?? '',
          description: item['description']?.toString() ?? '',
          isRecentlyUsed: recentlyUsed.contains(name),
        );
      }).toList(),
      models: ((j['models'] as List<dynamic>?) ?? []).map((m) => m.toString()).toList(),
    );
  }
}
