import 'dart:async';

import 'package:flutter/material.dart';

import 'daemon/credentials_store.dart';
import 'daemon/daemon_client.dart';
import 'daemon/models.dart';
import 'pairing_screen.dart';

/// The phone's decision inbox — mirrors whatever task Aer.Daemon currently has open (typically
/// opened by the desktop first) and lets the user Approve/Reject a paused step or Cancel the run.
/// RetryWithRevision/Supersede aren't offered here: both need a way to move file content onto the
/// daemon host that this app doesn't have yet (deferred past Phase 2 — see daemon_client.dart).
class InboxScreen extends StatefulWidget {
  const InboxScreen({super.key});

  @override
  State<InboxScreen> createState() => _InboxScreenState();
}

class _InboxScreenState extends State<InboxScreen> {
  DaemonClient? _client;
  StreamSubscription<TaskProjection>? _subscription;
  TaskProjection? _projection;
  String? _connectionError;
  final _pendingStepIds = <String>{};

  @override
  void initState() {
    super.initState();
    _init();
  }

  Future<void> _init() async {
    final credentials = await CredentialsStore().load();
    if (credentials == null) {
      if (mounted) {
        Navigator.of(context).pushReplacement(MaterialPageRoute(builder: (_) => const PairingScreen()));
      }
      return;
    }
    _client = DaemonClient(
      host: credentials.host,
      token: credentials.token,
      tsnetRouted: credentials.tsnetRouted,
    );
    _connect();
  }

  void _connect() {
    _subscription?.cancel();
    setState(() => _connectionError = null);
    _subscription = _client!.watch().listen(
      (projection) {
        if (mounted) setState(() => _projection = projection);
      },
      onError: (Object error) {
        if (mounted) setState(() => _connectionError = 'Disconnected — $error');
      },
      onDone: () {
        if (mounted) setState(() => _connectionError ??= 'Disconnected from Aer.Daemon.');
      },
    );
  }

  @override
  void dispose() {
    _subscription?.cancel();
    super.dispose();
  }

  Future<void> _forgetPairing() async {
    await CredentialsStore().clear();
    if (!mounted) return;
    Navigator.of(context).pushReplacement(MaterialPageRoute(builder: (_) => const PairingScreen()));
  }

