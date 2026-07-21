import 'dart:async';

import 'package:flutter/material.dart';

import 'daemon/daemon_client.dart';
import 'daemon/models.dart';

/// One rendered row in the chat transcript — a human turn or an assistant response, never both.
/// Mirrors Aer.Ui.Core's ChatMessageViewModel (see ChatViewModel.cs).
class _ChatMessage {
  final String senderLabel;
  final String text;
  final bool isFromUser;

  _ChatMessage({required this.senderLabel, required this.text, required this.isFromUser});
}

/// The mobile chat/codebase-session screen (M24, issue #262) — the Flutter counterpart of
/// Aer.Ui's dedicated Chat view. `Turns` (the actual message content) live outside TaskProjection
/// entirely, in SessionMetadata, so this screen re-fetches GET /api/sessions/{sessionId} rather
/// than reading anything off InboxScreen's projection.
///
/// Unlike desktop (which polls `.aer/session.json` off disk on a 2-second timer, since it's the
/// same machine), this phone has no filesystem access to the daemon host — it instead re-fetches
/// on every filtered `/api/ws` push for [directoryPath], which the daemon already sends whenever a
/// turn completes (Aer.Daemon.Program's ExecuteSessionTurnAsync calls BroadcastStateAsync through
/// the same session/DecideAsync path every other run uses).
///
/// Navigation into this screen only ever happens from an explicit local action (starting a
/// session here, or tapping an inbox card for a session this phone already has open) — never
/// automatically off an incoming WS push, so a different client starting its own session can't
/// yank this phone into a chat it didn't ask to view.
class ChatScreen extends StatefulWidget {
  final DaemonClient client;
  final String sessionId;
  final String directoryPath;

  const ChatScreen({super.key, required this.client, required this.sessionId, required this.directoryPath});

  @override
  State<ChatScreen> createState() => _ChatScreenState();
}

class _ChatScreenState extends State<ChatScreen> {
  final _inputController = TextEditingController();
  final _scrollController = ScrollController();

  StreamSubscription<TaskProjection>? _projectionSubscription;
  StreamSubscription<SessionProgressEvent>? _progressSubscription;
  Timer? _sendTimeoutTimer;

  SessionMetadata? _metadata;
  bool _isLoading = true;
  String? _loadError;
  String? _sendError;

  bool _isSending = false;
  String? _pendingUserMessage;
  int _turnsCountAtSendTime = 0;
  String _liveProgressText = '';

  @override
  void initState() {
    super.initState();
    _refresh();
    _projectionSubscription = widget.client.watch().listen((projection) {
      if (!mounted) return;
      if (projection.directoryPath == widget.directoryPath) {
        _refresh();
      }
    });
    _progressSubscription = widget.client.watchProgress().listen((event) {
      if (!mounted) return;
      if (event.directoryPath == widget.directoryPath && _isSending) {
        setState(() => _liveProgressText += event.text);
      }
    });
  }

  @override
  void dispose() {
    _projectionSubscription?.cancel();
    _progressSubscription?.cancel();
    _sendTimeoutTimer?.cancel();
    _inputController.dispose();
    _scrollController.dispose();
    super.dispose();
  }

  Future<void> _refresh() async {
    try {
      final metadata = await widget.client.getSession(widget.sessionId);
      if (!mounted) return;
      setState(() {
        _metadata = metadata;
        _isLoading = false;
        _loadError = null;

        if (_isSending && metadata.turnCount > _turnsCountAtSendTime) {
          _isSending = false;
          _liveProgressText = '';
          _pendingUserMessage = null;
          _sendTimeoutTimer?.cancel();
        }
      });
      _scrollToEnd();
    } on DaemonException catch (e) {
      if (mounted) setState(() { _isLoading = false; _loadError = e.message; });
    }
  }

