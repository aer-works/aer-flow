import 'package:flutter/material.dart';

import 'daemon/daemon_client.dart';
import 'daemon/models.dart';

/// The fleet management screen (M24 Phase 5, #278) — every known task/session directory at once,
/// with archive/unarchive/delete. The Flutter counterpart of Aer.Ui's dedicated Tasks view. Reached
/// from InboxScreen's kebab menu ("Manage tasks"), distinct from `_pickRecentTask`'s bare recents
/// sheet, which stays the quick-reopen path — this screen is the real management surface.
class TasksScreen extends StatefulWidget {
  final DaemonClient client;

  const TasksScreen({super.key, required this.client});

  @override
  State<TasksScreen> createState() => _TasksScreenState();
}

class _TasksScreenState extends State<TasksScreen> {
  List<TaskFleetItem> _items = [];
  bool _includeArchived = false;
  bool _isLoading = true;
  String? _loadError;

  @override
  void initState() {
    super.initState();
    _refresh();
  }

  Future<void> _refresh() async {
    setState(() {
      _isLoading = true;
      _loadError = null;
    });

    try {
      final items = await widget.client.listTasks(includeArchived: _includeArchived);
      if (!mounted) return;
      setState(() => _items = items);
    } on DaemonException catch (e) {
      if (!mounted) return;
      setState(() => _loadError = e.message);
    } finally {
      if (mounted) setState(() => _isLoading = false);
    }
  }

  Future<void> _archive(TaskFleetItem item) async {
    try {
      await widget.client.archiveTask(item.taskDirectoryPath);
      await _refresh();
    } on DaemonException catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  Future<void> _unarchive(TaskFleetItem item) async {
    try {
      await widget.client.unarchiveTask(item.taskDirectoryPath);
      await _refresh();
    } on DaemonException catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  /// Reuses `_cancelRun`'s existing `showDialog` + `AlertDialog` confirm pattern
  /// (inbox_screen.dart) — mobile already has this precedent, unlike desktop, which has no
  /// modal-dialog infrastructure and uses an inline two-step confirm instead.
  Future<void> _delete(TaskFleetItem item) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Delete this task?'),
        content: Text('"${item.friendlyName}" will be permanently removed. This can\'t be undone.'),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Cancel')),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('Delete')),
        ],
      ),
    );
    if (confirmed != true) return;

    try {
      await widget.client.deleteTask(item.taskDirectoryPath);
      await _refresh();
    } on DaemonException catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Tasks'),
        actions: [
          IconButton(icon: const Icon(Icons.refresh), tooltip: 'Refresh', onPressed: _isLoading ? null : _refresh),
        ],
      ),
      body: Column(
        children: [
          SwitchListTile(
            title: const Text('Show archived'),
            value: _includeArchived,
            onChanged: (value) {
              setState(() => _includeArchived = value);
              _refresh();
            },
          ),
          if (_loadError != null)
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
              child: Text(_loadError!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
            ),
          if (_isLoading) const LinearProgressIndicator(),
          Expanded(
            child: _items.isEmpty && !_isLoading
                ? const Center(child: Text('No tasks or sessions yet.'))
                : ListView.builder(
                    itemCount: _items.length,
                    itemBuilder: (context, index) {
                      final item = _items[index];
                      return Card(
                        margin: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
                        child: Padding(
                          padding: const EdgeInsets.all(12),
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Row(
                                children: [
                                  Expanded(
                                    child: Text(item.friendlyName, style: const TextStyle(fontWeight: FontWeight.bold)),
                                  ),
                                  Text(item.typeLabel, style: Theme.of(context).textTheme.bodySmall),
                                  if (item.isArchived) ...[
                                    const SizedBox(width: 8),
                                    Text('archived', style: Theme.of(context).textTheme.bodySmall),
                                  ],
                                ],
                              ),
                              const SizedBox(height: 4),
                              Text(item.statusText),
                              if (item.pausedStepCount > 0)
                                Text('${item.pausedStepCount} step(s) awaiting a decision',
                                    style: Theme.of(context).textTheme.bodySmall),
                              Text(item.taskDirectoryPath, style: Theme.of(context).textTheme.bodySmall),
                              const SizedBox(height: 8),
                              Row(
                                mainAxisAlignment: MainAxisAlignment.end,
                                children: [
                                  if (!item.isArchived)
                                    TextButton(onPressed: () => _archive(item), child: const Text('Archive'))
                                  else
                                    TextButton(onPressed: () => _unarchive(item), child: const Text('Unarchive')),
                                  TextButton(onPressed: () => _delete(item), child: const Text('Delete')),
                                ],
                              ),
                            ],
                          ),
                        ),
                      );
                    },
                  ),
          ),
        ],
      ),
    );
  }
}