  Future<void> _pickRecentTask() async {
    final client = _client;
    if (client == null) return;
    try {
      final directories = await client.recentTasks();
      if (!mounted) return;
      final selected = await showModalBottomSheet<String>(
        context: context,
        builder: (context) => directories.isEmpty
            ? const Padding(padding: EdgeInsets.all(24), child: Text('No recent tasks on that host yet.'))
            : ListView(
                shrinkWrap: true,
                children: directories
                    .map((d) => ListTile(title: Text(d.split(RegExp(r'[\\/]')).last), subtitle: Text(d), onTap: () => Navigator.pop(context, d)))
                    .toList(),
              ),
      );
      if (selected != null) {
        await client.openTask(selected);
      }
    } on DaemonException catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  Future<void> _decide(WorkflowStepState step, String decisionType) async {
    final client = _client;
    final directoryPath = _projection?.directoryPath;
    final executionId = step.latestExecutionId;
    if (client == null || directoryPath == null || executionId == null) return;

    setState(() => _pendingStepIds.add(step.stepId));
    try {
      await client.decide(directoryPath: directoryPath, stepId: step.stepId, executionId: executionId, decisionType: decisionType);
      // The card vanishing as soon as the next WS snapshot lands (it drops off pausedSteps once
      // resolved) reads as "did that even work?" with no other feedback -- confirm explicitly.
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(decisionType == 'Reject' ? 'Rejected ${step.stepId}' : 'Approved ${step.stepId}')),
        );
      }
    } on DaemonException catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    } finally {
      if (mounted) setState(() => _pendingStepIds.remove(step.stepId));
    }
  }

  Future<void> _decideWithReference(WorkflowStepState step, String decisionType, String targetStepId, String fileName) async {
    final client = _client;
    final directoryPath = _projection?.directoryPath;
    final executionId = step.latestExecutionId;
    if (client == null || directoryPath == null || executionId == null) return;

    setState(() => _pendingStepIds.add(step.stepId));
    try {
      await client.decide(
        directoryPath: directoryPath,
        stepId: step.stepId,
        executionId: executionId,
        decisionType: decisionType,
        targetStepId: targetStepId,
        artifactReference: {
          'executionId': executionId,
          'fileName': fileName,
        },
      );
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text('Sent back to $targetStepId for revision')),
        );
      }
    } on DaemonException catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    } finally {
      if (mounted) setState(() => _pendingStepIds.remove(step.stepId));
    }
  }

  Future<void> _showTemplatePicker() async {
    final client = _client;
    if (client == null) return;

    try {
      final data = await client.listTemplates();
      if (!mounted) return;

      final templates = (data['templates'] as List<dynamic>?) ?? [];
      final vendors = (data['availableVendors'] as List<dynamic>?) ?? [];

      String selectedTemplateId = 'solo-run';
      String primaryVendor = 'claude';
      String secondaryVendor = 'gemini';
      final taskNameController = TextEditingController();
      final customPromptController = TextEditingController();

      final availableVendorNames = vendors
          .where((v) => (v as Map<String, dynamic>)['isAvailable'] == true)
          .map((v) => v['adapterName'].toString())
          .toList();
      if (availableVendorNames.isNotEmpty) {
        primaryVendor = availableVendorNames.first;
        secondaryVendor = availableVendorNames.length > 1 ? availableVendorNames[1] : primaryVendor;
      }

      await showDialog<void>(
        context: context,
        builder: (context) => StatefulBuilder(
          builder: (context, setDialogState) => AlertDialog(
            title: const Text('Start from Template'),
            content: SingleChildScrollView(
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Text('Select Workflow Template:', style: TextStyle(fontWeight: FontWeight.bold)),
                  ...templates.map((t) {
                    final map = caseInsensitive(t as Map<String, dynamic>);
                    final id = map['id'].toString();
                    final title = map['title'].toString();
                    final desc = map['description'].toString();
                    return RadioListTile<String>(
                      title: Text(title),
                      subtitle: Text(desc),
                      value: id,
                      // ignore: deprecated_member_use
                      groupValue: selectedTemplateId,
                      // ignore: deprecated_member_use
                      onChanged: (val) {
                        if (val != null) setDialogState(() => selectedTemplateId = val);
                      },
                    );
                  }),
                  const SizedBox(height: 12),
                  TextField(
                    controller: taskNameController,
                    decoration: const InputDecoration(labelText: 'Task Name (Optional)', hintText: 'e.g. my-new-task'),
                  ),
                  const SizedBox(height: 12),
                  TextField(
                    controller: customPromptController,
                    decoration: const InputDecoration(labelText: 'Custom Prompt (Optional)', hintText: 'Initial instructions for the worker'),
                  ),
                  const SizedBox(height: 12),
                  const Text('Worker CLI Vendor:', style: TextStyle(fontWeight: FontWeight.bold)),
                  DropdownButton<String>(
                    value: primaryVendor,
                    isExpanded: true,
                    items: (availableVendorNames.isEmpty ? ['claude', 'gemini'] : availableVendorNames)
                        .map((v) => DropdownMenuItem(
                              value: v,
                              child: Row(
                                children: [
                                  _buildVendorIcon(v, size: 16),
                                  const SizedBox(width: 8),
                                  Text(v),
                                ],
                              ),
                            ))
                        .toList(),
                    onChanged: (val) {
                      if (val != null) setDialogState(() => primaryVendor = val);
                    },
                  ),
                  if (selectedTemplateId == 'review-run') ...[
                    const SizedBox(height: 8),
                    const Text('Reviewer Worker CLI Vendor:', style: TextStyle(fontWeight: FontWeight.bold)),
                    DropdownButton<String>(
                      value: secondaryVendor,
                      isExpanded: true,
                      items: (availableVendorNames.isEmpty ? ['claude', 'gemini'] : availableVendorNames)
                          .map((v) => DropdownMenuItem(
                                value: v,
                                child: Row(
                                  children: [
                                    _buildVendorIcon(v, size: 16),
                                    const SizedBox(width: 8),
                                    Text(v),
                                  ],
                                ),
                              ))
                          .toList(),
                      onChanged: (val) {
                        if (val != null) setDialogState(() => secondaryVendor = val);
                      },
                    ),
                  ],
                ],
              ),
            ),
            actions: [
              TextButton(onPressed: () => Navigator.pop(context), child: const Text('Cancel')),
              FilledButton(
                onPressed: () async {
                  final messenger = ScaffoldMessenger.of(context);
                  Navigator.pop(context);
                  try {
                    final dirPath = await client.runTemplate(
                      templateId: selectedTemplateId,
                      primaryAdapter: primaryVendor,
                      secondaryAdapter: selectedTemplateId == 'review-run' ? secondaryVendor : null,
                      taskName: taskNameController.text.trim().isEmpty ? null : taskNameController.text.trim(),
                      customPrompt: customPromptController.text.trim().isEmpty ? null : customPromptController.text.trim(),
                    );
                    messenger.showSnackBar(
                      SnackBar(content: Text('Started template $selectedTemplateId ($dirPath)')),
                    );
                  } on DaemonException catch (e) {
                    messenger.showSnackBar(SnackBar(content: Text(e.message)));
                  }
                },
                child: const Text('Start Task'),
              ),
            ],
          ),
        ),
      );
    } on DaemonException catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  Future<void> _cancelRun() async {
    final client = _client;
    final directoryPath = _projection?.directoryPath;
    if (client == null || directoryPath == null) return;

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Cancel this run?'),
        content: const Text('This stops the whole task, not just one step.'),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Keep running')),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('Cancel run')),
        ],
      ),
    );
    if (confirmed != true) return;

    try {
      await client.cancelRun(directoryPath: directoryPath);
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Run cancelled')));
    } on DaemonException catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  @override
  Widget build(BuildContext context) {
    final projection = _projection;

    final taskTitle = projection?.directoryPath == null
        ? (projection?.workflowTemplateId ?? 'Aer')
        : projection!.directoryPath!.split(RegExp(r'[\\/]')).last;

    return Scaffold(
      appBar: AppBar(
        title: Text(projection == null ? 'Aer' : '$taskTitle — ${projection.status}'),
        actions: [
          IconButton(icon: const Icon(Icons.add), tooltip: 'Start from template', onPressed: _showTemplatePicker),
          IconButton(icon: const Icon(Icons.folder_open), tooltip: 'Recent tasks', onPressed: _pickRecentTask),
          PopupMenuButton<String>(
            onSelected: (value) {
              if (value == 'forget') _forgetPairing();
              if (value == 'cancel') _cancelRun();
              if (value == 'template') _showTemplatePicker();
            },
            itemBuilder: (context) => [
              const PopupMenuItem(value: 'template', child: Text('Start from template')),
              if (projection != null && projection.status == 'Running')
                const PopupMenuItem(value: 'cancel', child: Text('Cancel run')),
              const PopupMenuItem(value: 'forget', child: Text('Forget pairing')),
            ],
          ),
        ],
      ),
      body: _buildBody(context, projection),
    );
  }

  Widget _buildBody(BuildContext context, TaskProjection? projection) {
    if (_connectionError != null) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(_connectionError!, textAlign: TextAlign.center),
              const SizedBox(height: 16),
              FilledButton(onPressed: _connect, child: const Text('Reconnect')),
            ],
          ),
        ),
      );
    }

    if (projection == null) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              const Text('No task is open on the host yet.', textAlign: TextAlign.center),
              const SizedBox(height: 16),
              FilledButton.icon(
                icon: const Icon(Icons.add),
                label: const Text('Start from template'),
                onPressed: _showTemplatePicker,
              ),
              const SizedBox(height: 12),
              OutlinedButton(onPressed: _pickRecentTask, child: const Text('Browse recent tasks')),
            ],
          ),
        ),
      );
    }

    final pausedSteps = projection.pausedSteps;
    if (pausedSteps.isEmpty) {
      return Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text('Nothing is waiting on you — ${projection.status.toLowerCase()}.'),
            const SizedBox(height: 16),
            FilledButton.icon(
              icon: const Icon(Icons.add),
              label: const Text('Start another task from template'),
              onPressed: _showTemplatePicker,
            ),
          ],
        ),
      );
    }

    return ListView.builder(
      padding: const EdgeInsets.all(12),
      itemCount: pausedSteps.length,
      itemBuilder: (context, index) {
        final step = pausedSteps[index];
        return _PausedStepCard(
          client: _client!,
          directoryPath: projection.directoryPath,
          step: step,
          definition: projection.definitionFor(step.stepId),
          execution: projection.executionFor(step.latestExecutionId),
          workerAdapters: projection.workerAdapters,
          isPending: _pendingStepIds.contains(step.stepId),
          onApprove: () => _decide(step, 'Resume'),
          onReject: () => _decide(step, 'Reject'),
          onSendBack: (targetStepId, fileName) => _decideWithReference(step, 'Supersede', targetStepId, fileName),
        );
      },
    );
  }
}

