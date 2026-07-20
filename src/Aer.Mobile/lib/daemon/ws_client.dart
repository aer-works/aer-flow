import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:tailscale/tailscale.dart';

import 'ws_codec.dart';

abstract interface class WsSocket {
  Stream<Uint8List> get input;
  Future<void> write(List<int> bytes);
  Future<void> close();
}

class TailscaleWsSocket implements WsSocket {
  final TailscaleConnection _connection;
  TailscaleWsSocket(this._connection);

  @override
  Stream<Uint8List> get input => _connection.input;

  @override
  Future<void> write(List<int> bytes) => _connection.output.write(bytes);

  @override
  Future<void> close() => _connection.close();
}

class TsnetWsChannel {
  final WsSocket _socket;
  final StreamController<String> _textController = StreamController<String>();
  final BytesBuilder _fragmentBuffer = BytesBuilder(copy: false);
  late final StreamSubscription<Uint8List> _subscription;
  final WsStreamParser _parser = WsStreamParser();

  TsnetWsChannel._(this._socket);

  Stream<String> get stream => _textController.stream;

  static Future<TsnetWsChannel> connect({
    required WsSocket socket,
    required String host,
    required String path,
  }) async {
    final channel = TsnetWsChannel._(socket);
    await channel._handshake(host: host, path: path);
    return channel;
  }

  Future<void> _handshake({
    required String host,
    required String path,
  }) async {
    final key = generateSecWebSocketKey();
    final expectedAcceptKey = computeSecWebSocketAccept(key);
    _parser.setExpectedAcceptKey(expectedAcceptKey);

    final reqStr = buildHandshakeRequest(
      host: host,
      path: path,
      secWebSocketKey: key,
    );
    final handshakeCompleter = Completer<void>();

    _subscription = _socket.input.listen(
      (chunk) {
        _parser.addBytes(chunk);
        if (!_parser.isHandshakeComplete) {
          try {
            if (_parser.tryParseHandshake()) {
              if (!handshakeCompleter.isCompleted) {
                handshakeCompleter.complete();
              }
            }
          } catch (e, st) {
            if (!handshakeCompleter.isCompleted) {
              handshakeCompleter.completeError(e, st);
            } else {
              _textController.addError(e, st);
            }
            close();
            return;
          }
        }

        if (_parser.isHandshakeComplete) {
          _drainFrames();
        }
      },
      onError: (Object error, StackTrace st) {
        if (!handshakeCompleter.isCompleted) {
          handshakeCompleter.completeError(error, st);
        } else {
          _textController.addError(error, st);
        }
      },
      onDone: () {
        if (!handshakeCompleter.isCompleted) {
          handshakeCompleter.completeError(
            StateError('Socket closed before WebSocket handshake completed'),
          );
        } else {
          _textController.close();
        }
      },
    );

    await _socket.write(utf8.encode(reqStr));
    await handshakeCompleter.future;
  }

  void _drainFrames() {
    while (true) {
      final frame = _parser.tryParseFrame();
      if (frame == null) break;

      switch (frame.opcode) {
        case 0x1: // Text
        case 0x0: // Continuation
          _fragmentBuffer.add(frame.payload);
          if (frame.fin) {
            final text = utf8.decode(_fragmentBuffer.takeBytes());
            _textController.add(text);
          }
          break;
        case 0x8: // Close
          close();
          break;
        case 0x9: // Ping
          final pong = encodeWsFrame(frame.payload, opcode: 0xA);
          _socket.write(pong);
          break;
        case 0xA: // Pong
          break;
      }
    }
  }

  Future<void> close() async {
    await _subscription.cancel();
    await _socket.close();
    if (!_textController.isClosed) {
      await _textController.close();
    }
  }
}
