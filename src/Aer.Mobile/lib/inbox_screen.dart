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

      String selectedTemplateId = 'chat-session';
      String primaryVendor = 'claude';
      String secondaryVendor = 'gemini';
      String? selectedProjectPath;
      List<Map<String, dynamic>> knownProjects = [];

      try {
        knownProjects = await client.listKnownProjects();
        if (knownProjects.isNotEmpty) {
          selectedProjectPath = knownProjects.first['Path']?.toString() ?? knownProjects.first['path']?.toString();
        }
      } catch (_) {}

      final taskNameController = TextEditingController();
      final customPromptController = TextEditingController();
      final secondaryCustomPromptController = TextEditingController();

      final availableVendorNames = vendors
          .where((v) => (v as Map<String, dynamic>)['isAvailable'] == true)
          .map((v) => v['adapterName'].toString())
          .toList();
      if (availableVendorNames.isNotEmpty) {
        primaryVendor = availableVendorNames.first;
        secondaryVendor = availableVendorNames.length > 1 ? availableVendorNames[1] : primaryVendor;
      }

      if (!mounted) return;

      await showDialog<void>(
        context: context,
        builder: (context) => StatefulBuilder(
          builder: (context, setDialogState) => AlertDialog(
            title: const Text('Start Task or Interactive Session'),
            content: SingleChildScrollView(
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Text('Select Task Type:', style: TextStyle(fontWeight: FontWeight.bold)),
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
                  if (selectedTemplateId == 'codebase-session') ...[
                    const SizedBox(height: 12),
                    const Text('Select Known Project Directory:', style: TextStyle(fontWeight: FontWeight.bold)),
                    if (knownProjects.isEmpty)
                      const Padding(
                        padding: EdgeInsets.symmetric(vertical: 8.0),
                        child: Text('No known projects registered on host yet.', style: TextStyle(color: Colors.grey, fontSize: 12)),
                      )
                    else
                      DropdownButton<String>(
                        value: selectedProjectPath,
                        isExpanded: true,
                        items: knownProjects.map((p) {
                          final name = p['FriendlyName']?.toString() ?? p['friendlyName']?.toString() ?? 'Project';
                          final path = p['Path']?.toString() ?? p['path']?.toString() ?? '';
                          return DropdownMenuItem<String>(
                            value: path,
                            child: Text('$name ($path)', overflow: TextOverflow.ellipsis),
                          );
                        }).toList(),
                        onChanged: (val) {
                          if (val != null) setDialogState(() => selectedProjectPath = val);
                        },
                      ),
                  ],
                  const SizedBox(height: 12),
                  TextField(
                    controller: taskNameController,
                    decoration: const InputDecoration(labelText: 'Task / Session Name (Optional)', hintText: 'e.g. my-chat-session'),
                  ),
                  const SizedBox(height: 12),
                  TextField(
                    controller: customPromptController,
                    decoration: const InputDecoration(labelText: 'Initial Message / Prompt (Optional)', hintText: 'Opening instructions or question'),
                  ),
                  const SizedBox(height: 12),
                  const Text('AI Vendor:', style: TextStyle(fontWeight: FontWeight.bold)),
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
                  if (selectedTemplateId == 'review-run' || selectedTemplateId == 'two-vendor-dialogue') ...[
                    const SizedBox(height: 8),
                    const Text('Secondary AI Vendor:', style: TextStyle(fontWeight: FontWeight.bold)),
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
                    const SizedBox(height: 12),
                    TextField(
                      controller: secondaryCustomPromptController,
                      decoration: const InputDecoration(
                        labelText: "Secondary Vendor Instructions (Optional)",
                        hintText: "Instructions for the secondary worker",
                      ),
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
                    if (selectedTemplateId == 'chat-session' || selectedTemplateId == 'codebase-session') {
                      final meta = await client.startSession(
                        adapter: primaryVendor,
                        workingDirectory: selectedTemplateId == 'codebase-session' ? selectedProjectPath : null,
                        initialMessage: customPromptController.text.trim().isEmpty ? null : customPromptController.text.trim(),
                        taskName: taskNameController.text.trim().isEmpty ? null : taskNameController.text.trim(),
                      );
                      messenger.showSnackBar(
                        SnackBar(content: Text('Started session ${meta["sessionId"] ?? meta["SessionId"]}')),
                      );
                    } else {
                      final dirPath = await client.runTemplate(
                        templateId: selectedTemplateId,
                        primaryAdapter: primaryVendor,
                        secondaryAdapter: (selectedTemplateId == 'review-run' || selectedTemplateId == 'two-vendor-dialogue') ? secondaryVendor : null,
                        taskName: taskNameController.text.trim().isEmpty ? null : taskNameController.text.trim(),
                        customPrompt: customPromptController.text.trim().isEmpty ? null : customPromptController.text.trim(),
                        secondaryCustomPrompt: (selectedTemplateId == 'review-run' || selectedTemplateId == 'two-vendor-dialogue') && secondaryCustomPromptController.text.trim().isNotEmpty
                            ? secondaryCustomPromptController.text.trim()
                            : null,
                      );
                      messenger.showSnackBar(
                        SnackBar(content: Text('Started task ($dirPath)')),
                      );
                    }
                  } on DaemonException catch (e) {
                    messenger.showSnackBar(SnackBar(content: Text(e.message)));
                  }
                },
                child: const Text('Start'),
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

/// M22 review follow-up (issue #250): a real vendor glyph instead of a stock Material icon standing
/// in for it. Same silhouette and brand-color pairing as desktop's `Icon.Vendor.Claude`/`.Gemini` in
/// `Theme/Icons.axaml` (6-point sunburst vs. 4-point sparkle — a distinct point count so the two
/// read apart without color alone), so the two clients agree on what a vendor "looks like". Only
/// recognizes the vendors `VendorCliPresence` actually probes for (`claude`, `gemini`); anything
/// else falls back to a plain neutral dot rather than inventing icon branches for adapter names
/// ("shell", "stub", "codex", "openai") this codebase never registers.
Widget _buildVendorIcon(String? adapter, {double size = 18.0}) {
  final name = (adapter ?? '').toLowerCase();
  if (name.contains('claude')) {
    return CustomPaint(size: Size(size, size), painter: _VendorGlyphPainter(_VendorGlyph.claude));
  }
  if (name.contains('gemini')) {
    return CustomPaint(size: Size(size, size), painter: _VendorGlyphPainter(_VendorGlyph.gemini));
  }
  return CustomPaint(size: Size(size, size), painter: _VendorGlyphPainter(_VendorGlyph.generic));
}

enum _VendorGlyph { claude, gemini, generic }

class _VendorGlyphPainter extends CustomPainter {
  const _VendorGlyphPainter(this.glyph);

  final _VendorGlyph glyph;

  static const _claudeColor = Color(0xFFD97757);
  static const _geminiColor = Color(0xFF4285F4);

  @override
  void paint(Canvas canvas, Size size) {
    if (glyph == _VendorGlyph.generic) {
      final paint = Paint()..color = Colors.grey;
      canvas.drawCircle(size.center(Offset.zero), size.shortestSide * 0.28, paint);
      return;
    }

    // Points authored on Aer.Ui's 16x16 icon grid, then scaled to this glyph's actual size —
    // identical coordinates to Icon.Vendor.Claude/.Gemini in Theme/Icons.axaml.
    final points = glyph == _VendorGlyph.claude
        ? const [
            Offset(8, 2), Offset(9.2, 5.9), Offset(13.2, 5), Offset(10.4, 8),
            Offset(13.2, 11), Offset(9.2, 10.1), Offset(8, 14), Offset(6.8, 10.1),
            Offset(2.8, 11), Offset(5.6, 8), Offset(2.8, 5), Offset(6.8, 5.9),
          ]
        : const [
            Offset(8, 1.5), Offset(9.4, 6.6), Offset(14.5, 8), Offset(9.4, 9.4),
            Offset(8, 14.5), Offset(6.6, 9.4), Offset(1.5, 8), Offset(6.6, 6.6),
          ];

    final scale = size.shortestSide / 16.0;
    final path = Path()..addPolygon(points.map((p) => p * scale).toList(), true);
    final paint = Paint()
      ..color = glyph == _VendorGlyph.claude ? _claudeColor : _geminiColor
      ..style = PaintingStyle.fill;
    canvas.drawPath(path, paint);
  }

  @override
  bool shouldRepaint(covariant _VendorGlyphPainter oldDelegate) => oldDelegate.glyph != glyph;
}
