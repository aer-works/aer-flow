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
    _client = DaemonClient(host: credentials.host, token: credentials.token);
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

    return Scaffold(
      appBar: AppBar(
        title: Text(projection == null ? 'Aer' : '${projection.workflowTemplateId} — ${projection.status}'),
        actions: [
          IconButton(icon: const Icon(Icons.folder_open), tooltip: 'Recent tasks', onPressed: _pickRecentTask),
          PopupMenuButton<String>(
            onSelected: (value) {
              if (value == 'forget') _forgetPairing();
              if (value == 'cancel') _cancelRun();
            },
            itemBuilder: (context) => [
              if (projection != null) const PopupMenuItem(value: 'cancel', child: Text('Cancel run')),
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
              const Text('No task is open on the desktop yet.', textAlign: TextAlign.center),
              const SizedBox(height: 16),
              OutlinedButton(onPressed: _pickRecentTask, child: const Text('Browse recent tasks')),
            ],
          ),
        ),
      );
    }

    final pausedSteps = projection.pausedSteps;
    if (pausedSteps.isEmpty) {
      return Center(child: Text('Nothing is waiting on you — ${projection.status.toLowerCase()}.'));
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
          isPending: _pendingStepIds.contains(step.stepId),
          onApprove: () => _decide(step, 'Resume'),
          onReject: () => _decide(step, 'Reject'),
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
  final bool isPending;
  final VoidCallback onApprove;
  final VoidCallback onReject;

  const _PausedStepCard({
    required this.client,
    required this.directoryPath,
    required this.step,
    required this.definition,
    required this.execution,
    required this.isPending,
    required this.onApprove,
    required this.onReject,
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

    return Card(
      margin: const EdgeInsets.only(bottom: 12),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(widget.definition?.worker ?? widget.step.stepId, style: Theme.of(context).textTheme.titleMedium),
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