class _PausedStepCard extends StatefulWidget {
  final DaemonClient client;
  final String? directoryPath;
  final WorkflowStepState step;
  final StepDefinition? definition;
  final ExecutionArtifacts? execution;
  final Map<String, String> workerAdapters;
  final bool isPending;
  final VoidCallback onApprove;
  final VoidCallback onReject;
  final Function(String targetStepId, String fileName)? onSendBack;

  const _PausedStepCard({
    required this.client,
    required this.directoryPath,
    required this.step,
    required this.definition,
    required this.execution,
    required this.workerAdapters,
    required this.isPending,
    required this.onApprove,
    required this.onReject,
    this.onSendBack,
  });

  @override
  State<_PausedStepCard> createState() => _PausedStepCardState();
}

class _PausedStepCardState extends State<_PausedStepCard> {
  bool _isLoadingPreview = false;
  String? _preview;

  Future<void> _loadPreview() async {
    final directoryPath = widget.directoryPath;
    final executionId = widget.step.latestExecutionId;
    final fileName = widget.execution?.outputFiles.firstOrNull;
    if (directoryPath == null || executionId == null || fileName == null || _preview != null) return;

    setState(() => _isLoadingPreview = true);
    try {
      final content = await widget.client.fetchArtifact(directoryPath: directoryPath, executionId: executionId, fileName: fileName);
      if (mounted) setState(() => _preview = content ?? '(no content)');
    } on DaemonException catch (e) {
      if (mounted) setState(() => _preview = 'Could not load preview: ${e.message}');
    } finally {
      if (mounted) setState(() => _isLoadingPreview = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final hasOutput = widget.execution?.outputFiles.isNotEmpty ?? false;
    final supersedeTarget = widget.definition?.supersedeTargets.firstOrNull;
    final outputFile = widget.execution?.outputFiles.firstOrNull ?? 'draft.md';

    final workerName = widget.definition?.worker ?? widget.step.stepId;
    final adapter = widget.workerAdapters[workerName];
    final titleText = adapter != null ? '$workerName ($adapter)' : workerName;

    return Card(
      margin: const EdgeInsets.only(bottom: 12),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                _buildVendorIcon(adapter),
                const SizedBox(width: 8),
                Expanded(child: Text(titleText, style: Theme.of(context).textTheme.titleMedium)),
              ],
            ),
            Text(widget.step.stepId, style: Theme.of(context).textTheme.bodySmall),
            if (hasOutput)
              ExpansionTile(
                tilePadding: EdgeInsets.zero,
                title: Text(widget.execution!.outputFiles.first),
                onExpansionChanged: (expanded) {
                  if (expanded) _loadPreview();
                },
                children: [
                  if (_isLoadingPreview) const Padding(padding: EdgeInsets.all(8), child: LinearProgressIndicator()),
                  if (_preview != null)
                    Container(
                      width: double.infinity,
                      padding: const EdgeInsets.all(8),
                      decoration: BoxDecoration(color: Theme.of(context).colorScheme.surfaceContainerHighest),
                      child: Text(_preview!, style: Theme.of(context).textTheme.bodySmall),
                    ),
                ],
              ),
            const SizedBox(height: 8),
            Row(
              mainAxisAlignment: MainAxisAlignment.end,
              children: [
                if (supersedeTarget != null && widget.onSendBack != null) ...[
                  OutlinedButton(
                    onPressed: widget.isPending ? null : () => widget.onSendBack!(supersedeTarget, outputFile),
                    child: Text('Send back to $supersedeTarget'),
                  ),
                  const SizedBox(width: 8),
                ],
                TextButton(onPressed: widget.isPending ? null : widget.onReject, child: const Text('Reject')),
                const SizedBox(width: 8),
                FilledButton(onPressed: widget.isPending ? null : widget.onApprove, child: const Text('Approve')),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

extension _FirstOrNull<T> on List<T> {
  T? get firstOrNull => isEmpty ? null : first;
}

Widget _buildVendorIcon(String? adapter, {double size = 18.0}) {
  final name = (adapter ?? '').toLowerCase();
  if (name.contains('claude')) {
    return Icon(Icons.psychology, size: size, color: Colors.deepOrangeAccent);
  }
  if (name.contains('gemini')) {
    return Icon(Icons.auto_awesome, size: size, color: Colors.indigoAccent);
  }
  if (name.contains('shell') || name.contains('stub')) {
    return Icon(Icons.terminal, size: size, color: Colors.grey);
  }
  return Icon(Icons.smart_toy, size: size, color: Colors.purpleAccent);
}