  void _scrollToEnd() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!_scrollController.hasClients) return;
      _scrollController.animateTo(
        _scrollController.position.maxScrollExtent,
        duration: const Duration(milliseconds: 200),
        curve: Curves.easeOut,
      );
    });
  }

  Future<void> _send() async {
    final message = _inputController.text.trim();
    final metadata = _metadata;
    if (message.isEmpty || metadata == null || _isSending) return;

    setState(() {
      _turnsCountAtSendTime = metadata.turnCount;
      _pendingUserMessage = message;
      _liveProgressText = '';
      _isSending = true;
      _sendError = null;
      _inputController.clear();
    });
    _scrollToEnd();

    // The daemon runs a turn fire-and-forget in the background and never reports failure back to
    // any client (Aer.Daemon.Program's /api/sessions/send handler only logs to Console.Error) — a
    // client-side timeout is the only thing that stops this screen spinning forever if that
    // background task dies silently or a WS push never arrives.
    _sendTimeoutTimer?.cancel();
    _sendTimeoutTimer = Timer(const Duration(minutes: 5), () {
      if (mounted && _isSending) {
        setState(() {
          _isSending = false;
          _sendError = 'No response after 5 minutes — the session may still be working in the background.';
        });
      }
    });

    try {
      await widget.client.sendSessionMessage(sessionId: widget.sessionId, message: message);
    } on DaemonException catch (e) {
      _sendTimeoutTimer?.cancel();
      if (mounted) {
        setState(() {
          _isSending = false;
          _pendingUserMessage = null;
          _sendError = e.message;
        });
      }
    }
  }

  List<_ChatMessage> _buildMessages(SessionMetadata metadata) {
    final messages = <_ChatMessage>[];
    for (final turn in metadata.turns) {
      messages.add(_ChatMessage(senderLabel: 'You', text: turn.humanMessage, isFromUser: true));
      if (turn.assistantResponse != null) {
        messages.add(_ChatMessage(senderLabel: turn.vendor, text: turn.assistantResponse!, isFromUser: false));
      }
    }
    if (_isSending && _pendingUserMessage != null) {
      messages.add(_ChatMessage(senderLabel: 'You', text: _pendingUserMessage!, isFromUser: true));
    }
    return messages;
  }

  @override
  Widget build(BuildContext context) {
    final metadata = _metadata;
    final title = metadata == null
        ? widget.directoryPath.split(RegExp(r'[\\/]')).last
        : '${metadata.currentAdapter} — turn ${metadata.turnCount}';

    return Scaffold(
      appBar: AppBar(title: Text(title)),
      body: Column(
        children: [
          Expanded(child: _buildBody(context, metadata)),
          if (_isSending && _liveProgressText.isNotEmpty)
            Container(
              width: double.infinity,
              padding: const EdgeInsets.all(12),
              color: Theme.of(context).colorScheme.surfaceContainerHighest,
              child: Text(_liveProgressText, style: Theme.of(context).textTheme.bodySmall),
            ),
          if (_sendError != null)
            Container(
              width: double.infinity,
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
              color: Theme.of(context).colorScheme.errorContainer,
              child: Text(_sendError!, style: TextStyle(color: Theme.of(context).colorScheme.onErrorContainer)),
            ),
          SafeArea(
            top: false,
            child: Padding(
              padding: const EdgeInsets.all(8),
              child: Row(
                children: [
                  Expanded(
                    child: TextField(
                      controller: _inputController,
                      minLines: 1,
                      maxLines: 5,
                      textInputAction: TextInputAction.newline,
                      decoration: const InputDecoration(hintText: 'Message', border: OutlineInputBorder()),
                      enabled: !_isSending,
                    ),
                  ),
                  const SizedBox(width: 8),
                  _isSending
                      ? const SizedBox(width: 48, height: 48, child: Padding(padding: EdgeInsets.all(12), child: CircularProgressIndicator(strokeWidth: 2)))
                      : IconButton.filled(icon: const Icon(Icons.send), onPressed: _send),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildBody(BuildContext context, SessionMetadata? metadata) {
    if (_isLoading) {
      return const Center(child: CircularProgressIndicator());
    }
    if (_loadError != null) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(_loadError!, textAlign: TextAlign.center),
              const SizedBox(height: 16),
              FilledButton(onPressed: _refresh, child: const Text('Retry')),
            ],
          ),
        ),
      );
    }
    if (metadata == null) {
      return const SizedBox.shrink();
    }

    final messages = _buildMessages(metadata);
    return ListView.builder(
      controller: _scrollController,
      padding: const EdgeInsets.all(12),
      itemCount: messages.length,
      itemBuilder: (context, index) => _MessageBubble(message: messages[index]),
    );
  }
}

class _MessageBubble extends StatelessWidget {
  final _ChatMessage message;

  const _MessageBubble({required this.message});

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final background = message.isFromUser ? scheme.primaryContainer : scheme.surfaceContainerHighest;
    final foreground = message.isFromUser ? scheme.onPrimaryContainer : scheme.onSurfaceVariant;

    return Align(
      alignment: message.isFromUser ? Alignment.centerRight : Alignment.centerLeft,
      child: Container(
        constraints: BoxConstraints(maxWidth: MediaQuery.of(context).size.width * 0.8),
        margin: const EdgeInsets.symmetric(vertical: 4),
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(color: background, borderRadius: BorderRadius.circular(12)),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(message.senderLabel, style: TextStyle(fontSize: 12, fontWeight: FontWeight.bold, color: foreground)),
            const SizedBox(height: 4),
            Text(message.text, style: TextStyle(color: foreground)),
          ],
        ),
      ),
    );
  }
}
